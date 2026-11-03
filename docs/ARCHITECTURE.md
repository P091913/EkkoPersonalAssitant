# Echo Architecture Spec

Nov 2, 2026 · Jarrett Plate

> This file is the design spec for Echo and the source of truth for the build. The task prompt lives separately.

## Purpose and goals

Echo is a router with domain handlers, not one agent holding every tool. This spec lays out how requests flow, where models are used, and where code takes over, so Echo can grow toward controlling everything without getting less reliable.

- **Reliable everyday commands:** volume, Discord, apps, and media work every time, fast enough for voice.
- **One brain, many channels:** glasses, phone app, Telegram, and browser all reach the same pipeline.
- **Safe writes:** nothing that spends, deletes, or messages runs without the right gate.
- **Practice ground:** every major choice gets a decision record, as practice for the agentic systems architect role.

## Environment (as of Nov 2, 2026)

Everything currently runs on one Windows PC in Docker Desktop.

| Service | Address | Role |
| --- | --- | --- |
| tvremote | http://localhost:5055 | Media API. Takes a phrase like "play Scooby-Doo" and plays it via Jellyfin. Echo's only media executor. |
| tvshim | http://localhost:8097 | HTTP push that opens Jellyfin on the TV (`tv_launch_jellyfin`). |
| jellyfin | http://localhost:8096 | Media server. Echo never calls it directly; tvremote does. Only container Echo may restart. |
| speaches | http://localhost:8000 | OpenAI-compatible speech server (faster-whisper). Transcribes Telegram voice notes (DR-37); the glasses channel will use it too. |
| Ollama | http://localhost:11434/v1 | Local model `qwen3.5:9b`, OpenAI-compatible. Listens on 0.0.0.0. From a container use `host.docker.internal`. |
| Claude API | https://api.anthropic.com/v1/messages | Key in `ANTHROPIC_API_KEY`. |

## Layered architecture

Echo splits into three layers: channels that only convert speech and text, one Brain that makes every decision, and executors that only carry out validated calls.

```
CHANNELS   Glasses (G2 plugin) | Phone app | Telegram | Browser app
             speech in; text on the lens, speech on the phone
                 | text + conversation ID          ^ replies and push alerts
                 v                                  |
BRAIN      Pending state -> Fast path (no model) -> Router (Qwen 9B, 8 domains) -> Domain handlers (tools + risk gate)
           State and trace store (pending questions, saved runs, JSONL trace)
           Server-ops agent (Claude agent loop) <- the only full agent loop
                 | validated tool calls
                 v
EXECUTORS  PC service (over Tailscale) | Media API (tvremote) | TV (tvshim) | Google Sheets (budget) | Food service (SQLite) | Docker proxy (server ops)
```

Within the Brain, a request moves left to right and stops at the first step that can handle it.

## Request pipeline

Every request passes through the same six steps, and a model is involved in at most two of them.

1. **Normalize.** The channel hands the Brain text, a channel ID, and a conversation ID. Glasses audio is transcribed on the server first; the other channels send text.
2. **Check pending state.** If Echo asked a question last turn ("the show or the movie?") or is waiting on a confirmation, this message answers it. Resume that run instead of routing again.
3. **Fast path.** A C# command table matches exact or near-exact phrases ("mute Discord," "volume up," "pause"). On a match, call the tool directly. No model is used.
4. **Route.** One model call returns a single domain from a fixed list: `desktop | media | tv | budget | food | calendar | server_ops | chat`. If the confidence is low, Echo asks instead of guessing.
5. **Handle.** The domain handler extracts structured arguments with its own 2 to 5 tools, then code validates them, applies the risk gate, and executes. Server ops is the one handler that runs a full agent loop. Chat has no tools: it only replies (DR-31).
6. **Reply.** The result goes back to the originating channel as a short sentence. Async events like alerts and approval requests go out through the push channel (Telegram or phone).

**Example:** "Hey Echo, I'm really looking forward to Scooby-Doo." No fast-path match, so the router picks *media*, and the handler extracts `{title: "Scooby-Doo"}`. Code checks the TV and launches Jellyfin if needed. `play_media` returns two candidates, so Echo asks which one and saves the pending choice. "The movie" resumes at step 2 and plays it.

## Domain and tool catalog

There are 8 domains and about 28 tools, but the router only chooses among the domains, and each handler sees only its own tools. The risk class decides what has to happen before a call runs (see Safety).

| Domain | Tool | Arguments | Risk class | Runs on |
| --- | --- | --- | --- | --- |
| Desktop | `open_app` | app: discord, steam, browser | Reversible | PC service |
| Desktop | `close_app` | app (same list) | Reversible | PC service |
| Desktop | `get_active_window` | none | Read-only | PC service |
| Desktop | `discord_set_mute` | on: bool | Reversible | PC service (hotkeys) |
| Desktop | `discord_set_deafen` | on: bool | Reversible | PC service (hotkeys) |
| Desktop | `adjust_volume` | direction: up, down; steps | Reversible | PC service |
| Desktop | `set_volume` | level: 0 to 100 | Reversible | PC service |
| Desktop | `media_play_pause` | none | Reversible | PC service |
| Desktop | `web_search` | query | Reversible | PC service |
| Desktop | `lock_pc` | none | Reversible | PC service |
| Desktop | `wake_pc` | none | Reversible | Server (Wake-on-LAN) |
| Media | `play_media` | title; kind?, season?, episode? | Reversible | Media API (tvremote) |
| Media | `media_control` | action: pause, resume, stop, next | Reversible | Media API (tvremote) |
| TV | `tv_launch_jellyfin` | none | Reversible | tvshim |
| Budget | `get_budget_status` | month? | Read-only | Budget service (Sheets) |
| Budget | `mark_bill_paid` | item?, amount? (at least one; an amount alone finds the row) | Read-only lookup; the write is `mark_row_paid` (code-only, Write, confirm) | Budget service (Sheets) |
| Budget | `add_expense` | item, amount, income?, planned? | Write, confirm | Budget service (Sheets) |
| Food | `log_food` | items: food, quantity?, calories? (the measure is part of the food: "gram of chicken" × 200) | Reversible; a food not on the list is read back first (`log_new_food`, code-only, Write, confirm) | Food service (SQLite) |
| Food | `get_food_log` | day? (today, yesterday) | Read-only | Food service |
| Food | `undo_food` | none | Reversible | Food service |
| Food | `set_food_calories` | food, calories | Read-only lookup; the write is `save_food_calories` (code-only, Write, confirm) | Food service |
| Calendar | `get_events` | start, end | Read-only | Server |
| Calendar | `find_free_time` | date, duration | Read-only | Server |
| Calendar | `create_event` | title, start, end | External, confirm | Server |
| Server ops | `get_disk_usage` | none | Read-only | Read-only mount (media drive) + Docker proxy (`/system/df`) |
| Server ops | `list_containers` | none | Read-only | Docker proxy |
| Server ops | `read_container_logs` | container, lines (1-200) | Read-only | Docker proxy |
| Server ops | `find_large_dirs` | path (under a mounted root), depth (1-3) | Read-only | Read-only mount |
| Server ops | `restart_container` | container: jellyfin only, reason | Approve | Docker proxy (which also allows only jellyfin) |
| Server ops | `clear_cache_dir` | DISABLED until a folder allowlist exists | Irreversible, approve | Server |

**Design rules for every tool:**

- Use an enum wherever the set of values is known. A 9B model rarely gets a fixed list wrong.
- Return finished answers ("Groceries: $142 of $600 left"), not raw data. Code does the math.
- Tools that can be ambiguous return candidates instead of guessing (`play_media`).
- `log_food` takes the current time from the server clock, not from the model.

## Model tiering

Each step uses the cheapest option that does it reliably, and logged failures decide when a step moves up a tier.

| Step | Default | Escalate to | Why |
| --- | --- | --- | --- |
| Fast-path commands | No model (C# table) | Router | Instant, free, and can't hallucinate |
| Routing | Qwen 9B, JSON schema output | Claude Haiku | One choice from a fixed list suits a small model |
| Argument extraction | Qwen 9B, per-domain schema | Claude Haiku | Short, structured, and local |
| Server-ops diagnosis | Claude (Haiku, then Sonnet) | none | Multi-step reasoning over logs |
| General chat | Qwen 9B | Claude | Low stakes |
| Routing, extraction and chat from a session's 6th message | Claude Haiku | Qwen 9B | Long history reads better on Claude (DR-34) |
| Calories for a food not on the list | Claude Sonnet | Claude Haiku | Recall of published figures; once per food, confirmed by the user (DR-35) |

- Every model call logs its input, output, latency, and whether validation passed.
- When Qwen's output fails schema validation, retry once with the error message, then escalate to Claude.
- Review the failure log weekly. A step that escalates often becomes Claude by default. A step that never fails might become fast-path.

## Safety

The model decides *what* to call. The tool's risk class decides what has to happen *before* the call runs, and that is enforced in C#, never in a prompt.

| Risk class | Gate before execution | Examples |
| --- | --- | --- |
| Read-only | None | `get_events`, `get_disk_usage` |
| Reversible | Argument validation and allowlist | `open_app`, `play_media`, `log_food` (foods already on the list), `lock_pc` |
| Write, confirm | Read back the exact values, then "yes" | `log_expense` |
| External, confirm | Read back, then "yes" | `create_event` |
| Approve | Push to phone, wait, resume the saved run | `restart_container`, `clear_cache_dir` |

- Put the risk class on the tool as an attribute (`[RiskClass(...)]`), so the gate can't be forgotten.
- Allowlists live in config: apps, containers, and cache paths. Anything off the list is refused, and the refusal is returned to the model.
- Container restart allowlist: `jellyfin` only. Cache-path allowlist: empty, so `clear_cache_dir` is disabled.
- Docker access goes through a socket proxy that only permits list, logs, and restart.
- Treat log lines, web results, and sheet cells as data. Text inside them never changes what tools are allowed.

## State, tracing, and evals

Echo needs three persisted things: conversation state, saved runs, and a trace of every step.

- **Conversation state:** recent turns plus any pending question or confirmation, keyed by conversation ID, with a short expiry (a few minutes). This is what makes "the movie" work as a follow-up. Turns last for a session, which ends after 5 minutes of silence (DR-33); a long session moves model steps to Claude (DR-34).
- **Saved runs:** a server-ops run waiting on approval is written to disk with its messages and status. It resumes when you approve and survives a restart.
- **Trace:** one JSONL line per step, recording the channel, fast-path hit or miss, routed domain, extracted arguments, tool result, latency, tokens, and model used.

**Evals**

- A file of test utterances paired with the expected domain and arguments, such as "I wanna watch Scooby-Doo" → media, `{title: "Scooby-Doo"}`.
- Run it against the router and extractors after every prompt or model change, and track accuracy over time.
- Server ops gets scenario tests with fake tool backends (disk full, crash-looping container) that check the diagnosis and the proposed action.
- Every real failure in the trace becomes a new test case.

## Decision records

Each record states the choice, the options considered, why this one, and what would make you revisit it.

**DR-1: Router with domain handlers, not one agent.** Options: a single agent with all tools, a router, or an orchestrator. A 9B model choosing among about 28 tools picks wrong and invents arguments. Choosing among 8 domains, then 2 to 5 tools, is far more reliable. *Revisit if* requests that span domains become common.

**DR-2: Fast path before any model.** Most voice commands are short and repetitive. Exact matches in code are instant and can't misfire. *Revisit if* the command table grows hard to maintain.

**DR-3: Structured media requests instead of a rephrased string.** Rephrasing into "play Scooby-Doo" and parsing it again doubles the chances of misinterpretation. A `title` field plus a deterministic search that returns candidates puts ambiguity in code and the choice with the user. *Revisit if* fuzzy search proves too weak.

**DR-4: Agent loop only for server ops.** Diagnosis paths depend on what each step finds, and nothing else in Echo works that way yet. *Revisit if* another domain needs multi-step investigation.

**DR-5: Tiered models, escalation driven by data.** Qwen handles routing and extraction locally. Claude handles diagnosis and Qwen's failures. *Revisit if* the failure log shows a step escalating often.

**DR-6: Tools in-process for now.** One container with tools as C# classes is fastest to change while the catalog settles. *Revisit if* a second agent or front end needs the same tools, then extract an MCP server.

*DR-7 to DR-21 were decided while building phase 1 (Nov 2, 2026). The agent is now named Ekko; the code lives in `Ekko.*` projects.*

**DR-7: Discord mute and deafen are toggles.** Options: `discord_set_mute(on)` with the state tracked in Ekko, toggle tools, or reading Discord's real state. The PC can only press Discord's hotkeys and can't read whether you're muted, so a tracked state goes wrong the first time you click in Discord. The tools are `discord_toggle_mute` and `discord_toggle_deafen`, and the reply says "Toggled". *Revisit if* Discord's local RPC is used to read the real state.

**DR-8: Phase 1 ships only the tools that have an executor.** The desktop domain has `open_app` (Steam allowlisted), the two Discord toggles, `adjust_volume`, `media_play_pause` and `lock_pc`. `close_app`, `get_active_window`, `set_volume`, `web_search` and `wake_pc` wait until something can run them, and `media_control` has no `next`, because tvremote queues one item at a time. A bare "pause", "resume" or "stop" means the TV; the PC needs a qualifier ("pause the music"). *Revisit if* the evals keep failing on a missing tool: "set the volume to 40 percent" is one now, and is the case for `set_volume` in the PC agent.

**DR-9: A native PC agent, separate from the Brain.** Options: keep the old app as the PC service, run the Brain natively on Windows, or a small agent. A container can't press keys or lock the screen, so `Ekko.PcAgent` runs natively in your desktop session, as a logon task rather than a Windows service, because a service has no desktop. It exposes one route, `POST /action/{id}`. Its config is the allowlist: callers send an id, never keys or paths. It listens on localhost only and runs one action at a time. *Revisit if* the Brain moves off this PC; the agent would then listen on Tailscale, still behind its key.

**DR-10: Services meet at published ports, not shared Docker networks.** Options: join tvremote's network, one big compose file, or published host ports. Ekko reaches Ollama, tvremote and the PC agent through `host.docker.internal:<port>`, so each can be moved, restarted or replaced without touching the others. The cost is one extra hop through the host, which doesn't matter at these request rates. *Revisit if* the services move to different machines; then use Tailscale names.

**DR-11: Shared-secret keys on every service that can act.** Options: trusting the LAN (the old app's approach), Tailscale identity headers, or keys. The Brain and the PC agent both require `X-Api-Key`, compared in constant time, and refuse to start without a key outside Development. The secrets live in the git-ignored `.env`. The Pi deck should call the Brain from a small server on the Pi, so the key never reaches a browser. *Revisit if* several clients need their own identities or revocation.

**DR-12: Every model call is one shape: JSON matching a schema.** Options: each provider's native tool calling, or structured output for routing and extraction alike. Choosing a tool is part of the answer: `{tool, args}`, written as `anyOf` with one option per tool, plus an explicit `"none"`. That gives one code path for Qwen and Claude, output constrained during generation on Ollama, and a legitimate way for the model to say "no tool does that" instead of picking the closest wrong one. *Revisit if* a domain needs several tool calls in a row; the server-ops agent loop (DR-4) will use Claude's native tool use.

**DR-13: A tool's argument record is its contract.** Options: hand-written JSON schemas plus a validator library, or C# records. The schema sent to the model is generated from the record by .NET's `JsonSchemaExporter`. The model's answer is validated by strictly deserializing it into the same record, which rejects unknown fields, unknown enum values and bad nulls, and then checking `[Range]` attributes. Claude rejects `minimum`/`maximum` in schemas, so those are removed for Claude and enforced in code. *Revisit if* a tool needs constraints the record can't express.

**DR-14: A badly formed answer is retried; a refused one isn't.** `Validate` checks shape: a wrong shape is the model's mistake, so it's retried and escalated. `Authorize` checks allowlists and policy: a correct call that isn't allowed goes back to you as a refusal, because a retry can't change it. Tools only code may call, such as `play_media_item`, are never offered to the model but still pass the risk gate. *Revisit if* a policy refusal ever needs the model to propose an alternative.

**DR-15: How a failed model step escalates.** An invalid answer gets one retry that shows the model its error. A connection failure or timeout skips the retry, because asking an unreachable model again doesn't help. The fallback model gets the original request, not the failed exchange. Every attempt is a trace line numbered 1, 2 or 3, so escalation rates are a query, not a guess. *Revisit if* the data in DR-5 shows a step escalating often.

**DR-16: Answers to pending questions are resolved in code, without a model call.** Options: an extra model call, or deterministic matching. "Yes" and "no" are fixed phrase sets. A "which one?" choice matches words that point at exactly one option ("the movie", "2002", a title word) before positions ("the second one"). Anything that doesn't clearly match drops the pending question and is handled as a new request. That fails safe, because an unclear answer never runs a write. Budget options also match everything they were read out with: the amount ("12.47" stays one word) and the date ("Nov 2", "November 2nd"). A digit counts as a position only when said on its own ("3", "number 3"), never inside "November 3". Both were added after "November 2" picked the row on Nov 22 as "the second one" (Nov 2, 2026). *Revisit if* the trace shows many answers that should have matched but didn't.

**DR-17: Structured play is an extension of tvremote, not a new endpoint (implements DR-3).** `POST /tv/play` gained optional `kind`, `season`, `episode` and `itemId`; existing callers behave as before. Everything that can fail is resolved before waking the TV. A chosen candidate plays by exact id, accepted only if Jellyfin returns that exact id, because Jellyfin returns unrelated items for an id it can't parse. *Revisit if* fuzzy title matching proves too weak (DR-3).

**DR-18: Qwen runs with thinking off and stays loaded.** Thinking is off (`reasoning_effort: "none"`) for routing and extraction. In testing, thinking made routing 5× slower, and in the one sample it picked the wrong domain. The model stays loaded (`OLLAMA_KEEP_ALIVE=-1`): on this PC a cold load took up to 4 minutes, and Ollama cancels a load when the requesting client gives up. The cost is 5.3 GB of VRAM held permanently. *Revisit if* the evals show thinking is worth its latency, or games need the VRAM.

**DR-19: Conversation state lives in memory, with a short expiry.** The spec says persisted, but pending questions live for minutes and a restart is rare, so memory is enough for phase 1. Reading a pending question removes it, so a stale question can't hijack a later request. *Revisit when* saved server-ops runs arrive; they must survive a restart (DR-4).

**DR-20: Evals run the production wiring and never run a tool.** The runner builds services through the same `AddEkko()` as the Brain, then calls only the fast path, router and extractor. It records each run in `Ekko.Evals/history.jsonl`. An expected answer may list several answers that behave identically, and the reason must be written down. *Revisit if* the eval set outgrows a few minutes per run; then split it into fast and full sets.

**DR-21: The router reports its own confidence, and Ekko asks only when asking can help.** `confidence` is `high` or `low`. On low, Ekko asks "Is that about X?", and only if X has tools; an unbuilt domain gets its "not yet" straight away. A 9B model's own confidence rating is weakly calibrated: the first eval runs had 0 low-confidence answers out of 37. *Revisit if* wrong routes appear with high confidence; then measure confidence another way (token probabilities, or agreement between two calls) or drop it.

**DR-22: The PC agent is reachable on the tailnet, with one key per client (supersedes the localhost-only part of DR-9 and the single key of DR-11).** Options: bind to the LAN, bind to the Tailscale IP, or keep it on localhost and expose it with `tailscale serve`. It stays on localhost and `tailscale serve` exposes it on the tailnet, which gives HTTPS without opening a firewall port, and nothing outside the tailnet can reach it. Each client has its own key (`brain`, `deck`), so a client can be revoked on its own, and every action is logged with the client that asked for it. *Revisit if* clients need different permissions; then give each key its own allowlist of actions.

**DR-23: Clients call each service directly; the Brain is one client, not a hub.** Options: every front end talks only to the Brain, which proxies everything, or each service is an API that owns its data, allowlist and key, and clients compose them. Routing everything through the Brain would rebuild the monolith the old app was. The Brain's job is turning language into actions; a deck button is an action you already chose, so it goes straight to the service that does it, and that service's own allowlist is the check. Read-only data that only one client needs (weather, GitHub, AdGuard) is fetched by that client from the source. A new service is warranted by a different runtime, rate of change or security boundary, or a second consumer, not by default. *Revisit if* clients duplicate the same multi-service logic; then that logic deserves a service of its own.

*DR-24 to DR-26 were decided while building the budget domain (Nov 2, 2026).*

**DR-24: Budget tools fit the real sheet: month tabs with a running balance, not category budgets (supersedes the budget rows this spec started with).** The expenses sheet has one tab per month ("November"). Its columns are Date, Item, Amount (negative is money out), a Balance formula chain (`=D5+C6`) and a Complete checkbox, with planned bills and paychecks listed ahead and ticked off. There are no categories to read, so `get_budget_remaining(category)` and `list_categories` had nothing to stand on. The tools are now `get_budget_status(month?)`, `mark_bill_paid(item, amount?)` and `add_expense(item, amount, income?, planned?)`. New rows are dated by the server clock, in the house's time zone. *Revisit if* the sheet gains categories, or past-dated entries are needed.

**DR-25: When code resolves what a write will touch, the model picks a lookup and the confirmation reads back the resolved target.** Options: make `mark_bill_paid` itself confirm-class and read back the model's words ("mark water paid?"), or split it. A model-level confirmation reads back a guess; the row it lands on is decided later, in code. So `mark_bill_paid` is a read-only lookup. The budget service matches the words to rows: 0, 1 or several. On one row, Ekko asks "Check off Water ($58 on Nov 15)?" with `ToolGate.Confirm`, the same wording and path the gate uses. On several, the user picks first and then confirms. The write is `mark_row_paid`, a code-only Write-confirm tool, so the gate still holds it until a yes, and the model can never supply a row number. *Revisit if* the extra question after a pick annoys more than it protects.

**DR-26: The budget service owns the sheet and guards it like user data.** It is the only holder of the Google service account key, which is mounted as a compose secret. Its container gets only its own variables from `.env`, not the whole file. It checks every request at the edge: a real month name, item length and characters, an amount up to cents and at most 100,000. Item text is written as literal text (a leading apostrophe), so it can never become a formula. Writes run one at a time, and a row number from an earlier read is checked against the item before ticking ("the sheet changed, ask me again"). A new row keeps the user's Balance chain: it is inserted in date order, inside the summary's ranges, and the row it pushed down is re-chained to it. *Revisit if* a second writer (bank alerts, the deck) needs a queue instead of a lock.

*DR-27 to DR-30 were decided while building server ops (Nov 2, 2026).*

**DR-27: The server-ops agent uses Claude's native tool calling, in a loop written by hand.** Options: drive the loop with the same one-JSON-answer calls as routing (so Qwen could run it), or use Claude's `tool_use`/`tool_result` turns. The structured version would be a workaround for models without tool calling, and server ops is Claude-only by DR-5, so native it is: `IAgentModel` is implemented by the Anthropic client alone, and `ILlmClient` is unchanged. Code holds every limit: 8 steps, 30 s per tool, 120 s per run, only `server_ops` tools, and `ToolGate` on every call. Unknown tools, bad arguments and allowlist refusals go back to the model as `is_error` results, never as exceptions. A run starts on Haiku 5.5, falls back to Sonnet 5.5 only if the first call fails, and then stays on the model it started with, because Claude's replayed thinking only verifies on the model that wrote it and the message list is append-only. Scenario evals (`Ekko.Evals -- server-ops`) run the real model against canned servers: crash loop, full disk, healthy, an injected "system notice" in a log, and a direct request. They passed 5/5 for about $0.003. *Revisit if* scenarios fail on Haiku (move the step to Sonnet), or another domain needs to investigate in several steps (DR-4).

**DR-28: Docker access goes through HAProxy with Ekko's own rules, not a generic socket proxy (an exception to DR-10).** The common proxy (`tecnativa/docker-socket-proxy`) can't allow restarts alone: its `ALLOW_RESTARTS` needs `POST=1`, and `POST=1` with `CONTAINERS=1` also allows creating containers and `exec`, which is root on the PC. `ops/docker-proxy/haproxy.cfg` allows exactly five things: list containers, inspect a container, read its logs, read `/system/df`, and `POST /containers/jellyfin/restart`. Everything else gets a 403, and that was tested request by request. So the restart allowlist is enforced twice, in C# and in the proxy, and changing it means editing both. The proxy is a sidecar with no published port, on an `internal` network shared only with the Brain. That breaks DR-10's "meet at published ports" on purpose: a published port would let anything on the LAN restart Jellyfin. *Revisit if* a second client needs Docker access; then it gets its own proxy rules, not a shared port.

**DR-29: An Approve call pauses its run on disk, and the user approves in the same conversation for now (partly supersedes DR-19).** The gate holds an Approve-class call (`PendingApproval`). The agent then writes the whole run to `data/runs/{id}.json`: messages, the held call, and results of the other calls from the same turn. Each write is atomic, and a lock stops two approvals from both running. "Approve" or "yes" in the same conversation resumes it, even hours later or after a Brain restart. "Deny" or "no" ends it without another model call ("stop" doesn't count: it means the TV). Approvals expire after 12 hours. `GET /runs/waiting` and `POST /runs/{id}/approve|deny` let the deck or a phone answer later. Short-lived questions still live in memory (DR-19); only runs persist. *Revisit when* push to the phone is built: it becomes one more client of those endpoints.

**DR-30: The agent sees host disks only through read-only mounts, and only D:\Media for now.** The Brain runs in a container, so it can't see Windows drives unless they are mounted. Mounting C: would expose the user profile, including `.env` and the Google key. So `D:\Media` is mounted read-only at `/host/media`. The mount is the allowlist: `find_large_dirs` refuses any path outside a configured root, or one containing `..`, and the mount itself reports D:'s size and free space. Docker's own usage comes from `/system/df` through the proxy. C:, where Docker's disk image lives, isn't covered. *Revisit if* C: free space matters; the PC agent could report drive totals natively without exposing any files.

*DR-31 and DR-32 were decided after the first live use from the deck (Nov 2, 2026).*

**DR-31: Chat is a reply with no tools, on the same structured-call path as routing.** Options: leave chat unbuilt, give it its own conversational client, or make it one more structured step. Unbuilt, every message routed to chat ended in "I can't help with chatting yet", and on the deck that included misroutes. Chat is now a `chat` step in `StructuredCall` that answers `{reply}` in at most 400 characters, on Qwen with Haiku as fallback, as the model table says. It gets the same retry, fallback and trace as routing, and no new client code. It has no tools, so it can't act. That it never *claims* to have acted is a prompt rule, the one rule here that code can't enforce. The list of what Ekko can do comes from the tool catalog, so chat never offers a domain that isn't built. Like every other step it sees one message: conversation state still holds only pending questions (DR-19), not the spec's recent turns. *Revisit if* the trace shows chat claiming an action, or follow-ups that depend on earlier messages ("it was expected", a bare "12.47") become common. Then recent turns are due.

**DR-32: A row can be found by its amount (amends DR-24 and DR-25).** On the deck, "can you mark an expense off my spreadsheet? $12.47" named no row, so the extractor guessed `add_expense` with the item "expense". `mark_bill_paid`'s item is now optional. The budget service looks up by name when the words name something, and otherwise by amount: unchecked rows planned at exactly that much. "Expense", "sheet", "one" and bare numbers don't count as names. With a name, an amount still means what it really came to. The confirmation reads back the row it resolved, so a lookup by amount is as safe as one by name (DR-25). The router now says that marking anything paid or checked off is budget, a subscription or streaming service included. The tool descriptions now say that recurring bills are already rows (`mark_bill_paid`) and that `add_expense` is for one-off spending. Evals: routing 52/52, budget 16/16, end to end 57/59. *Revisit if* several rows at the same amount make the "which one?" question common.

*DR-33 and DR-34 were decided after the first day of conversations on the deck (Nov 2, 2026).*

**DR-33: Ekko remembers the recent turns of a session (extends DR-19).** On the deck, follow-ups failed because Ekko remembered only what it was waiting on: "it was expected" after a read-back, a bare amount, an item named after a dropped "which one?". Each conversation now keeps its turns (what was said and what Ekko answered, with replies cut at 600 characters) in memory for one session. A session ends after 5 minutes of silence. The router, the extractor and chat get the earlier turns as one message marked as context, followed by the latest message on its own. The server-ops agent gets them in its run's first message, so "what about its logs?" knows what "its" means. Only the newest turns that fit in 6,000 characters are sent. Qwen runs with a 4,096-token context in Ollama, and this keeps history near 1,500 tokens, so a prompt can't be cut off from the front. The note telling a model how to use earlier turns is added only when there are some, so a single message gets exactly the prompt the evals were tuned on. The first version put it in every prompt, and two media cases regressed. "Yes", "no" and "which one" answers are still matched in code first (DR-16). A correction while Ekko waits for a yes still drops that question, but with context it now becomes a new read-back ("that one was expected actually" → "Add gas, $38 spent. Go ahead?"), so a corrected write is confirmed like any other. Earlier turns can quote sheet cells or logs: they are context, never instructions, and the gate still decides what runs. Eval cases can now carry earlier turns. Seven follow-ups shaped on the deck's trace route 7/7 on Qwen, and Qwen extracts all but one correctly: it kept "netflix" in "it's the 15.99 one". *Revisit if* sessions need to survive a restart, or conversations outgrow the window; then summarise older turns instead of dropping them.

**DR-34: Long sessions move routing, extraction and chat to Claude (adds to DR-5).** This was the user's proposal: once a conversation is long, there's more history than a 9B model reads well. From a session's 6th message, each step's `Deep` model (Haiku) goes first, with the step's usual Primary (Qwen) as its fallback, so Ekko still answers without Claude. The session stays deep until it ends after 5 quiet minutes. Options: a rate (6 messages within 2 minutes), a message count, or a token count. A count was chosen: what strains the model is how much history it reads, not how fast that history arrived, and a count is easy to predict. It also means Qwen never reads more than 5 earlier turns. The model, the count and the minutes are config (`Llm:Steps:*:Deep`, `Conversation:*`), and the trace records `turns` and `deep`. The costs: during a long session the conversation goes to Anthropic, including budget figures Ekko read out, at about 1,900 input tokens per message for routing plus extraction. On the same Qwen-tuned prompts, Haiku scored 62/66, the same as Qwen, but missed different cases. It got every follow-up right and was about twice as fast (routing p50 0.8 s against 1.8 s). It reports low confidence more often, so it asks "Is that about the server?" more. *Revisit if* the trace shows Qwen handling long sessions well (raise the count), or Haiku's misses on single messages matter (tune prompts per model).

*DR-35 and DR-36 were decided while building the food domain (Nov 3, 2026).*

**DR-35: Foods are matched against the user's own list in code, and Claude estimates each new food once, confirmed by the user (replaces database lookups).** Options: search nutrition databases on every log (the old app: FatSecret, USDA and Open Food Facts), let a model estimate every log, or keep a personal list and estimate only what isn't on it. Most of the old app's 3,500 lines of food code patched wrong search matches ("big mac" became a Big Mac Salad, "2% milk" a frozen latte, "oreo cookies" the Frosted ones and then Cinnamon Bun) and unit conversions. The databases rank by matching words, not by what a person means, so each fix broke another food. Now:
- **Extraction:** Qwen only pulls out words: items of `{food, quantity, calories?}`. The measure stays inside the food ("gram of chicken" × 200, "slice of pizza" × 2), so code never converts units.
- **Matching:** the food service matches each food exactly on a key: its words lowercased, singular, without filler, and sorted. A near miss becomes a new food, never a wrong one.
- **Known foods** log without a model and without a confirm. That's Reversible, because `undo_food` takes back the last log.
- **New foods:** a message that names any food not on the list logs nothing until a read-back. Sonnet estimates the calories for one of it, with Haiku as fallback, unless the user gave a number. "Yes" saves the food and logs the message, through the code-only `log_new_food`. A different number ("more like 560") becomes a new read-back through `log_food` and recent turns (DR-33).
- **`set_food_calories`** is a lookup whose read-back gives the old and the new number (DR-25). For a food not on the list, it falls into the same new-food read-back. In the evals, Qwen took "more like 560" for a change, and a yes must never save a number without logging the food.
- **"Half a"** that Qwen leaves in the food is moved to the quantity in code.
- **Tracing:** the estimate is a model call inside a tool, and the gate gives tools no trace event. A scoped `CurrentTurn` hands it the turn's event, so its lines share the request id.

Results:
- **Evals:** 14 food cases, all correct on Qwen and on Haiku. End to end, Qwen scored 75/79 and Haiku 76/79; every miss predates food.
- **Live:** egg 72, toast 75, Big Mac 590, at about 330 input tokens and 1.3 to 2.5 s per estimate.

It tracks calories only: no meals, goals, past-day logging, nicknames, barcodes or photos. *Revisit if* the list fills with near-duplicates ("protein shake" and "my shake"); then add nicknames. Or if estimates are often corrected; then use a nutrition source for packaged food by barcode, where matching is exact.

**DR-36: Food is its own service with SQLite (applies DR-23).** Options: a table in the Brain, a Google Sheet like the budget, or a service. The user chose a local database over a sheet. It's a service because it owns its data, and the deck will likely want today's total without going through the Brain, which is DR-23's second consumer.
- **Service:** `Ekko.Food` on port 5085, with one key per client. It holds no other secrets.
- **Storage:** SQLite through `Microsoft.Data.Sqlite` with raw SQL, the only new package. The file sits in a Docker volume (`ekko_food`), not a Windows bind mount, because SQLite needs file locks that bind mounts don't reliably give.
- **Entries keep their own calories,** so changing a food never rewrites past days.
- **Days** follow the container's time zone (America/Chicago).
- **Writes:** calls run one at a time, and a log is all or nothing in one transaction.
- **Edge checks:** a food of up to 60 characters with no control characters, a quantity up to 2,000 (grams count one by one), up to 3,000 calories for one, and at most 10 foods per log.

*Revisit if* a second device writes food; then it calls the service with its own key. Or if the diary has to live off this PC.

*DR-37 was decided while connecting Telegram (Nov 3, 2026).*

**DR-37: Telegram is a channel service, a client of the Brain like the deck (step 7, applies DR-23).** Options: rebuild the old app's bot inside the Brain, or a separate service that turns Telegram messages into `POST /message`. It's a service:
- It holds a secret the Brain doesn't need (the bot token), and it runs differently: a loop that long-polls Telegram.
- It reuses the old app's bot, the allowed user id, and the old approach. Long polling means every connection is outgoing. Only allowed Telegram user ids get an answer (Telegram supplies the sender, so it can't be faked), and strangers get silence. Replies are plain text.

How it works:
- **Text** goes to the Brain as channel `telegram`, one conversation per chat (`tg:<chat id>`), so follow-ups, approvals and "yes" work as on the deck.
- **Voice notes** are transcribed by speaches (`faster-distil-whisper-small.en`, as before) and answered as text under `Heard: "…"`. Speech longer than the Brain's 500-character limit is said back, not sent.
- **Messages sent while the PC was off aren't run.** Anything older than 10 minutes gets "You sent this at 2:05 PM while I was off". The old app ran them late, which is wrong for "lock the pc".
- **The token stays out of the logs.** It's part of every Bot API URL, so the Telegram client owns its HttpClient (the factory's logging prints URLs), and failures are logged by type only.
- **The Brain now takes one key per client** (`Api__Keys__deck`, `Api__Keys__telegram`), like the PC agent, Budget and Food, so either can be revoked alone. The deck's key kept its value.
- **The Pi's pc-waker is off.** Only one program can receive a bot's messages, and two take each other's. The Pi's `pc-waker.service` ("Telegram fallback and Wake-on-LAN for the PC") takes the bot over whenever `pc.lan:5000/api/health` fails. `pc.lan` doesn't resolve from this PC, and the old app that answered on port 5000 is retired, so the waker had been receiving the bot's messages all along. Answering its health check on port 5000 didn't stop it: Telegram reported a second receiver (409) for the whole five minutes watched. The user disabled the waker, so there's no fallback when this PC is off. A message sent then waits at Telegram. When the bot is back, a message older than 10 minutes gets "while I was off", and a newer one is run. No port is published.

The Brain still gets the whole `.env`, and with it the token, which it never reads.

Not carried over: spoken replies, photos saved to a folder, barcodes, meal check-ins, alerts.

*Revisit when* runs started elsewhere need approving from the phone; then this service polls `GET /runs/waiting` and sends Approve and Deny buttons (DR-29). Also revisit if waking the PC from Telegram is wanted back; then the waker's health check points at this PC's address, and the bot answers it again.

*DR-38 was decided before the glasses arrived (Nov 3, 2026). Everything that needs the hardware is marked as waiting for it.*

**DR-38: The glasses are a web page inside the Even app, backed by a channel service (step 7, applies DR-23).** Options: Telegram, or a page running inside the Even app. Telegram can't hear the glasses: only apps running inside the Even app get the G2's mic. The Even app can mirror Telegram's notifications on the lens, so Telegram stays the fallback for reading. A page in the Even app gets the lens, the touchpad and the mic. It's what the old app built and what the user first pictured.

How it works:
- **`Ekko.Glasses`** serves the page and two routes:
  - `/hear` takes audio and returns text, through speaches. It accepts WAV from the glasses, or a browser's own WebM or MP4 in preview, up to 2 MB.
  - `/ask` takes text and returns Ekko's reply, through the Brain's `/message` as channel `glasses`, always in one conversation called `glasses`.
  - There are two calls so the lens shows what was heard while Ekko works. A server-ops run can take 2 minutes.
- **Reach:** the service is published on `127.0.0.1:5090` only, so nothing on the LAN can reach it (checked). `tailscale serve --https=8444` puts it on the tailnet with HTTPS, which a browser needs before it allows the mic.
- **Keys: the first one held by a browser.**
  - The page holds `Glasses_PageKey`. It's typed once on the phone, never put in a link or QR code, and only works for `/hear` and `/ask`.
  - The service holds the Brain key (`Api__Keys__glasses`), so the phone never sees it.
  - Each key can be revoked on its own. The page's files need no key, because they hold no secrets.
- **The page** carries over the old app's `lens.js` (the SDK wrapper, the 999-byte text limit, the pretend lens) and its push-to-talk: tap to start, tap to send, double-tap to cancel, up to 30 seconds.
- **The SDK** is `@evenrealities/even_hub_sdk` 0.0.16 from npm, MIT licensed, with its bundle unchanged. It's byte-identical to the old app's copy, so the old code was written against it. Neither has run on glasses yet.
- **Preview mode:** in a normal browser the page draws the pretend lens with Tap and Double tap buttons, and records that device's own mic. So over Tailscale the phone can talk to Ekko through this page before the glasses arrive. A box to type instead of talking is there too.

Checks:
- A generated spoken question was transcribed word for word and answered.
- Requests with a missing key, non-audio uploads and anything over 2 MB were all refused.
- The preview was clicked through in a browser, with no console errors.

Waiting for the hardware:
- whether SDK 0.0.16's lens, mic and touchpad calls behave as the code expects;
- how the page is opened in the Even app;
- the lens layout, and how long replies read;
- tapping against holding to talk;
- filtering out other people's voices (0.0.16 reports `speakerRole`);
- Telegram notifications on the lens.

*Revisit when* the glasses arrive.

## Resolved questions

- **Speech-to-text runs on the server.** The Even Hub SDK gives plugins raw microphone audio (16 kHz PCM) with no transcription API. The page records it and sends one WAV per press to `Ekko.Glasses`, which transcribes it with speaches (DR-38).
- **No wake word for plugins** as far as public sources show. It's tap to talk and tap to send for now, as the old app did. Holding to talk and the R1 ring are still to be tried on hardware.
- **Telegram can't use the glasses' mic.** Only apps running inside the Even app get it. The Even app can mirror Telegram notifications onto the lens, which covers alerts while the Ekko page is closed.
- **The glasses have no speaker.** Replies on the glasses are short text on the lens; spoken replies stay on the phone and browser apps.
- **Discord control uses hotkeys,** sent by the PC service.
- **`lock_pc` skips confirmation.** Low risk.
- **Server-ops allowlists:** restart limited to Jellyfin; cache clearing disabled until a folder list exists.

## Build order

1. Stand up the Brain service skeleton: the normalized request, conversation state, and JSONL trace.
2. Add the fast-path table for existing commands (Discord, volume, Steam).
3. Add the router with a JSON schema and an eval file of 30 to 50 utterances.
4. Migrate the existing domains one at a time, desktop first, then media with structured `play_media`.
5. Add the risk-class gates and confirm flow, then budget and calendar writes.
6. Build the server-ops agent loop on the Claude API, with approvals and saved runs.
7. Connect the remaining channels (Telegram, phone app, glasses) to the same API.
