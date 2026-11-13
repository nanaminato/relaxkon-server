using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using RelaxKon.Auth.Application.Media;
using RelaxKon.Auth.Application.Routing;
using RelaxKon.Auth.Security.FeatureAccess;

namespace RelaxKon.Auth.Application.Analytics;

public sealed record OperationsSample(string Day, string Feature, string Model, string Endpoint,
    int Status, int Milliseconds, int? AccountId, long? InputTokens = null, long? OutputTokens = null,
    int Images = 0, string ErrorKind = "", string SizeBucket = "", string? EventKey = null);

public sealed record OperationsMetrics(long Requests, long Errors, int ActiveUsers, int P95Milliseconds,
    long InputTokens, long OutputTokens, int TokenReports, long Images, int Successes);

public sealed class OperationsAnalytics(IConfiguration configuration, ILogger<OperationsAnalytics> logger) : BackgroundService
{
    private const int RetentionDays = 37;
    private readonly Channel<OperationsSample> queue = Channel.CreateBounded<OperationsSample>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly string directory = configuration["Analytics:Directory"] ??
        (configuration["DataProtection:KeyPath"] is { Length: > 0 } keys
            ? Path.Combine(Path.GetFullPath(keys), "..", "analytics")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKon", "analytics"));
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Day(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public void Record(OperationsSample sample) => queue.Writer.TryWrite(sample);

    internal static string TaskKey(string taskId) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(taskId)));

    internal async Task<string> ImageTaskModel(string taskKey, CancellationToken token)
    {
        if (!ready.Task.IsCompletedSuccessfully) return "";
        try
        {
            await using var connection = Connection();
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Model FROM Samples WHERE EventKey = $key";
            command.Parameters.AddWithValue("$key", "image-submission:" + taskKey);
            return await command.ExecuteScalarAsync(token) as string ?? "";
        }
        catch (SqliteException) { return ""; }
    }

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
            await Upgrade(command, stoppingToken);
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
                command.CommandText = """
                    INSERT OR IGNORE INTO Samples
                    (Day,Feature,Model,Endpoint,Status,Milliseconds,AccountId,InputTokens,OutputTokens,Images,ErrorKind,SizeBucket,EventKey)
                    VALUES ($day,$feature,$model,$endpoint,$status,$ms,$account,$input,$output,$images,$error,$size,$event)
                    """;
                foreach (var key in new[] { "$day", "$feature", "$model", "$endpoint", "$status", "$ms", "$account", "$input", "$output", "$images", "$error", "$size", "$event" })
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
                    command.Parameters["$input"].Value = (object?)sample.InputTokens ?? DBNull.Value;
                    command.Parameters["$output"].Value = (object?)sample.OutputTokens ?? DBNull.Value;
                    command.Parameters["$images"].Value = sample.Images;
                    command.Parameters["$error"].Value = sample.ErrorKind;
                    command.Parameters["$size"].Value = sample.SizeBucket;
                    command.Parameters["$event"].Value = (object?)sample.EventKey ?? DBNull.Value;
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

    private static async Task Upgrade(SqliteCommand command, CancellationToken token)
    {
        command.CommandText = "PRAGMA table_info(Samples)";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) existing.Add(reader.GetString(1));
        foreach (var (name, type) in new (string, string)[]
        {
            ("InputTokens", "INTEGER"), ("OutputTokens", "INTEGER"), ("Images", "INTEGER NOT NULL DEFAULT 0"),
            ("ErrorKind", "TEXT NOT NULL DEFAULT ''"), ("SizeBucket", "TEXT NOT NULL DEFAULT ''"), ("EventKey", "TEXT")
        })
        {
            if (existing.Contains(name)) continue;
            command.CommandText = $"ALTER TABLE Samples ADD COLUMN {name} {type}";
            await command.ExecuteNonQueryAsync(token);
        }
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Samples_EventKey ON Samples(EventKey) WHERE EventKey IS NOT NULL";
        await command.ExecuteNonQueryAsync(token);
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
            SELECT Day,Feature,Model,Endpoint,Status,Milliseconds,AccountId,InputTokens,OutputTokens,Images,ErrorKind,SizeBucket
            FROM Samples WHERE Day BETWEEN $start AND $end
            """;
        command.Parameters.AddWithValue("$start", Day(start));
        command.Parameters.AddWithValue("$end", Day(today));
        await using var reader = await command.ExecuteReaderAsync(token);
        var samples = new List<OperationsSample>();
        while (await reader.ReadAsync(token))
            samples.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.GetInt32(9), reader.GetString(10), reader.GetString(11)));
        samples.AddRange(await EdgeOperationsLog.Read(configuration["Analytics:EdgeLogPath"], start, today, token));
        var registrations = samples.Where(sample => sample.Feature == "registration-cohort" && sample.AccountId.HasValue).ToArray();
        var activity = samples.Where(IsActive).ToLookup(sample => sample.AccountId!.Value, sample => sample.Day);
        var cohorts = registrations.GroupBy(sample => sample.AccountId!.Value).Select(group => group.MinBy(sample => sample.Day)!)
            .ToLookup(sample => sample.Day);
        var cohortRows = Enumerable.Range(0, days).Select(index => start.AddDays(index)).Select(day =>
        {
            var key = Day(day);
            var users = cohorts[key].Select(sample => sample.AccountId!.Value).ToArray();
            int? Retained(int offset) => day.AddDays(offset) >= today ? null :
                users.Count(id => activity[id].Contains(Day(day.AddDays(offset))));
            return new { Day = key, Registrations = users.Length,
                Activated = users.Count(id => activity[id].Any(value => string.CompareOrdinal(value, key) >= 0)),
                Day1 = Retained(1), Day7 = Retained(7) };
        }).ToArray();
        var visible = samples.Where(sample => sample.Feature is not ("registration-cohort" or "image-submission")).ToArray();
        var filtered = visible.Where(sample =>
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
            ErrorDetails = filtered.Where(sample => sample.Status >= 400)
                .GroupBy(sample => new { sample.Endpoint, sample.Status, Kind = ErrorCategory(sample) })
                .Select(group => new { group.Key.Endpoint, group.Key.Status, group.Key.Kind, Count = group.Count() })
                .OrderByDescending(row => row.Count).ThenBy(row => row.Endpoint).ThenBy(row => row.Status).ThenBy(row => row.Kind).ToArray(),
            SyncSizes = filtered.Where(sample => sample.Feature == "sync" && sample.SizeBucket.Length != 0)
                .GroupBy(sample => new { sample.Endpoint, Bucket = sample.SizeBucket })
                .Select(group => new { group.Key.Endpoint, group.Key.Bucket, Metrics = Metrics(group) })
                .OrderBy(row => row.Endpoint).ThenBy(row => row.Bucket).ToArray(),
            Cohorts = cohortRows,
            EdgeLogConfigured = !string.IsNullOrWhiteSpace(configuration["Analytics:EdgeLogPath"]),
            Filters = new
            {
                Features = Choices(visible, sample => sample.Feature),
                Models = Choices(visible, sample => sample.Model),
                Endpoints = Choices(visible, sample => sample.Endpoint)
            }
        };
    }

    private static string[] Choices(IEnumerable<OperationsSample> samples, Func<OperationsSample, string> key) =>
        samples.Select(key).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static object[] Breakdown(IEnumerable<OperationsSample> samples, Func<OperationsSample, string> key) =>
        samples.GroupBy(key).Select(group => new { Key = group.Key, Metrics = Metrics(group) })
            .OrderByDescending(row => row.Metrics.Requests).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();

    private static bool IsActive(OperationsSample sample) => sample.AccountId.HasValue &&
        sample.Status is >= 200 and < 300 && sample.Feature is not ("registration-cohort" or "image-result" or "image-submission");

    private static string ErrorCategory(OperationsSample sample) => sample.ErrorKind.Length != 0 ? sample.ErrorKind : sample.Status switch
    {
        413 => "tooLarge", 429 => "rateLimited", 499 => "cancelled", 504 => "timeout",
        502 or 503 => "upstream", >= 500 => "server", _ => "client"
    };

    private static OperationsMetrics Metrics(IEnumerable<OperationsSample> source)
    {
        var samples = source.ToArray();
        var requests = samples.Where(sample => sample.EventKey?.StartsWith("image-result:", StringComparison.Ordinal) != true).ToArray();
        var durations = requests.Select(sample => sample.Milliseconds).Order().ToArray();
        return new(requests.Length, requests.Count(sample => sample.Status >= 400),
            samples.Where(IsActive).Select(sample => sample.AccountId).Distinct().Count(),
            durations.Length == 0 ? 0 : durations[(int)Math.Ceiling(durations.Length * .95) - 1],
            samples.Sum(sample => sample.InputTokens ?? 0), samples.Sum(sample => sample.OutputTokens ?? 0),
            samples.Count(sample => sample.InputTokens.HasValue || sample.OutputTokens.HasValue),
            samples.Sum(sample => (long)sample.Images), requests.Count(sample => sample.Status is >= 200 and < 300));
    }
}

public sealed class OperationsAnalyticsMiddleware(RequestDelegate next, OperationsAnalytics analytics, QuickModel models)
{
    private static readonly object ModelKey = new();
    private static readonly object ErrorKey = new();
    private static readonly object UsageKey = new();
    internal sealed record Usage(long? Input, long? Output, int Images);
    public static void SetUsage(HttpContext? context, long? input, long? output, int images = 0)
    {
        if (context is not null) context.Items[UsageKey] = new Usage(input, output, images);
    }

    internal static string SizeBucket(long? bytes) => bytes switch
    {
        null => "unknown", < 64 * 1024 => "lt64KiB", < 1024 * 1024 => "64KiBTo1MiB",
        < 4 * 1024 * 1024 => "1To4MiB", < 16 * 1024 * 1024 => "4To16MiB", _ => "gte16MiB"
    };

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
        Exception? requestFailure = null;
        try { await next(context); }
        catch (Exception error)
        {
            requestFailure = error;
            throw;
        }
        finally
        {
            var model = context.Items.Remove(ModelKey, out var value) && value is string candidate &&
                (models.IsModelAvailable(candidate) || APIMartImagePolicy.Models.Contains(candidate, StringComparer.Ordinal))
                ? candidate : "";
            var status = context.Response.StatusCode is 413 || requestFailure is BadHttpRequestException { StatusCode: 413 }
                ? 413 : context.RequestAborted.IsCancellationRequested ? 499 :
                requestFailure is not null || context.Items.ContainsKey(ErrorKey) ? 500 : context.Response.StatusCode;
            var account = feature is not null && status is >= 200 and < 300 &&
                feature is not ("image-task" or "image-proxy") ? FeatureIdentity.AccountId(context.User) : null;
            var usage = context.Items[UsageKey] as Usage;
            var isSync = route.Equals("api/sync/data", StringComparison.OrdinalIgnoreCase);
            var error = status is 504 ? "timeout" : status == 500 && context.Items.ContainsKey(ErrorKey) ? "upstream" : "";
            analytics.Record(new(DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                feature ?? (route.StartsWith("api/sync/", StringComparison.OrdinalIgnoreCase) ? "sync" :
                    route.Equals("api/auth/register", StringComparison.OrdinalIgnoreCase) ? "registration" : "api"),
                model, context.Request.Method + " /" + route, status,
                (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds), account,
                usage?.Input, usage?.Output, usage?.Images ?? 0, error,
                isSync && HttpMethods.IsPost(context.Request.Method) ? SizeBucket(context.Request.ContentLength) : ""));
        }
    }
}
