using Agent.Core.Llm;

namespace Agent.Core.Tests.Llm;

public sealed class LlmOptionsTests
{
    [Fact]
    public void ResolveModel_Precedence()
    {
        var options = new LlmOptions
        {
            AnswerModel = "answer",
            CodingModel = "coding",
            Overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Jira.Answer"] = "jira-small",
                ["Coding"] = "coding-override",
            },
        };

        Assert.Equal("explicit", options.ResolveModel(ModelPurpose.Answer, "Jira.Answer", "explicit"));
        Assert.Equal("jira-small", options.ResolveModel(ModelPurpose.Answer, "Jira.Answer"));
        Assert.Equal("answer", options.ResolveModel(ModelPurpose.Answer, "Mattermost.Answer"));
        Assert.Equal("coding-override", options.ResolveModel(ModelPurpose.Coding));
        options.Overrides.Remove("Coding");
        Assert.Equal("coding", options.ResolveModel(ModelPurpose.Coding));
        options.CodingModel = null;
        Assert.Equal("answer", options.ResolveModel(ModelPurpose.Coding));
    }
}
