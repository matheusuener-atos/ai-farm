using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace atos.AIFarm.Core.Application;

public sealed record FarmWorkloadDefinition(
    string Id,
    string Workload,
    string Version,
    string DisplayName,
    string Description,
    string VerificationPolicy,
    int Iterations,
    int TimeoutSeconds,
    int MaxLogicalProcessors,
    long MaxMemoryBytes,
    long ProcessingPointsMilli,
    string NetworkPolicy,
    string DataClassification,
    string? InputSchema = null,
    string? OutputSchema = null,
    int RequiredLogicalProcessors = 1,
    long RequiredMemoryBytes = 64L * 1024 * 1024)
{
    public FarmWorkloadDefinition ValidateResources()
    {
        if (RequiredLogicalProcessors < 1 || RequiredLogicalProcessors > MaxLogicalProcessors)
            throw new InvalidOperationException($"Requisito de CPU inválido no workload {Id}.");
        if (RequiredMemoryBytes < 1 || RequiredMemoryBytes > MaxMemoryBytes)
            throw new InvalidOperationException($"Requisito de memória inválido no workload {Id}.");
        return this;
    }
}

public static class FarmWorkloadCatalog
{
    public const string Version = "atos-workload-catalog-v1";
    public const string DefaultId = "sha256-chain-v1";

    // Workload da farm contínua (ADR 0036): pesado o suficiente para aparecer
    // no gráfico de desempenho, curto o suficiente para a reexecução inline e
    // o deadline de 30s do lifecycle.
    public const string ContinuousId = "sha256-chain-marathon-v1";
    public const string ContinuousVersion = "atos-sha256-chain-marathon-v1";

    // Rota "AI Farm Network" (ADR 0040): o chat vira demanda; um node compõe
    // a resposta extrativa sobre a biblioteca indexada, verificável 1:1.
    public const string AnswerId = "iris-answer-v1";
    public const string AnswerVersion = "atos-iris-answer-v1";
    public const string AnswerInputSchema = "atos-answer-input-v1";
    public const string AnswerOutputSchema = "atos-answer-output-v1";

    public static IReadOnlyList<FarmWorkloadDefinition> Definitions { get; } =
    [
        new(
            DefaultId,
            "SHA256_CHAIN",
            "atos-sha256-chain-v1",
            "SHA-256 CHAIN",
            "Benchmark encadeado para medir e verificar processamento determinístico.",
            "DETERMINISTIC_REEXECUTION_V1",
            100_000,
            30,
            1,
            256L * 1024 * 1024,
            1_000,
            "DENY_ALL",
            "STANDARD",
            RequiredMemoryBytes: 64L * 1024 * 1024),
        new(
            "merkle-root-sha256-v1",
            "MERKLE_ROOT_SHA256",
            "atos-merkle-root-sha256-v1",
            "MERKLE ROOT SHA-256",
            "Constrói e verifica uma árvore de integridade determinística com 4.096 folhas.",
            "DETERMINISTIC_REEXECUTION_V1",
            4_096,
            30,
            1,
            256L * 1024 * 1024,
            1_000,
            "DENY_ALL",
            "STANDARD",
            RequiredMemoryBytes: 128L * 1024 * 1024),
        new(
            ContinuousId,
            "SHA256_CHAIN",
            ContinuousVersion,
            "SHA-256 MARATHON",
            "Cadeia longa para consumo contínuo do poder autorizado; ~20x o benchmark padrão.",
            "DETERMINISTIC_REEXECUTION_V1",
            2_000_000,
            30,
            1,
            256L * 1024 * 1024,
            20_000,
            "DENY_ALL",
            "STANDARD",
            RequiredMemoryBytes: 64L * 1024 * 1024),
        new(
            "iris-text-index-v1",
            "IRIS_TEXT_INDEX",
            "atos-iris-text-index-v1",
            "IRIS TEXT INDEX",
            "Normaliza e indexa texto real para recuperação lexical e preparação de contexto da Iris.",
            "DETERMINISTIC_OUTPUT_V1",
            1,
            30,
            1,
            256L * 1024 * 1024,
            2_000,
            "DENY_ALL",
            "STANDARD",
            "atos-text-input-v1",
            "atos-text-index-v1",
            RequiredMemoryBytes: 128L * 1024 * 1024),
        new(
            AnswerId,
            "IRIS_ANSWER",
            AnswerVersion,
            "IRIS ANSWER",
            "Compõe uma resposta extrativa determinística sobre trechos da biblioteca da conta.",
            "DETERMINISTIC_OUTPUT_V1",
            1,
            30,
            1,
            256L * 1024 * 1024,
            5_000,
            "DENY_ALL",
            "STANDARD",
            AnswerInputSchema,
            AnswerOutputSchema,
            RequiredMemoryBytes: 128L * 1024 * 1024)
    ];

    public static FarmWorkloadDefinition GetRequired(string id)
    {
        var definition = Definitions.SingleOrDefault(x =>
            string.Equals(x.Id, id?.Trim(), StringComparison.Ordinal))
            ?? throw new ArgumentException("O workload não pertence ao catálogo autorizado.", nameof(id));
        return definition.ValidateResources();
    }

    public static FarmWorkloadDefinition? Find(string workload, string version) =>
        Definitions.SingleOrDefault(x =>
            string.Equals(x.Workload, workload, StringComparison.Ordinal) &&
            string.Equals(x.Version, version, StringComparison.Ordinal));
}

public static class FarmDeterministicWorkloads
{
    public sealed record Execution(string Digest, string? OutputPayload);

    public static string Compute(
        string workload,
        string nonce,
        int iterations,
        CancellationToken cancellationToken = default) =>
        Execute(workload, nonce, iterations, null, cancellationToken).Digest;

    public static Execution Execute(
        string workload,
        string nonce,
        int iterations,
        string? inputPayload,
        CancellationToken cancellationToken = default) => workload switch
        {
            "SHA256_CHAIN" => DigestOnly(ComputeSha256Chain(nonce, iterations, cancellationToken)),
            "MERKLE_ROOT_SHA256" => DigestOnly(ComputeMerkleRoot(nonce, iterations, cancellationToken)),
            "IRIS_TEXT_INDEX" => ComputeTextIndex(inputPayload, cancellationToken),
            "IRIS_ANSWER" => ComputeAnswer(inputPayload, cancellationToken),
            _ => throw new ArgumentException("Workload não autorizado.", nameof(workload))
        };

    public static string ComputeInputDigest(string inputPayload)
    {
        ValidateTextInput(inputPayload);
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(inputPayload))).ToLowerInvariant();
    }

    private static string ComputeSha256Chain(
        string nonce,
        int iterations,
        CancellationToken cancellationToken)
    {
        Validate(nonce, iterations);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        for (var index = 0; index < iterations; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            hash = SHA256.HashData(hash);
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeMerkleRoot(
        string nonce,
        int leafCount,
        CancellationToken cancellationToken)
    {
        Validate(nonce, leafCount);
        if (!BitOperations.IsPow2(leafCount) || leafCount > 65_536)
            throw new ArgumentOutOfRangeException(nameof(leafCount), "A árvore exige uma quantidade de folhas em potência de dois.");

        var seed = Encoding.UTF8.GetBytes(nonce);
        var leaves = new byte[leafCount][];
        for (var index = 0; index < leafCount; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var payload = new byte[seed.Length + sizeof(int)];
            seed.CopyTo(payload, 0);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(seed.Length), index);
            leaves[index] = SHA256.HashData(payload);
        }

        var level = leaves;
        while (level.Length > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = new byte[level.Length / 2][];
            for (var index = 0; index < level.Length; index += 2)
            {
                var pair = new byte[64];
                level[index].CopyTo(pair, 0);
                level[index + 1].CopyTo(pair, 32);
                parent[index / 2] = SHA256.HashData(pair);
            }
            level = parent;
        }
        return Convert.ToHexString(level[0]).ToLowerInvariant();
    }

    private static void Validate(string nonce, int iterations)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            throw new ArgumentException("Nonce obrigatório.", nameof(nonce));
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations));
    }

    private static Execution ComputeTextIndex(
        string? inputPayload,
        CancellationToken cancellationToken)
    {
        ValidateTextInput(inputPayload);
        cancellationToken.ThrowIfCancellationRequested();
        var input = inputPayload!;
        var normalized = Regex.Replace(
            input.Normalize(NormalizationForm.FormKC)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n'),
            @"\s+",
            " ").Trim();
        var tokens = Regex.Matches(normalized.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .ToArray();
        var terms = tokens.GroupBy(x => x, StringComparer.Ordinal)
            .Select(group => new FarmTextIndexTerm(group.Key, group.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Term, StringComparer.Ordinal)
            .Take(32)
            .ToArray();
        var chunks = tokens.Chunk(200)
            .Select((chunk, index) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(string.Join('\n', chunk));
                return new FarmTextIndexChunk(
                    index + 1,
                    index * 200,
                    chunk.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            })
            .ToArray();
        var result = new FarmTextIndexResult(
            "atos-text-index-v1",
            ComputeInputDigest(input),
            normalized.Length,
            tokens.Length,
            tokens.Distinct(StringComparer.Ordinal).Count(),
            terms,
            chunks);
        var output = JsonSerializer.Serialize(result, TextIndexJsonOptions);
        return new Execution(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)))
                .ToLowerInvariant(),
            output);
    }

    private static Execution ComputeAnswer(
        string? inputPayload,
        CancellationToken cancellationToken)
    {
        ValidateTextInput(inputPayload);
        cancellationToken.ThrowIfCancellationRequested();
        FarmAnswerInput? request;
        try
        {
            request = JsonSerializer.Deserialize<FarmAnswerInput>(
                inputPayload!, TextIndexJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A entrada da resposta não é um JSON válido.", nameof(inputPayload), exception);
        }
        if (request is null ||
            request.Version != FarmWorkloadCatalog.AnswerInputSchema ||
            string.IsNullOrWhiteSpace(request.Question))
            throw new ArgumentException("A entrada da resposta viola o esquema tipado.", nameof(inputPayload));

        // Extração determinística: cada sentença dos trechos recebe o score
        // lexical da busca da Farm; as melhores compõem a resposta em ordem
        // estável (score desc, trecho, posição). Sem acaso, sem rede.
        var question = request.Question.Trim();
        var candidates = new List<(decimal Score, int ExcerptIndex, int SentenceIndex, string Sentence, FarmAnswerExcerpt Excerpt)>();
        var excerpts = request.Excerpts ?? [];
        for (var excerptIndex = 0; excerptIndex < excerpts.Count; excerptIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var excerpt = excerpts[excerptIndex];
            if (string.IsNullOrWhiteSpace(excerpt?.Excerpt)) continue;
            var sentences = Regex.Split(excerpt.Excerpt.Trim(), @"(?<=[\.\!\?])\s+");
            for (var sentenceIndex = 0; sentenceIndex < sentences.Length; sentenceIndex++)
            {
                var sentence = sentences[sentenceIndex].Trim();
                if (sentence.Length < 12) continue;
                var (score, matched) = FarmKnowledgeRetrieval.Score(question, sentence);
                if (score <= 0 || matched.Count == 0) continue;
                candidates.Add((score, excerptIndex, sentenceIndex, sentence, excerpt));
            }
        }

        string answer;
        FarmAnswerSource[] sources;
        if (candidates.Count == 0)
        {
            answer = "A rede ainda não tem esse conhecimento indexado. " +
                "Alimente a biblioteca em Farm → Demandas úteis, ou use a rota externa para perguntas gerais.";
            sources = [];
        }
        else
        {
            var selected = candidates
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.ExcerptIndex)
                .ThenBy(x => x.SentenceIndex)
                .ToArray();
            var composed = new List<(int ExcerptIndex, int SentenceIndex, string Sentence, FarmAnswerExcerpt Excerpt)>();
            var length = 0;
            foreach (var candidate in selected)
            {
                if (composed.Count > 0 && length + candidate.Sentence.Length > 900) continue;
                composed.Add((candidate.ExcerptIndex, candidate.SentenceIndex, candidate.Sentence, candidate.Excerpt));
                length += candidate.Sentence.Length + 1;
                if (composed.Count == 6) break;
            }
            answer = string.Join(" ", composed
                .OrderBy(x => x.ExcerptIndex)
                .ThenBy(x => x.SentenceIndex)
                .Select(x => x.Sentence));
            sources = composed
                .GroupBy(x => x.Excerpt.Reference, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new FarmAnswerSource(group.Key, group.First().Excerpt.Label))
                .ToArray();
        }

        var result = new FarmAnswerResult(
            FarmWorkloadCatalog.AnswerOutputSchema,
            ComputeInputDigest(inputPayload!),
            answer,
            sources,
            candidates.Count);
        var output = JsonSerializer.Serialize(result, TextIndexJsonOptions);
        return new Execution(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output)))
                .ToLowerInvariant(),
            output);
    }

    private static void ValidateTextInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("O texto útil é obrigatório.", nameof(input));
        var size = Encoding.UTF8.GetByteCount(input);
        if (size > 32 * 1024)
            throw new ArgumentOutOfRangeException(nameof(input),
                "O texto útil deve possuir no máximo 32 KiB em UTF-8.");
        if (input.Any(character => char.IsControl(character) &&
                                   character is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("O texto útil contém caracteres de controle inválidos.", nameof(input));
    }

    private static Execution DigestOnly(string digest) => new(digest, null);

    private static readonly JsonSerializerOptions TextIndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}

public sealed record FarmTextIndexTerm(string Term, int Count);
public sealed record FarmTextIndexChunk(
    int Sequence,
    int StartToken,
    int TokenCount,
    string Digest);
public sealed record FarmTextIndexResult(
    string Version,
    string InputDigest,
    int CharacterCount,
    int TokenCount,
    int UniqueTokenCount,
    IReadOnlyList<FarmTextIndexTerm> TopTerms,
    IReadOnlyList<FarmTextIndexChunk> Chunks);

public sealed record FarmAnswerExcerpt(string Reference, string Label, string Excerpt);

public sealed record FarmAnswerInput(
    string Version,
    string Question,
    IReadOnlyList<FarmAnswerExcerpt>? Excerpts);

public sealed record FarmAnswerSource(string Reference, string Label);

public sealed record FarmAnswerResult(
    string Version,
    string InputDigest,
    string Answer,
    IReadOnlyList<FarmAnswerSource> Sources,
    int ScoredSentences);
