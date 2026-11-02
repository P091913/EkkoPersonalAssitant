using Ekko.Brain.Budget;
using Ekko.Brain.Desktop;
using Ekko.Brain.Llm;
using Ekko.Brain.Media;
using Ekko.Brain.Pipeline;
using Ekko.Brain.ServerOps;
using Ekko.Brain.Tools;

namespace Ekko.Brain.Tests;

public static class TestPipeline
{
    /// <summary>A pipeline whose route and extract steps both use <paramref name="llm"/> (default: unreachable) with no fallback.</summary>
    public static MessagePipeline Create(ConversationStore store, ToolCatalog catalog, TraceLog trace, ILlmClient? llm = null, ServerOpsAgent? agent = null)
    {
        var clients = new Dictionary<string, ILlmClient> { ["qwen"] = llm ?? new ScriptedLlm("qwen", [null]) };
        var steps = new Dictionary<string, StepModels> { ["route"] = new() { Primary = "qwen" }, ["extract"] = new() { Primary = "qwen" } };
        var call = new StructuredCall(clients, steps, trace);
        agent ??= new ServerOpsAgent(new AgentModels(null, null), catalog, new RunStore(Path.Combine(Path.GetTempPath(), $"ekko-runs-{Guid.NewGuid():N}"), TimeProvider.System),
            new ServerOpsOptions(), trace, TimeProvider.System);
        return new MessagePipeline(store, catalog, new Router(call), new DomainHandler(catalog, call), agent, trace);
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
