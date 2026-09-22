using System.Globalization;

namespace Paster;

/// <summary>
/// Runtime configuration, read from PASTER_* environment variables.
/// Defaults are tuned for an Azure App Service free tier (F1) instance: 1 GB of
/// local disk shared with everything else, so we keep a hard cap on stored bytes.
/// </summary>
public sealed class PasterOptions
{
    public const long DefaultMaxFileBytes = 1024 * 1024;            // 1 MiB
    public const long DefaultMaxTotalBytes = 64 * 1024 * 1024;      // 64 MiB
    public const int DefaultTtlSeconds = 300;                       // 5 minutes

    public required string StorageRoot { get; init; }
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
    public int TtlSeconds { get; init; } = DefaultTtlSeconds;

    /// <summary>
    /// Optional absolute base URL used when building download links. When it is
    /// unset the link is built from the incoming request (App Service terminates
    /// TLS, so the forwarded headers middleware has to run first).
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    public TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds);

    public static PasterOptions FromEnvironment()
    {
        var root = ReadString("PASTER_STORAGE_ROOT");

        if (string.IsNullOrWhiteSpace(root))
        {
            // /home is the persistent mount on Linux App Service; falling back to a
            // folder next to the app keeps local development self-contained.
            root = OperatingSystem.IsLinux()
                ? "/home/data/paster"
                : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data");
        }

        return new PasterOptions
        {
            StorageRoot = Path.GetFullPath(root),
            MaxFileBytes = ReadLong("PASTER_MAX_FILE_BYTES", DefaultMaxFileBytes),
            MaxTotalBytes = ReadLong("PASTER_MAX_TOTAL_BYTES", DefaultMaxTotalBytes),
            TtlSeconds = (int)ReadLong("PASTER_TTL_SECONDS", DefaultTtlSeconds),
            PublicBaseUrl = Blank(ReadString("PASTER_PUBLIC_BASE_URL")) ? null : ReadString("PASTER_PUBLIC_BASE_URL")!.TrimEnd('/'),
        };
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? ReadString(string name) => Environment.GetEnvironmentVariable(name);

    private static long ReadLong(string name, long fallback)
    {
        var raw = ReadString(name);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
