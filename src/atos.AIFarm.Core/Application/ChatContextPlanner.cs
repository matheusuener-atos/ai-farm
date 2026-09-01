using System.Text;

namespace atos.AIFarm.Core.Application;

public sealed record ChatContextLimits(
    int DefaultMessageCount,
    int MaximumMessageCount,
    int MaximumInputTokens,
    int SummaryTokenBudget)
{
    public static ChatContextLimits Default { get; } = new(
        DefaultMessageCount: 16,
        MaximumMessageCount: 32,
        MaximumInputTokens: 32_000,
        SummaryTokenBudget: 2_000);

    public ChatContextLimits Validate()
    {
        if (DefaultMessageCount < 0 ||
            MaximumMessageCount < 1 ||
            DefaultMessageCount > MaximumMessageCount ||
            MaximumInputTokens < 1_024 ||
            SummaryTokenBudget < 128 ||
            SummaryTokenBudget >= MaximumInputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChatContextLimits),
                "Os limites de contexto são inválidos.");
        }

        return this;
    }
}

public interface IChatContextPlanner
{
    ChatContextPlan Plan(
        IReadOnlyList<ChatMessageSnapshot> messages,
        string prompt,
        int? requestedMessageCount = null);
}

public sealed class ChatContextPlanner(ChatContextLimits? limits = null)
    : IChatContextPlanner
{
    private readonly ChatContextLimits _limits =
        (limits ?? ChatContextLimits.Default).Validate();

    public ChatContextPlan Plan(
        IReadOnlyList<ChatMessageSnapshot> messages,
        string prompt,
        int? requestedMessageCount = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A mensagem não pode estar vazia.", nameof(prompt));
        }

        var messageLimit = requestedMessageCount ?? _limits.DefaultMessageCount;
        if (messageLimit < 0 || messageLimit > _limits.MaximumMessageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedMessageCount),
                $"O contexto deve conter entre 0 e {_limits.MaximumMessageCount} mensagens.");
        }

        var promptTokens = EstimateTokens(prompt);
        if (promptTokens >= _limits.MaximumInputTokens)
        {
            throw new InvalidOperationException(
                "A mensagem atual excede o limite seguro da janela de contexto.");
        }

        var selected = new List<ChatMessageSnapshot>();
        var selectedTokens = 0;
        var reserveForSummary = messages.Count > messageLimit
            ? _limits.SummaryTokenBudget
            : 0;
        var recentBudget = _limits.MaximumInputTokens - promptTokens - reserveForSummary;
        for (var index = messages.Count - 1;
             index >= 0 && selected.Count < messageLimit;
             index--)
        {
            var candidate = messages[index];
            var candidateTokens = EstimateTokens(candidate.Content);
            if (selectedTokens + candidateTokens > recentBudget)
            {
                break;
            }

            selected.Add(candidate);
            selectedTokens += candidateTokens;
        }
        selected.Reverse();

        var omittedCount = messages.Count - selected.Count;
        var context = new List<AiContextMessage>();
        var summaryTokens = 0;
        if (omittedCount > 0)
        {
            var summaryBudget = Math.Min(
                _limits.SummaryTokenBudget,
                _limits.MaximumInputTokens - promptTokens - selectedTokens);
            var summary = BuildSummary(messages.Take(omittedCount), summaryBudget);
            if (summary.Length > 0)
            {
                summaryTokens = EstimateTokens(summary);
                context.Add(new AiContextMessage(
                    ChatMessageRole.User,
                    summary,
                    IsSummary: true));
            }
        }

        context.AddRange(selected.Select(message => new AiContextMessage(
            message.Role,
            message.Content)));
        var estimatedTokens = checked(promptTokens + selectedTokens + summaryTokens);
        if (estimatedTokens > _limits.MaximumInputTokens)
        {
            throw new InvalidOperationException(
                "O contexto calculado excede o limite seguro configurado.");
        }

        return new ChatContextPlan(
            context,
            selected.Count,
            omittedCount,
            estimatedTokens,
            _limits.MaximumInputTokens);
    }

    private static int EstimateTokens(string content)
    {
        const int messageOverhead = 6;
        var bytes = Encoding.UTF8.GetByteCount(content);
        return checked(Math.Max(1, (bytes + 2) / 3) + messageOverhead);
    }

    private static string BuildSummary(
        IEnumerable<ChatMessageSnapshot> messages,
        int tokenBudget)
    {
        if (tokenBudget < 1)
        {
            return string.Empty;
        }

        var maximumCharacters = Math.Max(64, (tokenBudget - 16) * 3);
        var builder = new StringBuilder("RESUMO EXTRATIVO DO CONTEXTO ANTERIOR:\n");
        foreach (var message in messages.Reverse())
        {
            var normalized = string.Join(
                ' ',
                message.Content.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries));
            var role = message.Role == ChatMessageRole.User ? "USUÁRIO" : "ASSISTENTE";
            var available = maximumCharacters - builder.Length - role.Length - 5;
            if (available < 24)
            {
                break;
            }

            var excerptLength = Math.Min(available, Math.Min(320, normalized.Length));
            var excerpt = normalized[..excerptLength];
            if (excerptLength < normalized.Length && excerptLength > 1)
            {
                excerpt = $"{excerpt[..^1]}…";
            }
            builder.Append("- ").Append(role).Append(": ").AppendLine(excerpt);
        }

        return builder.ToString().TrimEnd();
    }
}
