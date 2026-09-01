using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using atos.AIFarm.Core.Wallet;

namespace atos.AIFarm.Core.Application;

public sealed class ConversationExportService(IChatStore chat) : IConversationExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<ConversationExport?> ExportAsync(
        Guid conversationId,
        ConversationExportFormat format,
        CancellationToken cancellationToken = default)
    {
        var conversation = await chat.GetConversationAsync(conversationId, cancellationToken);
        if (conversation is null) return null;

        var baseName = SafeFileName(conversation.Title, conversation.Id);
        return format switch
        {
            ConversationExportFormat.Markdown => new ConversationExport(
                $"{baseName}.md",
                "text/markdown; charset=utf-8",
                Encoding.UTF8.GetBytes(ToMarkdown(conversation))),
            ConversationExportFormat.Json => new ConversationExport(
                $"{baseName}.json",
                "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(conversation, JsonOptions)),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static string ToMarkdown(ConversationSnapshot conversation)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(conversation.Title ?? "Conversa atos AI Farm")
            .AppendLine()
            .Append("- Conversa: `").Append(conversation.Id).AppendLine("`")
            .Append("- Criada em UTC: ").AppendLine(conversation.CreatedAtUtc.ToString("O"))
            .Append("- Atualizada em UTC: ").AppendLine(conversation.UpdatedAtUtc.ToString("O"));
        if (conversation.ParentConversationId is Guid parentId)
            builder.Append("- Ramificada de: `").Append(parentId).AppendLine("`");

        foreach (var message in conversation.Messages)
        {
            builder.AppendLine()
                .Append("## ").AppendLine(message.Role == ChatMessageRole.User ? "Usuário" : "atos AI")
                .AppendLine()
                .AppendLine(message.Content)
                .AppendLine();
            if (message.Route is not null)
                builder.Append("- Rota: ").Append(message.Route.Provider).Append(" / ").AppendLine(message.Route.Model);
            if (message.ActualCostMinor is long actualCost)
                builder.Append("- Custo liquidado: ").AppendLine(Aic.Format(actualCost));
            if (message.Attachments is { Count: > 0 })
            {
                builder.AppendLine("- Anexos:");
                foreach (var attachment in message.Attachments)
                    builder.Append("  - ").Append(attachment.FileName).Append(" (").Append(attachment.ContentType).Append(", ").Append(attachment.SizeBytes).AppendLine(" bytes)");
            }
            if (message.Feedback is not null)
            {
                builder.Append("- Avaliação: ").AppendLine(message.Feedback.Rating == ChatFeedbackRating.Positive ? "Útil" : "Não útil");
                if (!string.IsNullOrWhiteSpace(message.Feedback.Reason))
                    builder.Append("- Motivo: ").AppendLine(message.Feedback.Reason);
            }
        }
        return builder.ToString();
    }

    private static string SafeFileName(string? title, Guid conversationId)
    {
        var source = string.IsNullOrWhiteSpace(title) ? $"conversa-{conversationId:N}" : title;
        var normalized = new string(source
            .Normalize(NormalizationForm.FormD)
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ')
            .Select(character => character == ' ' ? '-' : char.ToLowerInvariant(character))
            .Take(80)
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? $"conversa-{conversationId:N}" : normalized;
    }
}
