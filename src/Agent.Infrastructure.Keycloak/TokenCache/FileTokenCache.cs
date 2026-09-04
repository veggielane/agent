using System.Security.Cryptography;
using System.Text.Json;
using Agent.Infrastructure.Keycloak.DeviceFlow;

namespace Agent.Infrastructure.Keycloak.TokenCache;

/// <summary>
/// Stores tokens in <c>%APPDATA%/agent/tokens.json</c> (or <c>~/.config/agent/tokens.json</c>). On Windows the
/// file holds DPAPI-protected bytes bound to the current user; elsewhere it is plain JSON with mode 0600.
/// </summary>
public sealed class FileTokenCache : ITokenCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileTokenCache()
        : this(DefaultPath)
    {
    }

    public FileTokenCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = System.IO.Path.GetFullPath(path);
    }

    public static string DefaultPath
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "agent", "tokens.json");

    public string FilePath { get; }

    public async Task<TokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            }

            return JsonSerializer.Deserialize<TokenSet>(bytes, Json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable or written by another user / machine: behave as "not logged in".
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(TokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens, Json);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            }

            var temp = FilePath + ".tmp";
            await using (var stream = Open(temp))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, FilePath, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static FileStream Open(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }
}
