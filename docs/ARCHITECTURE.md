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
| speaches | http://localhost:8000 | OpenAI-compatible speech server (faster-whisper). Future speech-to-text for the glasses channel. Not used in phase 1. |
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
EXECUTORS  PC service (over Tailscale) | Media API (tvremote) | TV (tvshim) | Google Sheets (budget) | Docker proxy (server ops)
```

Within the Brain, a request moves left to right and stops at the first step that can handle it.

## Request pipeline

Every request passes through the same six steps, and a model is involved in at most two of them.

1. **Normalize.** The channel hands the Brain text, a channel ID, and a conversation ID. Glasses audio is transcribed on the server first; the other channels send text.
2. **Check pending state.** If Echo asked a question last turn ("the show or the movie?") or is waiting on a confirmation, this message answers it. Resume that run instead of routing again.
3. **Fast path.** A C# command table matches exact or near-exact phrases ("mute Discord," "volume up," "pause"). On a match, call the tool directly. No model is used.
4. **Route.** One model call returns a single domain from a fixed list: `desktop | media | tv | budget | food | calendar | server_ops | chat`. If the confidence is low, Echo asks instead of guessing.
5. **Handle.** The domain handler extracts structured arguments with its own 2 to 5 tools, then code validates them, applies the risk gate, and executes. Server ops is the one handler that runs a full agent loop.
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
| Budget | `mark_bill_paid` | item, amount? | Read-only lookup; the write is `mark_row_paid` (code-only, Write, confirm) | Budget service (Sheets) |
| Budget | `add_expense` | item, amount, income?, planned? | Write, confirm | Budget service (Sheets) |
| Food | `log_food` | item, quantity?, meal? | Reversible | Server |
| Food | `get_food_log` | date | Read-only | Server |
| Calendar | `get_events` | start, end | Read-only | Server |
| Calendar | `find_free_time` | date, duration | Read-only | Server |
| Calendar | `create_event` | title, start, end | External, confirm | Server |
| Server ops | `get_disk_usage` | none | Read-only | Server |
| Server ops | `list_containers` | none | Read-only | Server (socket proxy) |
| Server ops | `read_container_logs` | container, lines | Read-only | Server (socket proxy) |
| Server ops | `find_large_dirs` | path, depth | Read-only | Server |
| Server ops | `restart_container` | container: jellyfin only | Reversible, approve | Server (socket proxy) |
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

- Every model call logs its input, output, latency, and whether validation passed.
- When Qwen's output fails schema validation, retry once with the error message, then escalate to Claude.
- Review the failure log weekly. A step that escalates often becomes Claude by default. A step that never fails might become fast-path.

## Safety

The model decides *what* to call. The tool's risk class decides what has to happen *before* the call runs, and that is enforced in C#, never in a prompt.

| Risk class | Gate before execution | Examples |
| --- | --- | --- |
| Read-only | None | `get_events`, `get_disk_usage` |
| Reversible | Argument validation and allowlist | `open_app`, `play_media`, `log_food`, `lock_pc` |
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

- **Conversation state:** recent turns plus any pending question or confirmation, keyed by conversation ID, with a short expiry (a few minutes). This is what makes "the movie" work as a follow-up.
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

**DR-16: Answers to pending questions are resolved in code, without a model call.** Options: an extra model call, or deterministic matching. "Yes" and "no" are fixed phrase sets. A "which one?" choice matches words that point at exactly one option ("the movie", "2002", a title word) before positions ("the second one"). Anything that doesn't clearly match drops the pending question and is handled as a new request. That fails safe, because an unclear answer never runs a write. *Revisit if* the trace shows many answers that should have matched but didn't.

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

## Resolved questions

- **Speech-to-text runs on the server.** The Even Hub SDK gives plugins raw microphone audio (16 kHz PCM) with no transcription API. The plugin streams audio to Echo, which transcribes it with speaches.
- **No wake word for plugins** as far as public sources show. Plan: hold-to-speak on the glasses or R1 ring; keep the phone app's always-on mic as the hands-free fallback. Verify on hardware.
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
