using Ekko.Brain.Budget;
using Ekko.Brain.Desk;
using Ekko.Brain.Desktop;
using Ekko.Brain.Food;
using Ekko.Brain.Llm;
using Ekko.Brain.Media;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;
using Ekko.Brain.Tools;

namespace Ekko.Brain;

public static class EkkoServices
{
    /// <summary>Everything the Brain runs on. The eval runner calls this too, so it measures exactly what production runs.</summary>
    public static IServiceCollection AddEkko(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        services.AddSingleton(TimeProvider.System);
        var conversation = config.GetSection("Conversation").Get<ConversationOptions>() ?? new ConversationOptions();
        services.AddSingleton(sp => new ConversationStore(sp.GetRequiredService<TimeProvider>(), TimeSpan.FromMinutes(conversation.PendingMinutes)));
        services.AddSingleton(sp => new ConversationMemory(sp.GetRequiredService<TimeProvider>(), conversation));
        services.AddSingleton(sp => new TraceLog(
            Path.Combine(contentRoot, config["Trace:Path"] ?? "data/trace.jsonl"),
            sp.GetRequiredService<TimeProvider>()));

        var llm = config.GetSection("Llm").Get<LlmOptions>() ?? throw new InvalidOperationException("Llm config is missing.");
        foreach (var (step, models) in llm.Steps)
            if (new[] { models.Primary, models.Fallback, models.Deep }.OfType<string>().Any(name => !llm.Clients.ContainsKey(name)))
                throw new InvalidOperationException($"Llm:Steps:{step} names a client that isn't in Llm:Clients.");
        // The agent loop uses Claude's native tool calling, which only the Anthropic client speaks.
        if (llm.Steps.GetValueOrDefault("server_ops") is { } opsStep && new[] { opsStep.Primary, opsStep.Fallback }.OfType<string>().Any(n => llm.Clients[n].Kind != LlmKind.Anthropic))
            throw new InvalidOperationException("Llm:Steps:server_ops must name Anthropic clients.");
        var anthropicKey = config["ANTHROPIC_API_KEY"]; // user-secrets in Development, environment in Docker
        foreach (var (name, options) in llm.Clients)
            services.AddHttpClient(name, http => LlmSetup.ConfigureHttp(options, http));
        services.AddScoped(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>();
            return new StructuredCall(LlmSetup.CreateClients(llm, http.CreateClient, anthropicKey), llm.Steps, sp.GetRequiredService<TraceLog>());
        });

        var pc = config.GetSection("PcService").Get<PcServiceOptions>() ?? throw new InvalidOperationException("PcService config is missing.");
        services.AddSingleton(pc);
        services.AddHttpClient<PcService>(c =>
        {
            c.BaseAddress = new Uri(pc.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(pc.TimeoutSeconds);
            if (!string.IsNullOrEmpty(pc.ApiKey))
                c.DefaultRequestHeaders.Add(ApiKey.Header, pc.ApiKey);
        });
        services.AddScoped<ITool, OpenApp>();
        services.AddScoped<ITool, DiscordToggleMute>();
        services.AddScoped<ITool, DiscordToggleDeafen>();
        services.AddScoped<ITool, AdjustVolume>();
        services.AddScoped<ITool, MediaPlayPause>();
        services.AddScoped<ITool, LockPc>();

        var tv = config.GetSection("TvRemote").Get<TvRemoteOptions>() ?? throw new InvalidOperationException("TvRemote config is missing.");
        services.AddHttpClient<TvRemote>(c =>
        {
            c.BaseAddress = new Uri(tv.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(tv.TimeoutSeconds);
            if (!string.IsNullOrEmpty(tv.ApiKey))
                c.DefaultRequestHeaders.Add(ApiKey.Header, tv.ApiKey);
        });
        services.AddScoped<ITool, PlayMedia>();
        services.AddScoped<ITool, PlayMediaItem>();
        services.AddScoped<ITool, MediaControl>();

        var budget = config.GetSection("Budget").Get<BudgetOptions>() ?? throw new InvalidOperationException("Budget config is missing.");
        services.AddHttpClient<BudgetService>(c =>
        {
            c.BaseAddress = new Uri(budget.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(budget.TimeoutSeconds);
            if (!string.IsNullOrEmpty(budget.ApiKey))
                c.DefaultRequestHeaders.Add(ApiKey.Header, budget.ApiKey);
        });
        services.AddScoped<ITool, GetBudgetStatus>();
        services.AddScoped<ITool, MarkBillPaid>();
        services.AddScoped<ITool, MarkRowPaid>();
        services.AddScoped<ITool, AddExpense>();

        var food = config.GetSection("Food").Get<FoodOptions>() ?? throw new InvalidOperationException("Food config is missing.");
        services.AddHttpClient<FoodService>(c =>
        {
            c.BaseAddress = new Uri(food.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(food.TimeoutSeconds);
            if (!string.IsNullOrEmpty(food.ApiKey))
                c.DefaultRequestHeaders.Add(ApiKey.Header, food.ApiKey);
        });
        services.AddScoped<CalorieEstimator>();
        services.AddScoped<ITool, LogFood>();
        services.AddScoped<ITool, LogNewFood>();
        services.AddScoped<ITool, GetFoodLog>();
        services.AddScoped<ITool, UndoFood>();
        services.AddScoped<ITool, SetFoodCalories>();
        services.AddScoped<ITool, SaveFoodCalories>();

        var desk = config.GetSection("Desk").Get<DeskOptions>() ?? throw new InvalidOperationException("Desk config is missing.");
        services.AddHttpClient<DeskService>(c =>
        {
            c.BaseAddress = new Uri(desk.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(desk.TimeoutSeconds);
            if (!string.IsNullOrEmpty(desk.ApiKey))
                c.DefaultRequestHeaders.Add(ApiKey.Header, desk.ApiKey);
        });
        services.AddScoped<ITool, AddDeskNote>();

        var ops = config.GetSection("ServerOps").Get<ServerOpsOptions>() ?? throw new InvalidOperationException("ServerOps config is missing.");
        services.AddSingleton(ops);
        services.AddHttpClient<IServerHost, DockerHost>(c =>
        {
            c.BaseAddress = new Uri(ops.DockerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(ops.ToolTimeoutSeconds);
        });
        services.AddScoped<ITool, ListContainers>();
        services.AddScoped<ITool, ReadContainerLogs>();
        services.AddScoped<ITool, GetDiskUsage>();
        services.AddScoped<ITool, FindLargeDirs>();
        services.AddScoped<ITool, RestartContainer>();
        services.AddSingleton(sp => new RunStore(Path.Combine(contentRoot, ops.RunsPath), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp =>
        {
            var clients = LlmSetup.CreateClients(llm, sp.GetRequiredService<IHttpClientFactory>().CreateClient, anthropicKey);
            IAgentModel? Pick(string? name) => name is not null ? clients.GetValueOrDefault(name) as IAgentModel : null;
            return llm.Steps.GetValueOrDefault("server_ops") is { } step ? new AgentModels(Pick(step.Primary), Pick(step.Fallback)) : new AgentModels(null, null);
        });
        services.AddScoped<ServerOpsAgent>();

        services.AddScoped<ToolCatalog>();
        services.AddScoped<Router>();
        services.AddScoped<DomainHandler>();
        services.AddScoped<Chat>();
        services.AddScoped<CurrentTurn>();
        services.AddScoped<MessagePipeline>();
        return services;
    }
}
