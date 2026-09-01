using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace atos.AIFarm.Infrastructure.Attachments;

public sealed record NeonObjectStorageOptions(
    string AccessKeyId,
    string SecretAccessKey,
    string Endpoint,
    string Region,
    string BucketName = "chat-attachments")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AccessKeyId) ||
            string.IsNullOrWhiteSpace(SecretAccessKey) ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out _) ||
            string.IsNullOrWhiteSpace(Region) ||
            string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException("A configuração do Neon Object Storage é inválida.");
        }
    }
}

internal static class ChatAttachmentPolicy
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/png"] = [".png"],
            ["image/jpeg"] = [".jpg", ".jpeg"],
            ["image/webp"] = [".webp"],
            ["application/pdf"] = [".pdf"],
            ["text/plain"] = [".txt", ".log", ".cs", ".js", ".ts", ".py", ".sql", ".xml", ".html", ".css"],
            ["text/markdown"] = [".md"],
            ["text/csv"] = [".csv"],
            ["application/json"] = [".json"]
        };

    public static string Validate(string fileName, string contentType, long sizeBytes, ChatAttachmentLimits limits)
    {
        var safeName = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(safeName) || safeName.Length > 180)
        {
            throw new InvalidOperationException("O nome do anexo é inválido ou excede 180 caracteres.");
        }
        if (sizeBytes <= 0 || sizeBytes > limits.MaximumFileBytes)
        {
            throw new InvalidOperationException($"Cada anexo deve ter até {limits.MaximumFileBytes / 1024 / 1024} MB.");
        }
        var normalizedType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var extension = Path.GetExtension(safeName);
        if (!Allowed.TryGetValue(normalizedType, out var extensions) ||
            !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Tipo de anexo não permitido ou incompatível com a extensão.");
        }
        return safeName;
    }

    public static string NormalizeContentType(string fileName, string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (normalized is not ("" or "application/octet-stream")) return normalized;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".txt" or ".log" or ".cs" or ".js" or ".ts" or ".py" or ".sql" or ".xml" or ".html" or ".css" => "text/plain",
            _ => normalized
        };
    }

    public static void ValidateContent(string contentType, byte[] content)
    {
        var valid = contentType switch
        {
            "image/png" => content.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => content.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "image/webp" => content.Length >= 12 && content.AsSpan(0, 4).SequenceEqual("RIFF"u8) && content.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => content.AsSpan().StartsWith("%PDF-"u8),
            "application/json" => IsValidJson(content),
            _ when contentType.StartsWith("text/", StringComparison.Ordinal) => IsValidUtf8Text(content),
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException("O conteúdo real do anexo não corresponde ao tipo declarado.");
    }

    private static bool IsValidUtf8Text(byte[] content)
    {
        if (content.Contains((byte)0)) return false;
        try { _ = new UTF8Encoding(false, true).GetString(content); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool IsValidJson(byte[] content)
    {
        try { using var _ = JsonDocument.Parse(content); return true; }
        catch (JsonException) { return false; }
    }

    public static int EstimateInputTokens(string contentType, long sizeBytes) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? 16_384
            : checked((int)Math.Min(int.MaxValue, sizeBytes + 256));
}

public sealed class InMemoryChatAttachmentService : IChatAttachmentService
{
    private readonly ConcurrentDictionary<Guid, StoredAttachment> _items = new();
    public bool IsAvailable => true;
    public ChatAttachmentLimits Limits { get; } = ChatAttachmentLimits.Default;

    public async Task<ChatAttachmentSnapshot> UploadDraftAsync(Guid conversationId, string fileName, string contentType, Stream content, long sizeBytes, CancellationToken cancellationToken = default)
    {
        contentType = ChatAttachmentPolicy.NormalizeContentType(fileName, contentType);
        var safeName = ChatAttachmentPolicy.Validate(fileName, contentType, sizeBytes, Limits);
        var existing = _items.Values.Where(x => x.Snapshot.ConversationId == conversationId && x.Snapshot.Status == ChatAttachmentStatus.Draft).ToArray();
        if (existing.Length >= Limits.MaximumCount || existing.Sum(x => x.Snapshot.SizeBytes) + sizeBytes > Limits.MaximumTotalBytes)
        {
            throw new InvalidOperationException("O conjunto de anexos excede o limite desta execução.");
        }
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != sizeBytes) throw new InvalidOperationException("O tamanho recebido do anexo é inconsistente.");
        var bytes = buffer.ToArray();
        ChatAttachmentPolicy.ValidateContent(contentType, bytes);
        var id = Guid.NewGuid();
        var snapshot = new ChatAttachmentSnapshot(id, conversationId, safeName, contentType, sizeBytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ChatAttachmentStatus.Draft, DateTimeOffset.UtcNow);
        _items[id] = new(snapshot, bytes);
        return snapshot;
    }

    public Task<IReadOnlyList<ChatAttachmentSnapshot>> ListDraftsAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChatAttachmentSnapshot>>(_items.Values.Where(x => x.Snapshot.ConversationId == conversationId && x.Snapshot.Status == ChatAttachmentStatus.Draft).OrderBy(x => x.Snapshot.CreatedAtUtc).Select(x => x.Snapshot).ToArray());

    public Task<IReadOnlyList<AiAttachmentInput>> LoadDraftsAsync(Guid conversationId, IReadOnlyCollection<Guid> attachmentIds, CancellationToken cancellationToken = default)
    {
        var items = attachmentIds.Distinct().Select(id => _items.TryGetValue(id, out var item) && item.Snapshot.ConversationId == conversationId && item.Snapshot.Status == ChatAttachmentStatus.Draft ? item : throw new InvalidOperationException("Um anexo não existe ou não pertence a esta conversa.")).ToArray();
        return Task.FromResult<IReadOnlyList<AiAttachmentInput>>(items.Select(ToInput).ToArray());
    }

    public Task<bool> DeleteDraftAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.TryGetValue(attachmentId, out var item) && item.Snapshot.Status == ChatAttachmentStatus.Draft && _items.TryRemove(attachmentId, out _));

    public Task<ChatAttachmentDownload?> DownloadAsync(Guid attachmentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.TryGetValue(attachmentId, out var item) ? new ChatAttachmentDownload(item.Snapshot.FileName, item.Snapshot.ContentType, item.Content) : null);

    internal IReadOnlyList<ChatAttachmentSnapshot> Attach(Guid conversationId, IReadOnlyList<Guid>? ids)
    {
        if (ids is not { Count: > 0 }) return [];
        return ids.Select(id =>
        {
            if (!_items.TryGetValue(id, out var item) || item.Snapshot.Status != ChatAttachmentStatus.Draft) throw new InvalidOperationException("O anexo confirmado não está mais disponível.");
            var attached = item.Snapshot with { ConversationId = conversationId, Status = ChatAttachmentStatus.Attached };
            _items[id] = item with { Snapshot = attached };
            return attached;
        }).ToArray();
    }

    private static AiAttachmentInput ToInput(StoredAttachment item) => new(item.Snapshot.Id, item.Snapshot.ConversationId, item.Snapshot.FileName, item.Snapshot.ContentType, item.Snapshot.SizeBytes, item.Snapshot.Sha256, item.Content, ChatAttachmentPolicy.EstimateInputTokens(item.Snapshot.ContentType, item.Snapshot.SizeBytes), item.Snapshot.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
    private sealed record StoredAttachment(ChatAttachmentSnapshot Snapshot, byte[] Content);
}

public sealed class UnavailableChatAttachmentService : IChatAttachmentService
{
    public bool IsAvailable => false;
    public ChatAttachmentLimits Limits { get; } = ChatAttachmentLimits.Default;
    private static InvalidOperationException Error() => new("O armazenamento privado de anexos não está configurado neste ambiente.");
    public Task<ChatAttachmentSnapshot> UploadDraftAsync(Guid conversationId, string fileName, string contentType, Stream content, long sizeBytes, CancellationToken cancellationToken = default) => Task.FromException<ChatAttachmentSnapshot>(Error());
    public Task<IReadOnlyList<ChatAttachmentSnapshot>> ListDraftsAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ChatAttachmentSnapshot>>([]);
    public Task<IReadOnlyList<AiAttachmentInput>> LoadDraftsAsync(Guid conversationId, IReadOnlyCollection<Guid> attachmentIds, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<AiAttachmentInput>>(Error());
    public Task<bool> DeleteDraftAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<ChatAttachmentDownload?> DownloadAsync(Guid attachmentId, CancellationToken cancellationToken = default) => Task.FromResult<ChatAttachmentDownload?>(null);
}

internal interface IChatObjectStorage
{
    Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken);
    Task<byte[]> GetAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

internal sealed class NeonS3ObjectStorage : IChatObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    public NeonS3ObjectStorage(NeonObjectStorageOptions options)
    {
        options.Validate();
        _bucket = options.BucketName;
        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), new AmazonS3Config { ServiceURL = options.Endpoint, AuthenticationRegion = options.Region, ForcePathStyle = true });
    }
    public async Task PutAsync(string key, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, ContentType = contentType, InputStream = stream, AutoCloseStream = false }, cancellationToken);
    }
    public async Task<byte[]> GetAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
        await using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken) => _client.DeleteObjectAsync(_bucket, key, cancellationToken);
    public void Dispose() => _client.Dispose();
}

internal sealed class PostgresChatAttachmentService(
    IDbContextFactory<AIFarmDbContext> factory,
    IAIFarmIdentityAccessor identityAccessor,
    IChatObjectStorage storage) : IChatAttachmentService
{
    public bool IsAvailable => true;
    public ChatAttachmentLimits Limits { get; } = ChatAttachmentLimits.Default;

    public async Task<ChatAttachmentSnapshot> UploadDraftAsync(Guid conversationId, string fileName, string contentType, Stream content, long sizeBytes, CancellationToken cancellationToken = default)
    {
        contentType = ChatAttachmentPolicy.NormalizeContentType(fileName, contentType);
        var safeName = ChatAttachmentPolicy.Validate(fileName, contentType, sizeBytes, Limits);
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Conversations.AnyAsync(x => x.Id == conversationId && x.AccountId == identity.AccountId && x.Status == "Active", cancellationToken))
            throw new InvalidOperationException("A conversa não existe ou não pertence à conta atual.");
        var drafts = await db.ChatAttachments.Where(x => x.AccountId == identity.AccountId && x.ConversationId == conversationId && x.Status == "Draft").ToArrayAsync(cancellationToken);
        if (drafts.Length >= Limits.MaximumCount || drafts.Sum(x => x.SizeBytes) + sizeBytes > Limits.MaximumTotalBytes)
            throw new InvalidOperationException("O conjunto de anexos excede o limite desta execução.");
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != sizeBytes) throw new InvalidOperationException("O tamanho recebido do anexo é inconsistente.");
        var bytes = buffer.ToArray();
        ChatAttachmentPolicy.ValidateContent(contentType, bytes);
        var id = Guid.NewGuid();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = $"accounts/{identity.AccountId:N}/conversations/{conversationId:N}/{id:N}/{Uri.EscapeDataString(safeName)}";
        await storage.PutAsync(key, contentType, bytes, cancellationToken);
        try
        {
            var record = new ChatAttachmentRecord { Id = id, AccountId = identity.AccountId, ConversationId = conversationId, StorageKey = key, FileName = safeName, ContentType = contentType, SizeBytes = sizeBytes, Sha256 = sha, Status = "Draft", CreatedAtUtc = DateTimeOffset.UtcNow };
            db.ChatAttachments.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            return Snapshot(record);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<ChatAttachmentSnapshot>> ListDraftsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ChatAttachments.Where(x => x.AccountId == identity.AccountId && x.ConversationId == conversationId && x.Status == "Draft").OrderBy(x => x.CreatedAtUtc).Select(x => new ChatAttachmentSnapshot(x.Id, x.ConversationId, x.FileName, x.ContentType, x.SizeBytes, x.Sha256, ChatAttachmentStatus.Draft, x.CreatedAtUtc)).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiAttachmentInput>> LoadDraftsAsync(Guid conversationId, IReadOnlyCollection<Guid> attachmentIds, CancellationToken cancellationToken = default)
    {
        var ids = attachmentIds.Distinct().ToArray();
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var records = await db.ChatAttachments.Where(x => ids.Contains(x.Id) && x.AccountId == identity.AccountId && x.ConversationId == conversationId && x.Status == "Draft").ToArrayAsync(cancellationToken);
        if (records.Length != ids.Length) throw new InvalidOperationException("Um anexo não existe ou não pertence a esta conversa.");
        var result = new List<AiAttachmentInput>(records.Length);
        foreach (var record in records.OrderBy(x => Array.IndexOf(ids, x.Id)))
        {
            var bytes = await storage.GetAsync(record.StorageKey, cancellationToken);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != record.SizeBytes || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha), Convert.FromHexString(record.Sha256)))
                throw new InvalidOperationException("A integridade de um anexo não pôde ser confirmada.");
            result.Add(new AiAttachmentInput(record.Id, record.ConversationId, record.FileName, record.ContentType, record.SizeBytes, record.Sha256, bytes, ChatAttachmentPolicy.EstimateInputTokens(record.ContentType, record.SizeBytes), record.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    public async Task<bool> DeleteDraftAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ChatAttachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.AccountId == identity.AccountId && x.Status == "Draft", cancellationToken);
        if (record is null) return false;
        db.ChatAttachments.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
        await storage.DeleteAsync(record.StorageKey, cancellationToken);
        return true;
    }

    public async Task<ChatAttachmentDownload?> DownloadAsync(Guid attachmentId, CancellationToken cancellationToken = default)
    {
        var identity = await identityAccessor.GetRequiredIdentityAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var record = await db.ChatAttachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.AccountId == identity.AccountId, cancellationToken);
        return record is null ? null : new ChatAttachmentDownload(record.FileName, record.ContentType, await storage.GetAsync(record.StorageKey, cancellationToken));
    }

    private static ChatAttachmentSnapshot Snapshot(ChatAttachmentRecord record) => new(record.Id, record.ConversationId, record.FileName, record.ContentType, record.SizeBytes, record.Sha256, Enum.Parse<ChatAttachmentStatus>(record.Status), record.CreatedAtUtc);
}
