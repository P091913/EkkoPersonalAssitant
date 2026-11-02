using Ekko.Brain.Budget;
using Ekko.Brain.Desktop;
using Ekko.Brain.Llm;
using Ekko.Brain.Media;
using Ekko.Brain.Pipeline;
using Ekko.Brain.Tools;

namespace Ekko.Brain;

public static class EkkoServices
{
    /// <summary>Everything the Brain runs on. The eval runner calls this too, so it measures exactly what production runs.</summary>
    public static IServiceCollection AddEkko(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new ConversationStore(
            sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromMinutes(config.GetValue("Conversation:PendingMinutes", 3))));
        services.AddSingleton(sp => new TraceLog(
            Path.Combine(contentRoot, config["Trace:Path"] ?? "data/trace.jsonl"),
            sp.GetRequiredService<TimeProvider>()));

        var llm = config.GetSection("Llm").Get<LlmOptions>() ?? throw new InvalidOperationException("Llm config is missing.");
        foreach (var (step, models) in llm.Steps)
            if (!llm.Clients.ContainsKey(models.Primary) || models.Fallback is { } f && !llm.Clients.ContainsKey(f))
                throw new InvalidOperationException($"Llm:Steps:{step} names a client that isn't in Llm:Clients.");
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

        services.AddScoped<ToolCatalog>();
        services.AddScoped<Router>();
        services.AddScoped<DomainHandler>();
        services.AddScoped<MessagePipeline>();
        return services;
    }
}
