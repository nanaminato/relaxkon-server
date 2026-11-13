using System.Globalization;

namespace RelaxKon.Auth.Application.Analytics;

internal static class EdgeOperationsLog
{
    // Dedicated Nginx log contains only epoch seconds, method, duration and declared length.
    public static async Task<IReadOnlyList<OperationsSample>> Read(string? path, DateTime start, DateTime end, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var samples = new List<OperationsSample>();
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) return [];
        var name = Path.GetFileName(fullPath);
        var files = Directory.EnumerateFiles(directory, name + ".*")
            .Where(file => int.TryParse(Path.GetExtension(file).AsSpan(1), out _)).Append(fullPath);
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var fields = line.Split(' ');
                if (fields.Length != 4 || !decimal.TryParse(fields[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var timestamp) ||
                    timestamp is < 0 or > 253402300799 || fields[1] is not ("GET" or "POST") ||
                    !decimal.TryParse(fields[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
                    seconds is < 0 or > 2147483 ||
                    !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var length) && fields[3] != "-")
                    continue;
                var day = DateTimeOffset.FromUnixTimeSeconds((long)timestamp).UtcDateTime.Date;
                if (day < start || day > end) continue;
                samples.Add(new(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "sync", "",
                    fields[1] + " /api/sync/data", 413, (int)(seconds * 1000), null,
                    ErrorKind: "edgeTooLarge", SizeBucket: fields[1] == "POST"
                        ? OperationsAnalyticsMiddleware.SizeBucket(fields[3] == "-" ? null : length) : ""));
            }
        }
        return samples;
    }
}
