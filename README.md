# Echo Architecture Spec

Nov 2, 2026 · @Jarrett Plate

## Purpose and goals

Echo is a router with domain handlers, not one agent holding every tool. This spec lays out how requests flow, where models are used, and where code takes over, so Echo can grow toward controlling everything without getting less reliable.

- **Reliable everyday commands:** volume, Discord, apps, and media work every time, fast enough for voice.
- **One brain, many channels:** glasses, phone app, Telegram, and browser all reach the same pipeline.
- **Safe writes:** nothing that spends, deletes, or messages runs without the right gate.
- **Practice ground:** every major choice gets a decision record, as practice for the agentic systems architect role.

## Review of the current sketch

The sketch already has the right core: a domain selector fanning out to five domains with narrow tools. The gaps are around the edges: channels besides the glasses, a fast path, confirmation, conversation state, and two missing domains.

**What works**

- The glasses chain (Even Realities app, plugin, WebView, API, speech-to-text) is a clean channel layer.
- A domain selector in front of small tool groups is the right shape for a 9B model.
- Tools are narrow and named after intent, such as `get_budget_remaining` instead of raw sheet access.

**What to change**

- **Agent, Prompt, and Domain Selector are drawn as three boxes.** Merge them into one "Brain" service containing the fast path, router, and handlers, so it's clear what each part decides.
- **Only one channel is drawn.** The phone app, Telegram, and browser app should enter the same API, and replies should go back to whichever channel asked.
- **Replies only flow back synchronously.** Approvals and server-ops alerts need a push path that reaches you without a request.
- **No fast path.** Exact commands like "mute Discord" should skip the model entirely.
- **No confirm or approval step.** `log_expense`, `create_event`, and `lock_pc` need a gate before they run.
- **No conversation state.** Follow-ups like "the movie, not the show" need the previous turn and any pending choice.
- **Missing domains:** Server ops (the practice agent) and TV control (`tv_launch_jellyfin`).
- **Missing PC tools:** `discord_set_mute`, `discord_set_deafen`, `web_search`.
- **Media tools say "movie."** Scooby-Doo might be a show. Use `play_media` with an optional kind, season, and episode.
- **`turn_on_pc` can't run on the PC.** If the PC is off, its service isn't running. That tool belongs on the server as Wake-on-LAN.
- **Speech-to-text placement.** If the Even Realities app already transcribes, the server-side step is required, since the SDK only provides raw audio (resolved below).

## Layered architecture

Echo splits into three layers: channels that only convert speech and text, one Brain that makes every decision, and executors that only carry out validated calls.

&#91;embedded content: Echo architecture · 3 layers\]

Within the Brain, a request moves left to right and stops at the first step that can handle it. The highlighted server-ops agent is the only place a full agent loop runs.

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
| Desktop | `discord_set_mute` | on: bool | Reversible | PC service |
| Desktop | `discord_set_deafen` | on: bool | Reversible | PC service |
| Desktop | `adjust_volume` | direction: up, down; steps | Reversible | PC service |
| Desktop | `set_volume` | level: 0 to 100 | Reversible | PC service |
| Desktop | `media_play_pause` | none | Reversible | PC service |
| Desktop | `web_search` | query | Reversible | PC service |
| Desktop | `lock_pc` | none | Reversible | PC service |
| Desktop | `wake_pc` | none | Reversible | Server (Wake-on-LAN) |
| Media | `play_media` | title; kind?, season?, episode? | Reversible | Media API |
| Media | `media_control` | action: pause, resume, stop, next | Reversible | Media API |
| TV | `tv_launch_jellyfin` | none | Reversible | Server (batch push) |
| Budget | `get_budget_remaining` | category? | Read-only | Server (Sheets) |
| Budget | `list_categories` | none | Read-only | Server (Sheets) |
| Budget | `log_expense` | amount, category, note | Write, confirm | Server (Sheets) |
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
| Server ops | `clear_cache_dir` | target (allowlist) | Irreversible, approve | Server |

**Design rules for every tool:**

- Use an enum wherever the set of values is known. A 9B model rarely gets a fixed list wrong.
- Return finished answers ("Groceries: $142 of $600 left"), not raw data. Code does the math.
- Tools that can be ambiguous return candidates instead of guessing (`play_media`).
- `log_food` takes `currenttime` from the server clock, not from the model.

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

**DR-3: Structured media requests instead of a rephrased string.** Rephrasing into "play Scooby-Doo" and parsing it again doubles the chances of misinterpretation. A `title` field plus a deterministic search that returns candidates puts ambiguity in code and the choice with you. *Revisit if* fuzzy search proves too weak.

**DR-4: Agent loop only for server ops.** Diagnosis paths depend on what each step finds, and nothing else in Echo works that way yet. *Revisit if* another domain needs multi-step investigation.

**DR-5: Tiered models, escalation driven by data.** Qwen handles routing and extraction locally. Claude handles diagnosis and Qwen's failures. *Revisit if* the failure log shows a step escalating often.

**DR-6: Tools in-process for now.** One container with tools as C# classes is fastest to change while the catalog settles. *Revisit if* a second agent or front end needs the same tools, then extract an MCP server.

## Open questions and build order

**Open questions**

- [x] **Speech-to-text runs on the server.** The Even Hub SDK hands a plugin raw microphone audio (16 kHz PCM) and has no transcription API, so the plugin streams audio to Echo and Echo transcribes it, for example with local Whisper. ([SDK audio](https://www.skills.sh/even-realities/everything-evenhub/device-features), [simulator notes](https://github.com/BxNxM/even-dev))
- [x] **No wake word for plugins, as far as public sources show.** Nothing documents a way for third-party plugins to hook "Hey Even" or listen continuously. Community apps use tap or hold-to-speak, and a mic indicator always shows while listening. Plan: hold-to-speak from the glasses or R1 ring, and keep the phone app's always-on mic as the hands-free fallback. Verify on hardware. ([community app list](https://github.com/samuelvoltarius/quietglass-g2))
- [x] **The glasses have no speaker.** Replies to the glasses are short text on the lens. Spoken replies stay with the phone and browser apps. ([G2 overview](https://github.com/pangoleen/awesome-even-realities-g2))
- [x] **Discord control uses hotkeys,** sent by the PC service.
- [x] **`lock_pc` skips confirmation.** It's low risk.
- [x] **Server-ops allowlists:** restart is limited to Jellyfin. Folders Echo may clear are still undecided, so clear\_cache\_dir stays disabled until there's a list.

