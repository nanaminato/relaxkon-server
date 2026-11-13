using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using RelaxKon.Auth.Application.Media;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Application.Analytics;

public sealed record OperationsSample(string Day, string Feature, string Model, string Endpoint,
    int Status, int Milliseconds, int? AccountId);

public sealed record OperationsMetrics(long Requests, long Errors, int ActiveUsers, int P95Milliseconds);

public sealed class OperationsAnalytics(IConfiguration configuration, ILogger<OperationsAnalytics> logger) : BackgroundService
{
    private const int RetentionDays = 30;
    private readonly Channel<OperationsSample> queue = Channel.CreateBounded<OperationsSample>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly string directory = configuration["Analytics:Directory"] ??
        (configuration["DataProtection:KeyPath"] is { Length: > 0 } keys
            ? Path.Combine(Path.GetFullPath(keys), "..", "analytics")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKon", "analytics"));
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Day(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public void Record(OperationsSample sample) => queue.Writer.TryWrite(sample);

    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "operations.sqlite"),
        DefaultTimeout = 3
    }.ToString());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            Directory.CreateDirectory(directory);
            await using var connection = Connection();
            await connection.OpenAsync(stoppingToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Samples (
                    Day TEXT NOT NULL, Feature TEXT NOT NULL, Model TEXT NOT NULL, Endpoint TEXT NOT NULL,
                    Status INTEGER NOT NULL, Milliseconds INTEGER NOT NULL, AccountId INTEGER
                );
                CREATE INDEX IF NOT EXISTS IX_Samples_Day ON Samples(Day);
                """;
            await command.ExecuteNonQueryAsync(stoppingToken);
            var lastPruned = "";
            async Task Prune()
            {
                var today = Day(DateTime.UtcNow);
                if (lastPruned == today) return;
                command.Parameters.Clear();
                command.CommandText = "DELETE FROM Samples WHERE Day < $start";
                command.Parameters.AddWithValue("$start", Day(DateTime.UtcNow.Date.AddDays(1 - RetentionDays)));
                await command.ExecuteNonQueryAsync(stoppingToken);
                lastPruned = today;
            }
            await Prune();
            ready.TrySetResult();
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Prune();
                await using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO Samples VALUES ($day,$feature,$model,$endpoint,$status,$ms,$account)";
                foreach (var key in new[] { "$day", "$feature", "$model", "$endpoint", "$status", "$ms", "$account" })
                    command.Parameters.AddWithValue(key, DBNull.Value);
                for (var index = 0; index < 128 && queue.Reader.TryRead(out var sample); index++)
                {
                    command.Parameters["$day"].Value = sample.Day;
                    command.Parameters["$feature"].Value = sample.Feature;
                    command.Parameters["$model"].Value = sample.Model;
                    command.Parameters["$endpoint"].Value = sample.Endpoint;
                    command.Parameters["$status"].Value = sample.Status;
                    command.Parameters["$ms"].Value = sample.Milliseconds;
                    command.Parameters["$account"].Value = (object?)sample.AccountId ?? DBNull.Value;
                    await command.ExecuteNonQueryAsync(stoppingToken);
                }
                await transaction.CommitAsync(stoppingToken);
                command.Transaction = null;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            ready.TrySetException(new InvalidOperationException("Operations analytics storage is unavailable."));
            logger.LogError("Operations analytics storage failed. ErrorType: {ErrorType}", error.GetType().Name);
        }
    }

    public async Task<object> Read(int days, string? feature, string? model, string? endpoint, CancellationToken token)
    {
        await ready.Task.WaitAsync(token);
        var today = DateTime.UtcNow.Date;
        var start = today.AddDays(1 - days);
        await using var connection = Connection();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Day,Feature,Model,Endpoint,Status,Milliseconds,AccountId
            FROM Samples WHERE Day BETWEEN $start AND $end
            """;
        command.Parameters.AddWithValue("$start", Day(start));
        command.Parameters.AddWithValue("$end", Day(today));
        await using var reader = await command.ExecuteReaderAsync(token);
        var samples = new List<OperationsSample>();
        while (await reader.ReadAsync(token))
            samples.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        var filtered = samples.Where(sample =>
            (string.IsNullOrEmpty(feature) || sample.Feature == feature) &&
            (string.IsNullOrEmpty(model) || sample.Model == model) &&
            (string.IsNullOrEmpty(endpoint) || sample.Endpoint == endpoint)).ToArray();
        var byDay = filtered.ToLookup(sample => sample.Day);
        return new
        {
            Summary = Metrics(filtered),
            Trend = Enumerable.Range(0, days).Select(index => Day(start.AddDays(index)))
                .Select(day => new { Day = day, Metrics = Metrics(byDay[day]) }).ToArray(),
            Features = Breakdown(filtered, sample => sample.Feature),
            Models = Breakdown(filtered.Where(sample => sample.Model.Length != 0), sample => sample.Model),
            Endpoints = Breakdown(filtered, sample => sample.Endpoint),
            Errors = filtered.Where(sample => sample.Status >= 400).GroupBy(sample => sample.Status)
                .OrderBy(group => group.Key).Select(group => new { Status = group.Key, Count = group.Count() }).ToArray(),
            Filters = new
            {
                Features = Choices(samples, sample => sample.Feature),
                Models = Choices(samples, sample => sample.Model),
                Endpoints = Choices(samples, sample => sample.Endpoint)
            }
        };
    }

    private static string[] Choices(IEnumerable<OperationsSample> samples, Func<OperationsSample, string> key) =>
        samples.Select(key).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static object[] Breakdown(IEnumerable<OperationsSample> samples, Func<OperationsSample, string> key) =>
        samples.GroupBy(key).Select(group => new { Key = group.Key, Metrics = Metrics(group) })
            .OrderByDescending(row => row.Metrics.Requests).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();

    private static OperationsMetrics Metrics(IEnumerable<OperationsSample> source)
    {
        var samples = source.ToArray();
        var durations = samples.Select(sample => sample.Milliseconds).Order().ToArray();
        return new(samples.Length, samples.Count(sample => sample.Status >= 400),
            samples.Where(sample => sample.AccountId.HasValue).Select(sample => sample.AccountId).Distinct().Count(),
            durations.Length == 0 ? 0 : durations[(int)Math.Ceiling(durations.Length * .95) - 1]);
    }
}

public sealed class OperationsAnalyticsMiddleware(RequestDelegate next, OperationsAnalytics analytics, QuickModel models)
{
    private static readonly object ModelKey = new();
    private static readonly object ErrorKey = new();

    public static void SetModel(HttpContext? context, string? model)
    {
        if (context is not null && model is { Length: > 0 and <= 200 }) context.Items[ModelKey] = model;
    }

    public static void MarkError(HttpContext? context)
    {
        if (context is not null) context.Items[ErrorKey] = true;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        var route = endpoint?.RoutePattern.RawText;
        if (route is null || !route.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith("api/admin/operations", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var feature = endpoint!.Metadata.GetMetadata<FeatureAccessAttribute>()?.Key;
        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try { await next(context); }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var model = context.Items.Remove(ModelKey, out var value) && value is string candidate &&
                (models.IsModelAvailable(candidate) || APIMartImagePolicy.Models.Contains(candidate, StringComparer.Ordinal))
                ? candidate : "";
            var status = context.RequestAborted.IsCancellationRequested ? 499 :
                failed || context.Items.ContainsKey(ErrorKey) ? 500 : context.Response.StatusCode;
            var account = feature is not null && status is >= 200 and < 300 &&
                feature is not ("image-task" or "image-proxy") ? FeatureIdentity.AccountId(context.User) : null;
            analytics.Record(new(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                feature ?? (route.StartsWith("api/sync/", StringComparison.OrdinalIgnoreCase) ? "sync" : "api"),
                model, context.Request.Method + " /" + route, status,
                (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds), account));
        }
    }
}
