using System.Globalization;

namespace RvtIO.Cli;

/// <summary>The <c>--ifc-timestamp &lt;timestamp&gt;</c> option: fixes the UTC timestamp written into the STEP <c>FILE_NAME</c> header so
/// repeated IFC exports of the same input are byte-for-byte identical.</summary>
internal static class IfcTimestampOption
{
    public const string Name = "--ifc-timestamp";

    private static readonly string[] ExactFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
    ];

    /// <summary>Reads <c>--ifc-timestamp</c> from <paramref name="args"/>. Without the option <paramref name="timestamp"/> is null.
    /// Accepts ISO-8601 timestamps (UTC when no offset is given) or non-negative Unix epoch seconds. Returns false with
    /// <paramref name="error"/> set when the option has no value, is given twice or cannot be parsed.</summary>
    public static bool TryParse(string[] args, out DateTimeOffset? timestamp, out string? error)
    {
        timestamp = null;
        error = null;
        bool seen = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != Name) continue;
            if (seen || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"{Name} requires one ISO-8601 timestamp (for example 2026-01-15T12:30:45Z) or Unix epoch seconds.";
                timestamp = null;
                return false;
            }
            seen = true;
            string raw = args[++i];
            if (!TryParseValue(raw, out var parsed))
            {
                error = $"{Name} requires one ISO-8601 timestamp (for example 2026-01-15T12:30:45Z) or Unix epoch seconds.";
                timestamp = null;
                return false;
            }
            timestamp = parsed;
        }
        return true;
    }

    private static bool TryParseValue(string raw, out DateTimeOffset result)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            result = default;
            return false;
        }
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long epochSeconds))
        {
            try
            {
                result = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                result = default;
                return false;
            }
        }
        return DateTimeOffset.TryParseExact(raw, ExactFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    }
}
