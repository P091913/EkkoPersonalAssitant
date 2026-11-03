using Ekko.Brain.Budget;
using Ekko.Brain.Desktop;
using Ekko.Brain.Food;
using Ekko.Brain.Llm;
using Ekko.Brain.Media;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public static class TestPipeline
{
    /// <summary>
    /// A pipeline whose route, extract and chat steps all use <paramref name="llm"/> (default: unreachable) with no fallback,
    /// and <paramref name="deep"/> in a deep session if given. Pass the same <paramref name="memory"/> to carry turns across pipelines.
    /// </summary>
    public static MessagePipeline Create(ConversationStore store, ToolCatalog catalog, TraceLog trace, ILlmClient? llm = null, ServerOpsAgent? agent = null,
        ConversationMemory? memory = null, ILlmClient? deep = null)
    {
        var clients = new Dictionary<string, ILlmClient> { ["qwen"] = llm ?? new ScriptedLlm("qwen", [null]) };
        if (deep is not null)
            clients["deep"] = deep;
        var steps = new[] { "route", "extract", "chat" }.ToDictionary(s => s, _ => new StepModels { Primary = "qwen", Deep = deep is null ? null : "deep" });
        var call = new StructuredCall(clients, steps, trace);
        agent ??= new ServerOpsAgent(new AgentModels(null, null), catalog, new RunStore(Path.Combine(Path.GetTempPath(), $"ekko-runs-{Guid.NewGuid():N}"), TimeProvider.System),
            new ServerOpsOptions(), trace, TimeProvider.System);
        memory ??= new ConversationMemory(TimeProvider.System, new ConversationOptions());
        return new MessagePipeline(store, memory, catalog, new Router(call), new DomainHandler(catalog, call), agent, new Chat(call, catalog), trace, new CurrentTurn());
    }

    /// <summary>Food tools whose estimate step asks <paramref name="estimate"/> (default: unreachable).</summary>
    public static ITool[] FoodTools(FakeHttp food, TraceLog trace, ILlmClient? estimate = null)
    {
        var service = new FoodService(food.Client("http://food/"));
        var call = new StructuredCall(new Dictionary<string, ILlmClient> { ["sonnet"] = estimate ?? new ScriptedLlm("sonnet", [null]) },
            new Dictionary<string, StepModels> { ["estimate"] = new() { Primary = "sonnet" } }, trace);
        return [new LogFood(service, new CalorieEstimator(call, new CurrentTurn())), new LogNewFood(service), new GetFoodLog(service),
            new UndoFood(service), new SetFoodCalories(service), new SaveFoodCalories(service)];
    }

    public static ITool[] DesktopTools(FakeHttp pc)
    {
        var service = new PcService(pc.Client());
        var options = new PcServiceOptions { Apps = new() { ["steam"] = "steam" } };
        return [new OpenApp(service, options), new DiscordToggleMute(service), new DiscordToggleDeafen(service),
            new AdjustVolume(service), new MediaPlayPause(service), new LockPc(service)];
    }

    public static ITool[] BudgetTools(FakeHttp budget)
    {
        var service = new BudgetService(budget.Client("http://budget/"));
        return [new GetBudgetStatus(service), new MarkBillPaid(service), new MarkRowPaid(service), new AddExpense(service)];
    }

    public static ITool[] MediaTools(FakeHttp tv)
    {
        var remote = new TvRemote(tv.Client("http://tv/"));
        return [new PlayMedia(remote), new PlayMediaItem(remote), new MediaControl(remote)];
    }
}
