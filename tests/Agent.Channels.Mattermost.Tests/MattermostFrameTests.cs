using System.Text.Json;
using static Agent.Channels.Mattermost.Tests.Fixtures;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostFrameTests
{
    [Fact]
    public void Parse_PostedFrame_ParsesEmbeddedPostAndMentions()
    {
        var frame = MattermostFrame.Parse(PostedFrame(Post(message: "@agent hi", rootId: "r1", createAt: 4242), "O", ["bot1"], channelName: "dev"));

        Assert.Equal("posted", frame.Event);
        Assert.False(frame.IsReply);
        var posted = Assert.IsType<MattermostPostedEvent>(frame.Posted);
        Assert.Equal("p1", posted.Post.Id);
        Assert.Equal("@agent hi", posted.Post.Message);
        Assert.Equal("r1", posted.Post.RootId);
        Assert.Equal(4242, posted.Post.CreateAt);
        Assert.Equal("O", posted.ChannelType);
        Assert.Equal("dev", posted.ChannelName);
        Assert.Equal("@alice", posted.SenderName);
        Assert.Equal(["bot1"], posted.Mentions);
    }

    [Fact]
    public void Parse_PostedFrameWithoutMentions_HasEmptyMentions()
    {
        var frame = MattermostFrame.Parse(PostedFrame(Post(), "D"));

        Assert.NotNull(frame.Posted);
        Assert.Empty(frame.Posted.Mentions);
    }

    [Fact]
    public void Parse_RealServerShape_Works()
    {
        const string json = """
            {"event":"posted","data":{"channel_display_name":"@alice","channel_name":"bot1__u1","channel_type":"D","post":"{\"id\":\"p9\",\"create_at\":1725400000000,\"update_at\":1725400000000,\"edit_at\":0,\"delete_at\":0,\"is_pinned\":false,\"user_id\":\"u1\",\"channel_id\":\"c1\",\"root_id\":\"\",\"original_id\":\"\",\"message\":\"hello bot\",\"type\":\"\",\"props\":{},\"hashtags\":\"\",\"pending_post_id\":\"abc\",\"reply_count\":0,\"last_reply_at\":0,\"participants\":null,\"metadata\":{}}","sender_name":"@alice","set_online":true,"team_id":""},"broadcast":{"omit_users":null,"user_id":"","channel_id":"c1","team_id":""},"seq":3}
            """;

        var frame = MattermostFrame.Parse(json);

        Assert.NotNull(frame.Posted);
        Assert.Equal("p9", frame.Posted.Post.Id);
        Assert.Equal("hello bot", frame.Posted.Post.Message);
        Assert.Equal("D", frame.Posted.ChannelType);
        Assert.False(frame.Posted.Post.IsReply);
    }

    [Fact]
    public void Parse_AuthReply_IsReplyWithStatus()
    {
        var ok = MattermostFrame.Parse(AuthOkFrame);
        var fail = MattermostFrame.Parse(AuthFailFrame);

        Assert.True(ok.IsReply);
        Assert.True(ok.IsOk);
        Assert.Equal(1, ok.SeqReply);
        Assert.Null(ok.Posted);
        Assert.True(fail.IsReply);
        Assert.False(fail.IsOk);
        Assert.Equal("Invalid or expired session", fail.Error);
    }

    [Fact]
    public void Parse_HelloEvent_HasNoPost()
    {
        var frame = MattermostFrame.Parse(HelloFrame);

        Assert.Equal("hello", frame.Event);
        Assert.Null(frame.Posted);
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => MattermostFrame.Parse("{not json"));
    }
}
