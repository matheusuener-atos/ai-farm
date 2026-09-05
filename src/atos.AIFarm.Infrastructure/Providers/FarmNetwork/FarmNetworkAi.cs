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
    public const string GenerateModel = "iris-generate-network";
    public const string GeneratePriceCatalogVersion = "farm-generate-aic-v1";
    public const string PremiumModel = "iris-premium-network";
    public const string PremiumPriceCatalogVersion = "farm-premium-aic-v1";

    // Orquestrador do "auto" (ADR 0041): score mínimo da biblioteca para a
    // rede assumir a resposta; abaixo disso a rota externa responde melhor.
    public const decimal AutoRouteScoreThreshold = 3m;

    public static AiRoute Route { get; } = new(AiProviderNames.FarmNetwork, Model, PriceCatalogVersion);

    public static AiRoute GenerateRoute { get; } =
        new(AiProviderNames.FarmNetwork, GenerateModel, GeneratePriceCatalogVersion);

    public static AiPriceCatalogEntry GenerateEntry { get; } = new(GenerateRoute, 500, 500, 1_024);

    public static AiRoute PremiumRoute { get; } =
        new(AiProviderNames.FarmNetwork, PremiumModel, PremiumPriceCatalogVersion);

    // Premium custa mais (best-of-N usa varias maquinas + juiz), mas segue
    // simbolico frente ao provedor externo.
    public static AiPriceCatalogEntry PremiumEntry { get; } = new(PremiumRoute, 2_000, 2_000, 1_024);

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
        if (string.Equals(preference, GenerateModel, StringComparison.OrdinalIgnoreCase))
            return GenerateRoute;
        if (string.Equals(preference, PremiumModel, StringComparison.OrdinalIgnoreCase))
            return PremiumRoute;
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
        new AiModelOption(
            GenerateModel,
            "AI FARM GENERATE (modelo da rede — experimental)",
            GenerateEntry.MaximumOutputUnits),
        new AiModelOption(
            PremiumModel,
            "AI FARM PREMIUM (varias maquinas → melhor resposta)",
            PremiumEntry.MaximumOutputUnits),
    ];

    public async Task<PriceEstimate> EstimateAsync(
        string prompt,
        AiRoute route,
        CancellationToken cancellationToken)
    {
        if (route == Route || route == GenerateRoute || route == PremiumRoute)
            return Estimate(prompt.Length);
        return await primary.Estimator.EstimateAsync(prompt, route, cancellationToken);
    }

    public async Task<PriceEstimate> EstimateAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Route == Route || request.Route == GenerateRoute ||
            request.Route == PremiumRoute)
            return Estimate(request.Prompt.Length);
        if (primary.Estimator is IAiRequestPriceEstimator requestEstimator)
            return await requestEstimator.EstimateAsync(request, cancellationToken);
        return await primary.Estimator.EstimateAsync(
            request.Prompt, request.Route, cancellationToken);
    }

    public AiPriceCatalogEntry Get(AiRoute route)
    {
        if (route == Route) return Entry;
        if (route == GenerateRoute) return GenerateEntry;
        if (route == PremiumRoute) return PremiumEntry;
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
    TimeProvider timeProvider,
    FarmNetworkOptions networkOptions)
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
        if (request.Route == FarmNetworkAiCatalog.GenerateRoute)
            return await GenerateAsync(request, cancellationToken);
        if (request.Route == FarmNetworkAiCatalog.PremiumRoute)
            return await GeneratePremiumAsync(request, cancellationToken);

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

    /// <summary>
    /// ADR 0043: a mensagem vira demanda IRIS_GENERATE; um node com o runtime
    /// local executa o modelo da plataforma; a resposta volta rotulada como
    /// não verificada (CP=0 até o quórum).
    /// </summary>
    private async Task<AiProviderResult> GenerateAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var input = BuildGenerateInput(
            networkOptions.NetworkModel, request.Prompt, request.SystemInstruction, request.Context);
        var result = await RunGenerationAsync(input, request.Prompt, cancellationToken);
        var content = result.Answer +
            $"\n\n[gerado pela rede // modelo {result.Model} // não verificado — CP=0 até o quórum]";
        return BuildResult(content, request.Prompt, result, FarmNetworkAiCatalog.GenerateEntry);
    }

    /// <summary>
    /// best-of-N (ADR 0045): a mesma pergunta vai para N máquinas da rede em
    /// paralelo; um juiz (mais uma geração da rede) sintetiza a melhor
    /// resposta. É o único jeito legítimo de várias máquinas melhorarem UMA
    /// resposta — a inferência não se soma, mas a diversidade de candidatos sim.
    /// </summary>
    private async Task<AiProviderResult> GeneratePremiumAsync(
        AiProviderRequest request,
        CancellationToken cancellationToken)
    {
        var input = BuildGenerateInput(
            networkOptions.NetworkModel, request.Prompt, request.SystemInstruction, request.Context);
        var candidateCount = networkOptions.PremiumCandidates;
        var tasks = Enumerable.Range(0, candidateCount)
            .Select(async _ =>
            {
                try { return await RunGenerationAsync(input, request.Prompt, cancellationToken); }
                catch (InvalidOperationException) { return null; }
            })
            .ToArray();
        var candidates = (await Task.WhenAll(tasks))
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Answer))
            .Select(x => x!)
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException(
                "Nenhuma máquina da rede gerou uma resposta. Ative a geração em pelo menos um computador da pool " +
                $"(instale o Ollama e o modelo {networkOptions.NetworkModel}).");
        if (candidates.Length == 1)
        {
            var only = candidates[0];
            var soloContent = only.Answer +
                $"\n\n[gerado pela rede // {candidates.Length} de {candidateCount} máquina(s) responderam // não verificado — CP=0]";
            return BuildResult(soloContent, request.Prompt, only, FarmNetworkAiCatalog.PremiumEntry);
        }

        // Juiz: sintetiza a melhor resposta a partir das candidatas.
        var judgeQuestion = new StringBuilder();
        judgeQuestion.AppendLine("Você é um avaliador. Abaixo estão respostas independentes à mesma pergunta.");
        judgeQuestion.AppendLine("Escreva a MELHOR resposta possível, combinando o que há de mais correto e completo. Não mencione que existem candidatas.");
        judgeQuestion.AppendLine();
        judgeQuestion.AppendLine($"PERGUNTA: {request.Prompt}");
        for (var index = 0; index < candidates.Length; index++)
        {
            var answer = candidates[index].Answer;
            if (answer.Length > 3_000) answer = answer[..3_000];
            judgeQuestion.AppendLine($"\nRESPOSTA {index + 1}:\n{answer}");
        }
        var judgeInput = BuildGenerateInput(
            networkOptions.NetworkModel,
            judgeQuestion.ToString(),
            "Responda de forma objetiva e correta, em português.",
            null);
        FarmGenerateResult judged;
        try
        {
            judged = await RunGenerationAsync(judgeInput, request.Prompt, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // Juiz indisponível: devolve a candidata mais longa (heurística
            // simples) em vez de falhar — melhor-esforço.
            judged = candidates.OrderByDescending(x => x.Answer.Length).First();
        }
        var content = judged.Answer +
            $"\n\n[AI FARM PREMIUM // sintetizado de {candidates.Length} respostas da rede // não verificado — CP=0]";
        return BuildResult(content, request.Prompt, judged, FarmNetworkAiCatalog.PremiumEntry);
    }

    private static string BuildGenerateInput(
        string model, string prompt, string? systemInstruction, IReadOnlyList<AiContextMessage>? messages)
    {
        var context = new List<string>();
        var budget = 22 * 1024;
        foreach (var message in (messages ?? []).TakeLast(8))
        {
            var line = $"{message.Role}: {message.Content}";
            if (line.Length > 2_000) line = line[..2_000];
            if (budget - line.Length < 0) break;
            budget -= line.Length;
            context.Add(line);
        }
        return JsonSerializer.Serialize(
            new FarmGenerateInput(
                FarmWorkloadCatalog.GenerateInputSchema,
                model,
                prompt,
                systemInstruction,
                context),
            JsonOptions);
    }

    private async Task<FarmGenerateResult> RunGenerationAsync(
        string input,
        string prompt,
        CancellationToken cancellationToken)
    {
        var demand = await demands.SubmitAsync(
            new FarmDemandRequest(
                FarmWorkloadCatalog.GenerateId,
                DemandLabel(prompt),
                input,
                FarmDemandPriority.High,
                LifetimeMinutes: 5,
                TargetShards: 1,
                MaxAttemptsPerShard: 3),
            cancellationToken);

        var deadline = timeProvider.GetUtcNow().AddSeconds(110);
        var polls = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await demands.DownloadOutputAsync(demand.Id, cancellationToken);
            if (output is not null)
            {
                var payload = Encoding.UTF8.GetString(output.Content);
                return JsonSerializer.Deserialize<FarmGenerateResult>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("A rede devolveu uma geração vazia.");
            }
            if (++polls % 5 == 0)
            {
                var state = (await demands.GetDashboardAsync(cancellationToken))
                    .Demands.SingleOrDefault(x => x.Id == demand.Id);
                if (state is { Status: FarmDemandStatus.Failed or FarmDemandStatus.Expired })
                    throw new InvalidOperationException(
                        "A rede não conseguiu gerar: nenhuma máquina com o runtime local respondeu. " +
                        $"Instale o Ollama e baixe o modelo {networkOptions.NetworkModel} em um computador da pool. ({state.Detail})");
            }
            if (timeProvider.GetUtcNow() >= deadline)
                throw new InvalidOperationException(
                    "A rede não gerou a tempo. Verifique se um computador com o runtime local está FARMANDO.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static AiProviderResult BuildResult(
        string content, string prompt, FarmGenerateResult result, AiPriceCatalogEntry entry)
    {
        var inputUnits = result.PromptUnits ?? Math.Max(1, prompt.Length / 4);
        var outputUnits = result.CompletionUnits ?? Math.Max(1, result.Answer.Length / 4);
        var cost = Math.Max(1,
            (inputUnits * entry.InputCostPerMillionMinor +
             outputUnits * entry.OutputCostPerMillionMinor) / 1_000_000);
        return new AiProviderResult(content, inputUnits, outputUnits, cost);
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
