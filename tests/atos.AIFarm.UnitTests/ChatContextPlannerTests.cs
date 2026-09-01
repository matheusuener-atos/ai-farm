using atos.AIFarm.Core.Application;

namespace atos.AIFarm.UnitTests;

public sealed class ChatContextPlannerTests
{
    [Fact]
    public void PlansRecentMessagesAndSummarizesOmittedHistoryWithinLimit()
    {
        var planner = new ChatContextPlanner(new ChatContextLimits(
            DefaultMessageCount: 2,
            MaximumMessageCount: 4,
            MaximumInputTokens: 1_024,
            SummaryTokenBudget: 256));
        var messages = Enumerable.Range(1, 6)
            .Select(index => Message(
                index % 2 == 0 ? ChatMessageRole.Assistant : ChatMessageRole.User,
                $"conteúdo histórico número {index}"))
            .ToArray();

        var plan = planner.Plan(messages, "mensagem atual", 2);

        Assert.Equal(2, plan.IncludedMessageCount);
        Assert.Equal(4, plan.OmittedMessageCount);
        Assert.True(plan.UsesSummary);
        Assert.True(plan.EstimatedInputTokens <= plan.MaximumInputTokens);
        Assert.Equal("conteúdo histórico número 5", plan.Messages[^2].Content);
        Assert.Equal("conteúdo histórico número 6", plan.Messages[^1].Content);
        Assert.Contains("número 4", plan.Messages[0].Content);
    }

    [Fact]
    public void RejectsPromptThatCannotFitSafeContextWindow()
    {
        var planner = new ChatContextPlanner(new ChatContextLimits(
            DefaultMessageCount: 2,
            MaximumMessageCount: 4,
            MaximumInputTokens: 1_024,
            SummaryTokenBudget: 128));

        Assert.Throws<InvalidOperationException>(() => planner.Plan(
            [],
            new string('x', 4_000)));
    }

    private static ChatMessageSnapshot Message(
        ChatMessageRole role,
        string content) => new(
            Guid.NewGuid(),
            role,
            content,
            ChatMessageStatus.Completed,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
}
