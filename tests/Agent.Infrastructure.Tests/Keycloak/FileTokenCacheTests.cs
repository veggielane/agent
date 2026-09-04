using System.Text;
using Agent.Infrastructure.Keycloak.DeviceFlow;
using Agent.Infrastructure.Keycloak.TokenCache;

namespace Agent.Infrastructure.Tests.Keycloak;

public sealed class FileTokenCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public FileTokenCacheTests()
    {
        _path = Path.Combine(_directory, "nested", "tokens.json");
    }

    [Fact]
    public async Task SaveLoadClear_RoundTripsAndProtectsTheFile()
    {
        var cache = new FileTokenCache(_path);
        var tokens = new TokenSet("access-token-value", "refresh-token-value", new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), "id-token");

        Assert.Null(await cache.LoadAsync());

        await cache.SaveAsync(tokens);

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        var raw = await File.ReadAllBytesAsync(_path);
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("access-token-value", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("access-token-value", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
        }

        var loaded = await cache.LoadAsync();
        Assert.Equal(tokens, loaded);

        await cache.ClearAsync();
        Assert.False(File.Exists(_path));
        Assert.Null(await cache.LoadAsync());
        await cache.ClearAsync();
    }

    [Fact]
    public async Task SaveAsync_OverwritesPreviousTokens()
    {
        var cache = new FileTokenCache(_path);
        await cache.SaveAsync(new TokenSet("first", null, DateTimeOffset.UtcNow));

        await cache.SaveAsync(new TokenSet("second", "r", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal("second", (await cache.LoadAsync())!.AccessToken);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "not json, not dpapi");

        Assert.Null(await new FileTokenCache(_path).LoadAsync());
    }

    [Fact]
    public void DefaultPath_IsUnderApplicationData()
    {
        var cache = new FileTokenCache();

        Assert.EndsWith(Path.Combine("agent", "tokens.json"), cache.FilePath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), cache.FilePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
