namespace Agent.Channels.Mattermost;

/// <summary>
/// Settings for the Mattermost channel. Bound from the <c>Mattermost</c> section and read through
/// <c>IOptionsMonitor</c> so edits hot-reload.
/// </summary>
public sealed class MattermostOptions
{
    public const string SectionName = "Mattermost";

    /// <summary>Server URL, e.g. <c>https://chat.corp.local</c>. The WebSocket address is derived from it.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Access token of the bot account.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Answer every post in a direct-message channel with the bot; no mention needed.</summary>
    public bool RespondToDirectMessages { get; set; } = true;

    /// <summary>Answer <c>@bot</c> mentions in public, private and group channels.</summary>
    public bool RespondToMentions { get; set; } = true;

    /// <summary>When false, every post in a group message (channel type G) is treated as addressed to the bot.</summary>
    public bool GroupMessagesRequireMention { get; set; } = true;

    /// <summary>
    /// After the bot posted in a thread, later posts in that thread count as follow-ups without a mention
    /// for this many minutes. Zero disables thread continuation.
    /// </summary>
    public int ThreadFollowMinutes { get; set; } = 120;

    /// <summary>How many recent posts of a DM channel are loaded as history when the post is not in a thread.</summary>
    public int DmHistoryMessages { get; set; } = 30;

    /// <summary>
    /// Channel ids or names the bot listens to. Empty means every channel. Direct-message channels are always
    /// allowed; the list restricts public, private and group channels.
    /// </summary>
    public string[] ChannelAllowList { get; set; } = [];

    /// <summary>
    /// Channel id that receives a copy of every terminal task event: merge request opened, task failed, needs
    /// input, CI fix given up. Progress stays on the originating thread. Empty disables the mirror.
    /// </summary>
    public string OpsChannelId { get; set; } = string.Empty;

    /// <summary>Emoji added to the triggering post while the agent works.</summary>
    public string AckReaction { get; set; } = "eyes";

    /// <summary>Emoji that replaces <see cref="AckReaction"/> on success.</summary>
    public string DoneReaction { get; set; } = "white_check_mark";

    /// <summary>Emoji that replaces <see cref="AckReaction"/> on failure.</summary>
    public string FailedReaction { get; set; } = "x";

    /// <summary>
    /// Reserved: update the reply post while the answer streams. The Core reply contract delivers complete
    /// replies only, so this flag is bound but not acted on yet.
    /// </summary>
    public bool StreamByEditing { get; set; }

    /// <summary>The server maximum post length; longer replies are split into consecutive posts.</summary>
    public int MaxPostLength { get; set; } = 16383;

    /// <summary>First wait after the WebSocket drops; doubles on every failed attempt.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>Upper bound for the reconnect wait.</summary>
    public int MaxReconnectDelaySeconds { get; set; } = 60;
}
