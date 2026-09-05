using System.Text;
using System.Text.Json;
using atos.AIFarm.Core.Application;
using atos.AIFarm.Infrastructure.Development;

namespace atos.AIFarm.Infrastructure.Providers.FarmNetwork;

/// <summary>
/// Interfaces primárias do provider externo configurado (Fake, OpenAI ou
/// compatível), capturadas pelos ramos de composição para o catálogo
/// composto enxergar a rota primária ao lado da rota da farm (ADR 0040).
/// </summary>
public sealed record PrimaryAiCatalogHandles(
    IAiRouteSelector Selector,
    IAiModelCatalog Models,
    IPriceEstimator Estimator,
    IAiPriceCatalog? Catalog);

/// <summary>
/// Catálogo composto: a rota primária continua sendo a padrão ("auto"); a
/// rota "AI Farm Network" entra como modelo selecionável que transforma a
/// mensagem numa demanda útil executada pelos nodes da própria conta.
/// </summary>
public sealed class FarmNetworkAiCatalog(
    PrimaryAiCatalogHandles primary,
    IFarmKnowledgeSearchService? knowledge = null) :
    IAiRouteSelector, IAiModelCatalog, IPriceEstimator, IAiRequestPriceEstimator, IAiPriceCatalog
{
    public const string Model = "iris-answer-extractive";
    public const string PriceCatalogVersion = "farm-network-aic-v1";

    // Orquestrador do "auto" (ADR 0041): score mínimo da biblioteca para a
    // rede assumir a resposta; abaixo disso a rota externa responde melhor.
    public const decimal AutoRouteScoreThreshold = 3m;

    public static AiRoute Route { get; } = new(AiProviderNames.FarmNetwork, Model, PriceCatalogVersion);

    // Tarifa simbólica: a computação é da própria conta; o AIC cobre o custo
    // de orquestração/verificação. 500 mAIC por 1M de unidades ≈ ~2 mAIC por
    // resposta típica — na ordem da rota gratuita externa.
    public static AiPriceCatalogEntry Entry { get; } = new(Route, 500, 500, 1_024);

    public Task<AiRoute> SelectAsync(string prompt, CancellationToken cancellationToken) =>
        primary.Selector.SelectAsync(prompt, cancellationToken);

    public async Task<AiRoute> SelectAsync(
        string prompt,
        string? modelPreference,
        CancellationToken cancellationToken)
    {
        var preference = modelPreference?.Trim();
        if (string.Equals(preference, Model, StringComparison.OrdinalIgnoreCase))
            return Route;
        // "Auto" orquestra: se a biblioteca cobre bem a pergunta, a rede
        // responde; senão, a rota externa. Preferência explícita nunca é
        // sobrescrita, e o preflight sempre mostra a rota escolhida.
        var automatic = string.IsNullOrWhiteSpace(preference) ||
            string.Equals(preference, ConversationAiSettings.AutomaticModel, StringComparison.OrdinalIgnoreCase);
        if (automatic && knowledge is not null)
        {
            try
            {
                var probe = await knowledge.SearchAsync(
                    prompt, maximumHits: 1, cancellationToken: cancellationToken);
                if (probe.HasHits && probe.Hits[0].Score >= AutoRouteScoreThreshold)
                    return Route;
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException)
            {
                // A sonda da biblioteca nunca derruba o chat: cai no externo.
            }
        }
        return await primary.Selector.SelectAsync(prompt, modelPreference, cancellationToken);
    }

    public IReadOnlyList<AiModelOption> GetOptions() =>
    [
        .. primary.Models.GetOptions(),
        new AiModelOption(
            Model,
            "AI FARM NETWORK (extrai da sua biblioteca)",
            Entry.MaximumOutputUnits),
    ];

    public async Task<PriceEstimate> EstimateAsync(
        string prompt,
        AiRoute route,
        CancellationToken cancellationToken)
    {
        if (route == Route) return Estimate(prompt.Length);
        return await primary.Estimator.EstimateAsync(prompt, route, cancellationToken);
    }

    public async Task<PriceEstimate> EstimateAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Route == Route) return Estimate(request.Prompt.Length);
        if (primary.Estimator is IAiRequestPriceEstimator requestEstimator)
            return await requestEstimator.EstimateAsync(request, cancellationToken);
        return await primary.Estimator.EstimateAsync(
            request.Prompt, request.Route, cancellationToken);
    }

    public AiPriceCatalogEntry Get(AiRoute route)
    {
        if (route == Route) return Entry;
        if (primary.Catalog is not null) return primary.Catalog.Get(route);
        throw new InvalidOperationException("A rota não pertence ao catálogo ativo.");
    }

    private static PriceEstimate Estimate(int promptLength)
    {
        var inputUnits = promptLength / 4 + Entry.InputEstimateOverheadUnits;
        var maximum =
            (inputUnits * Entry.InputCostPerMillionMinor +
             (long)Entry.MaximumOutputUnits * Entry.OutputCostPerMillionMinor) / 1_000_000;
        return new PriceEstimate(Math.Max(1, maximum));
    }
}

/// <summary>
/// Executa a rota da farm: recupera trechos da biblioteca, embala pergunta e
/// trechos numa demanda IRIS_ANSWER da própria conta, acompanha a fila até o
/// node concluir com verificação e devolve a resposta com as fontes.
/// </summary>
public sealed class FarmNetworkChatProvider(
    IFarmDemandQueueService demands,
    IFarmKnowledgeSearchService knowledge,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(90);

    public async Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Attachments is { Count: > 0 })
            throw new InvalidOperationException(
                "A rota AI Farm Network ainda não aceita anexos. Envie sem anexos ou use a rota externa.");

        var search = await knowledge.SearchAsync(
            request.Prompt, maximumHits: 6, cancellationToken: cancellationToken);
        if (!search.HasHits)
        {
            const string emptyAnswer =
                "A rede ainda não tem esse conhecimento indexado. Adicione conteúdo na " +
                "página Biblioteca (menu Farm → Biblioteca, ou /biblioteca) e pergunte de " +
                "novo — ou troque para o modelo automático, que usa a rota externa em perguntas gerais.";
            return Result(request, emptyAnswer);
        }

        var input = BuildInput(request.Prompt, search.Hits);
        var demand = await demands.SubmitAsync(
            new FarmDemandRequest(
                FarmWorkloadCatalog.AnswerId,
                DemandLabel(request.Prompt),
                input,
                FarmDemandPriority.High,
                LifetimeMinutes: 5,
                TargetShards: 1,
                MaxAttemptsPerShard: 2),
            cancellationToken);

        var deadline = timeProvider.GetUtcNow().Add(CompletionTimeout);
        var polls = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await demands.DownloadOutputAsync(demand.Id, cancellationToken);
            if (output is not null)
            {
                var payload = Encoding.UTF8.GetString(output.Content);
                var result = JsonSerializer.Deserialize<FarmAnswerResult>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("A rede devolveu uma resposta vazia.");
                var content = Compose(result);
                return Result(request, content);
            }
            if (++polls % 5 == 0)
            {
                var state = (await demands.GetDashboardAsync(cancellationToken))
                    .Demands.SingleOrDefault(x => x.Id == demand.Id);
                if (state is { Status: FarmDemandStatus.Failed or FarmDemandStatus.Expired })
                    throw new InvalidOperationException(
                        $"A rede não conseguiu computar a resposta ({state.Status}). {state.Detail}");
            }
            if (timeProvider.GetUtcNow() >= deadline)
                throw new InvalidOperationException(
                    "A rede não respondeu a tempo. Verifique na pool se algum computador está FARMANDO e tente de novo.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string BuildInput(string question, IReadOnlyList<FarmKnowledgeHit> hits)
    {
        // Teto de 30 KiB para caber no input tipado de 32 KiB com folga.
        var excerpts = new List<FarmAnswerExcerpt>();
        var budget = 30 * 1024 - question.Length - 512;
        foreach (var hit in hits)
        {
            var excerpt = hit.Excerpt.Length > 4_000 ? hit.Excerpt[..4_000] : hit.Excerpt;
            if (budget - excerpt.Length < 0) break;
            budget -= excerpt.Length;
            excerpts.Add(new FarmAnswerExcerpt(hit.Reference, hit.Label, excerpt));
        }
        return JsonSerializer.Serialize(
            new FarmAnswerInput(FarmWorkloadCatalog.AnswerInputSchema, question, excerpts),
            JsonOptions);
    }

    private static string Compose(FarmAnswerResult result)
    {
        if (result.Sources.Count == 0) return result.Answer;
        var builder = new StringBuilder(result.Answer);
        builder.Append("\n\nFontes da Farm:");
        foreach (var source in result.Sources)
            builder.Append($"\n- {source.Label} // {source.Reference}");
        return builder.ToString();
    }

    private AiProviderResult Result(AiProviderRequest request, string content)
    {
        var inputUnits = Math.Max(1, request.Prompt.Length / 4);
        var outputUnits = Math.Max(1, content.Length / 4);
        var entry = FarmNetworkAiCatalog.Entry;
        var cost = Math.Max(1,
            (inputUnits * entry.InputCostPerMillionMinor +
             outputUnits * entry.OutputCostPerMillionMinor) / 1_000_000);
        return new AiProviderResult(content, inputUnits, outputUnits, cost);
    }

    private static string DemandLabel(string prompt)
    {
        var normalized = new string(prompt.Where(character =>
            !char.IsControl(character)).ToArray()).Trim();
        var label = $"Chat // {normalized}";
        return label.Length <= 80 ? label : label[..80];
    }
}

/// <summary>
/// Despachante por rota: "AI Farm Network" vai para a rede; qualquer outra
/// rota segue para o provider externo protegido (timeout + circuit breaker).
/// </summary>
public sealed class RoutingAiProvider(
    ProtectedAiProvider external,
    FarmNetworkChatProvider farmNetwork) : IAiStreamingProvider
{
    public Task<AiProviderResult> ExecuteAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken) =>
        IsFarmRoute(request)
            ? farmNetwork.ExecuteAsync(request, cancellationToken)
            : external.ExecuteAsync(request, cancellationToken);

    public async Task<AiProviderResult> ExecuteStreamingAsync(
        AiProviderRequest request,
        AiTextDeltaHandler onDelta,
        CancellationToken cancellationToken)
    {
        if (!IsFarmRoute(request))
            return await external.ExecuteStreamingAsync(request, onDelta, cancellationToken);
        var result = await farmNetwork.ExecuteAsync(request, cancellationToken);
        await onDelta(result.Content, cancellationToken);
        return result;
    }

    private static bool IsFarmRoute(AiProviderRequest request) =>
        string.Equals(
            request.Route.Provider,
            AiProviderNames.FarmNetwork,
            StringComparison.Ordinal);
}
