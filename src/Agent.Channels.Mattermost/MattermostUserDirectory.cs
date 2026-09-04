using Microsoft.Extensions.Caching.Memory;

namespace Agent.Channels.Mattermost;

/// <summary>User lookups with a short cache, shared by the listener, reply sender and context provider.</summary>
public interface IMattermostUserDirectory
{
    /// <summary>The bot account; cached for a long time because it never changes while running.</summary>
    Task<MattermostUser> GetMeAsync(CancellationToken cancellationToken);

    /// <summary>A user by id, cached for a few minutes. Null when unknown.</summary>
    Task<MattermostUser?> GetUserAsync(string userId, CancellationToken cancellationToken);
}

public sealed class MattermostUserDirectory : IMattermostUserDirectory
{
    private const string MeKey = "mattermost:me";
    private const string UserKeyPrefix = "mattermost:user:";

    private static readonly TimeSpan MeLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan UserLifetime = TimeSpan.FromMinutes(5);

    private readonly IMattermostClient _client;
    private readonly IMemoryCache _cache;

    public MattermostUserDirectory(IMattermostClient client, IMemoryCache cache)
    {
        _client = client;
        _cache = cache;
    }

    public async Task<MattermostUser> GetMeAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(MeKey, out MattermostUser? cached) && cached is not null)
        {
            return cached;
        }

        var me = await _client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        _cache.Set(MeKey, me, MeLifetime);
        return me;
    }

    public async Task<MattermostUser?> GetUserAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        var key = UserKeyPrefix + userId;
        if (_cache.TryGetValue(key, out MattermostUser? cached) && cached is not null)
        {
            return cached;
        }

        var user = await _client.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is not null)
        {
            _cache.Set(key, user, UserLifetime);
        }

        return user;
    }
}
