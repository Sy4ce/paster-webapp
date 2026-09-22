using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace Paster;

/// <summary>A file currently being held in the temporary store.</summary>
public sealed record StoredFile(
    string Token,
    string OriginalName,
    long Size,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public enum SaveStatus
{
    Ok,
    TooLarge,
    QuotaExceeded,
}

public sealed record SaveOutcome(SaveStatus Status, StoredFile? File, long FreeBytes);

public enum LookupStatus
{
    Found,
    Expired,
    NotFound,
}

public sealed record LookupOutcome(LookupStatus Status, StoredFile? File, string? Path);

public sealed record StoreStats(int FileCount, long UsedBytes);

/// <summary>
/// Stores uploads on the web app's own disk, one pair of files per upload:
/// <c>&lt;token&gt;.bin</c> holds the bytes and <c>&lt;token&gt;.json</c> the metadata.
/// Expiry is enforced both by a background sweep and, authoritatively, at lookup
/// time — App Service unloads idle free-tier apps, so a timer alone is not enough.
/// </summary>
public sealed class TempStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AbandonedPartAge = TimeSpan.FromMinutes(10);
    private const int TokenBytes = 16;   // 128 bits, base64url => 22 chars
    private const int MaxNameLength = 180;

    private readonly PasterOptions _options;
    private readonly ILogger<TempStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    public TempStore(PasterOptions options, ILogger<TempStore> logger)
    {
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_options.StorageRoot);
    }

    public async Task<SaveOutcome> SaveAsync(Stream source, string originalName, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await SweepLockedAsync(now, cancellationToken);

            var used = MeasureLocked(cancellationToken);
            var free = Math.Max(0, _options.MaxTotalBytes - used);

            var token = NewToken();
            var partPath = PathFor(token, ".part");
            var binPath = PathFor(token, ".bin");
            var metaPath = PathFor(token, ".json");

            long written;
            try
            {
                written = await CopyAsync(source, partPath, used, cancellationToken);
                File.Move(partPath, binPath);
            }
            catch
            {
                Delete(partPath);
                throw;
            }

            var file = new StoredFile(token, SanitizeName(originalName), written, now, now + _options.Ttl);
            try
            {
                await WriteMetaAsync(metaPath, file, cancellationToken);
            }
            catch
            {
                Delete(binPath);
                throw;
            }

            _logger.LogInformation("Stored {Size} bytes as {Token}, expires {ExpiresAt:u}.", written, token, file.ExpiresAtUtc);
            return new SaveOutcome(SaveStatus.Ok, file, Math.Max(0, free - written));
        }
        catch (QuotaExceededException)
        {
            var used = MeasureLocked(cancellationToken);
            return new SaveOutcome(SaveStatus.QuotaExceeded, null, Math.Max(0, _options.MaxTotalBytes - used));
        }
        catch (FileTooLargeException)
        {
            return new SaveOutcome(SaveStatus.TooLarge, null, 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Resolves a download token, deleting the file when it has expired.</summary>
    public async Task<LookupOutcome> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        if (!IsValidToken(token))
        {
            return new LookupOutcome(LookupStatus.NotFound, null, null);
        }

        var metaPath = PathFor(token!, ".json");
        var binPath = PathFor(token!, ".bin");

        StoredFile? file;
        try
        {
            file = await ReadMetaAsync(metaPath, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Discarding unreadable metadata for {Token}.", token);
            file = null;
        }

        if (file is null || !File.Exists(binPath))
        {
            Delete(metaPath);
            Delete(binPath);
            return new LookupOutcome(LookupStatus.NotFound, null, null);
        }

        if (file.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            Delete(metaPath);
            Delete(binPath);
            _logger.LogInformation("Token {Token} expired and was deleted.", token);
            return new LookupOutcome(LookupStatus.Expired, file, null);
        }

        return new LookupOutcome(LookupStatus.Found, file, binPath);
    }

    public async Task<StoreStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(DateTimeOffset.UtcNow, cancellationToken);
            return new StoreStats(CountLocked(cancellationToken), MeasureLocked(cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes everything that has expired. Called on a timer and before each write.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastSweep < SweepInterval)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<long> CopyAsync(Stream source, string partPath, long alreadyUsed, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long written = 0;

        try
        {
            await using var target = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > _options.MaxFileBytes)
                {
                    throw new FileTooLargeException();
                }

                if (alreadyUsed + written > _options.MaxTotalBytes)
                {
                    throw new QuotaExceededException();
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await target.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return written;
    }

    private async Task SweepLockedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var swept = 0;
        var freed = 0L;

        foreach (var metaPath in Directory.EnumerateFiles(_options.StorageRoot, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = Path.GetFileNameWithoutExtension(metaPath);
            var binPath = PathFor(token, ".bin");

            StoredFile? file = null;
            try
            {
                file = await ReadMetaAsync(metaPath, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Discarding unreadable metadata {Path}.", metaPath);
            }

            var binSize = File.Exists(binPath) ? new FileInfo(binPath).Length : 0;
            if (file is null || file.ExpiresAtUtc <= now || binSize == 0)
            {
                Delete(metaPath);
                Delete(binPath);
                swept++;
                freed += binSize;
            }
        }

        // Orphaned payloads (metadata lost) are kept for one TTL at most.
        foreach (var binPath in Directory.EnumerateFiles(_options.StorageRoot, "*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = Path.GetFileNameWithoutExtension(binPath);
            if (File.Exists(PathFor(token, ".json")))
            {
                continue;
            }

            var age = now - new DateTimeOffset(File.GetLastWriteTimeUtc(binPath), TimeSpan.Zero);
            if (age >= _options.Ttl)
            {
                var size = new FileInfo(binPath).Length;
                Delete(binPath);
                swept++;
                freed += size;
            }
        }

        // Interrupted writes.
        foreach (var partPath in Directory.EnumerateFiles(_options.StorageRoot, "*.part"))
        {
            var age = now - new DateTimeOffset(File.GetLastWriteTimeUtc(partPath), TimeSpan.Zero);
            if (age >= AbandonedPartAge)
            {
                Delete(partPath);
            }
        }

        _lastSweep = now;

        if (swept > 0)
        {
            _logger.LogInformation("Swept {Count} expired file(s), freed {Bytes} bytes.", swept, freed);
        }
    }

    private long MeasureLocked(CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var binPath in Directory.EnumerateFiles(_options.StorageRoot, "*.bin"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += new FileInfo(binPath).Length;
        }

        return total;
    }

    private int CountLocked(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var _ in Directory.EnumerateFiles(_options.StorageRoot, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static async Task<StoredFile?> ReadMetaAsync(string metaPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        await using var stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<StoredFile>(stream, cancellationToken: cancellationToken);
    }

    private static async Task WriteMetaAsync(string metaPath, StoredFile file, CancellationToken cancellationToken)
    {
        var tempPath = metaPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, file, cancellationToken: cancellationToken);
        }

        File.Move(tempPath, metaPath, overwrite: true);
    }

    private string PathFor(string token, string extension) => Path.Combine(_options.StorageRoot, token + extension);

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    private static bool IsValidToken(string? token)
    {
        if (token is null || token.Length != 22)
        {
            return false;
        }

        foreach (var c in token)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Keeps the client-supplied name as a label only — never as a path.</summary>
    private static string SanitizeName(string? raw)
    {
        var name = (raw ?? string.Empty).Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (name.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(name);
            if (extension.Length > 16)
            {
                extension = string.Empty;
            }

            name = name[..(MaxNameLength - extension.Length)] + extension;
        }

        return name.Length == 0 ? "file" : name;
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The file is still being streamed to a client; the next sweep retries.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileTooLargeException : Exception;

    private sealed class QuotaExceededException : Exception;
}
