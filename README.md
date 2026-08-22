# DB Agent

A self-healing, event-driven pipeline that turns natural language commands into SQL, executes them against PostgreSQL, and automatically classifies, retries, and repairs failed queries — with a Kafka backbone tying it all together.

## Status

🚧 **In progress.** Infrastructure, happy-path execution, error classification, SQL generation, retry handling, and automated schema-error fixing are all complete. Natural language commands flow end-to-end: API → agent → Kafka → executor → PostgreSQL, with automatic retry-with-backoff on transient failures and LLM-driven SQL repair on fixable schema errors. Scope validation and the dashboard are not yet built.

| Phase | Description | Status |
|---|---|---|
| 1 | Infrastructure (Docker, Kafka topics, Postgres tables) | ✅ Done |
| 2 | Happy path (API → Kafka → Executor → Postgres) | ✅ Done |
| 3 | Error classification & routing | ✅ Done |
| 4 | Python SQL-generation agent | ✅ Done |
| 5 | Retry handling (exponential backoff) | ✅ Done |
| 6 | Fix agent (automated schema-error repair) | ✅ Done |
| 7 | Scope validation | ⬜ Not started |
| 8 | Angular dashboard | ⬜ Not started |
| 9 | Polish / demo / resume | ⬜ Not started |

## Architecture

```
User command (NL)
      │
      ▼
 .NET API  ──────────►  Python SQL Agent (FastAPI) — /generate
      │                  - prunes schema to relevant tables
      │                  - returns generated SQL + RelevantSchema
      ▼
 Kafka: pending-queries
      │
      ▼
 DbExecutor (.NET Worker Service)
      │  executes SQL against PostgreSQL
      │  classifies errors on failure
      │
      ├──────────────────────┬───────────────────────────┐
      ▼                       ▼                            
 retry-queue            fixable-schema-errors              
      │                       │                            
      ▼                       ▼                            
 RetryChannelProcessor   FixAgentConsumer
 (in-process,             (in-process, fire-and-forget)
  fire-and-forget,             │
  exponential backoff)         ▼
      │                  Python Fix Agent (FastAPI) — /fix
      │                       - takes command, failed SQL,
      │                       - error, and RelevantSchema
      │                       - returns fixed SQL or CANNOT_FIX
      │                       │
      │                  re-executes fixed SQL once, in-process
      │                       │
      ├─ success  → query_executions.status = "success"       ◄──┤
      ├─ exhausted retries → status = "failed"                   │
      ├─ CANNOT_FIX → status = "failed"                          │
      ├─ fix fails, retryable error → routed to retry-queue ──────┘
      └─ fix fails again, fixable-schema error → status = "failed"
                                                    (no second fix attempt)
```

Every execution and retry/fix attempt is logged to PostgreSQL for auditability (`query_executions`, `query_attempts`). Terminal failure state is written directly to `query_executions` — there is no separate dead-letter Kafka topic (see Design Notes).

## Tech Stack

- **API / Backend:** .NET 9 (Api, DbExecutor, Common projects)
- **Messaging:** Apache Kafka + Zookeeper, inspected via Kafdrop
- **Database:** PostgreSQL (via Dapper + Npgsql)
- **SQL Generation & Repair:** Python (FastAPI, OpenAI SDK)
- **Dashboard (planned):** Angular
- **Orchestration:** Docker Compose

## Design Notes

Deliberate deviations from the original plan, kept here so the reasoning isn't lost:

- **No reflection loop in the SQL agent.** Dropped in favor of the system-level safety net (classification, retries, and now the fix agent) rather than duplicating validation inside every generation call. Testing surfaced at least one case where the agent misidentified a data value as a table/column name and rejected a valid command outright — a live candidate to revisit if this pattern recurs.

- **Both retry and fix logic live inside `DbExecutor`**, not separate projects. `DbExecutor` now handles initial execution, retries, and fix-agent orchestration in one process, sharing the same `IDatabaseService`. Simpler than coordinating shared execution logic across multiple .NET projects via `Common`.

- **Retries and fix processing both use fire-and-forget dispatch, not a fixed worker pool.** A single Kafka consumer per topic reads messages and hands each one to an independent task (`_ = ProcessRetryAsync(...)` / `_ = ProcessFixAsync(...)`) instead of awaiting it inline — so a slow backoff delay or LLM call never blocks the consumer from picking up the next message. Concurrency scales naturally with how many items are actually in flight, with no upfront worker-count tuning needed.

- **`DatabaseService` is a singleton**, safely — every method opens its own `NpgsqlConnection` rather than holding one on the instance, so concurrent fire-and-forget tasks each get their own connection via Npgsql's pool. Extracted behind `IDatabaseService` for testability. A connection-disposal bug (missing `await using` in `ExecuteSqlAsync`) was found and fixed during Phase 5 testing.

- **No `failed-queries` Kafka topic.** Cut after confirming nothing needs to consume it — `query_executions.status` is set to `"failed"` directly wherever retries/fixes are exhausted, and `query_attempts` already holds full per-attempt history. A topic with zero consumers was overhead with no functional benefit.

- **Fix attempts are capped at one per execution.** If the fix agent's rewritten SQL fails again with another fixable-schema error, the query goes straight to `failed` rather than calling the fix agent a second time — there's no reason to believe a second call has new information to work with. This is enforced structurally (the fix code path only ever calls the agent once per message) rather than via a persisted counter/flag, since `FixAgentConsumer`'s fix-and-reexecute flow is a single synchronous call chain per message, not a re-queued loop like retries are.

- **`query_attempts.attempt_type` (`"retry"` vs `"fix"`) distinguishes how a query ultimately succeeded or failed** — a plain retry (same SQL, different timing) vs. an agent-driven rewrite. `query_attempts.generated_sql` additionally records exactly what SQL ran on each attempt, so the audit trail shows not just *that* a fix happened but *what changed*.

- **LLM prompts are structured for prompt caching.** Static content (instructions, rules) is placed first in the system message, followed by semi-static content (schema/table context), with only genuinely per-request content (command, failed SQL, error message) in the user message. This maximizes the shared, cacheable prefix across repeated calls to the same endpoint.

## Kafka Topics

| Topic | Purpose |
|---|---|
| `pending-queries` | New SQL commands awaiting execution |
| `retry-queue` | Failed queries eligible for retry (transient/backoff-classified errors) |
| `fixable-schema-errors` | Failures the fix agent may be able to repair (e.g. bad column/table references) |

> Note: the original plan included a fourth topic, `failed-queries`. It was cut — see Design Notes above.

## Database Schema

- `query_executions` — one row per submitted command: original command, generated SQL, and current status (`pending`, `retrying`, `fixing`, `success`, `failed`, `manual_review`, etc.). Updated in place as the command moves through the pipeline.
- `query_attempts` — one row per execution *attempt* (initial try, every retry, and every fix attempt):
  - `attempt_number` — sequence within the execution
  - `attempt_type` — `"retry"` or `"fix"`
  - `generated_sql` — the exact SQL that ran on this attempt
  - `error_type` / `error_message` — what went wrong, if anything
  - `attempted_at`, `resolved`

  Append-only audit trail, linked to `query_executions` via `execution_id`. Powers the planned Phase 8 "attempt history" view.

## Project Structure

```
db-agent/
├── docker-compose.yml
├── src/
│   ├── Api/                  # .NET 9 — receives commands, calls /generate, publishes to Kafka
│   ├── DbExecutor/           # .NET 9 Worker Service — executes SQL, classifies errors,
│   │                         #   handles retries (RetryChannel, RetryChannelProcessor,
│   │                         #   RetryQueryConsumer) AND fix-agent orchestration
│   │                         #   (FixAgentConsumer, IFixAgentClient) — see Design Notes
│   └── Common/                # Shared Kafka message models (QueryMessage, RetryMessage, FailedQueryMessage)
├── agents/
│   ├── sql_generator/         # Python FastAPI — /generate: NL → SQL, with schema pruning
│   └── fix_agent/             # Python FastAPI — /fix: broken SQL + error → corrected SQL or CANNOT_FIX
└── dashboard/                  # Angular — planned
```

## Setup

### Prerequisites

- Docker & Docker Compose
- .NET 9 SDK
- Python 3.11+
- An OpenAI API key (or Anthropic, depending on agent configuration)

### 1. Start infrastructure

```bash
docker-compose up -d
```

This starts Zookeeper, Kafka, PostgreSQL, and Kafdrop.

- Kafdrop UI: [http://localhost:9000](http://localhost:9000)
- Verify all four containers are running: `docker ps`

### 2. Create Kafka topics

In Kafdrop, manually create:

- `pending-queries`
- `retry-queue`
- `fixable-schema-errors`

### 3. Set up the database

Connect to PostgreSQL (e.g. via DBeaver/pgAdmin) and create the `query_executions` and `query_attempts` tables (see Database Schema above for required columns).

### 4. Set up the Python agents

```bash
cd agents/sql_generator
python -m venv venv
source venv/bin/activate        # Windows: venv\Scripts\activate
pip install -r requirements.txt
```

Create a `.env` file (never commit this):

```
DB_AGENT_KEY=your_key_here
```

Run the agent:

```bash
uvicorn main:app --reload --port 8001
```

Repeat the same setup for `agents/fix_agent`, running on a separate port (e.g. `8002`).

### 5. Run the .NET services

```bash
cd src/Api
dotnet run

cd src/DbExecutor
dotnet run
```

`DbExecutor` handles execution, retries, and fix-agent orchestration — no separate service to run for either.

### 6. Try it

Send a natural language command to the API:

```bash
curl -X POST http://localhost:5000/command \
  -H "Content-Type: application/json" \
  -d '{"command": "show me all customers from New York"}'
```

Watch the message flow through Kafdrop, and check `query_executions` in Postgres for the result.

To see retry behavior, try a command that will transiently fail (e.g. stop the Postgres container briefly, or point at a locked row). To see fix-agent behavior, manually publish a message to `pending-queries` with SQL referencing a nonexistent column or table (the SQL-generation agent tends to correctly refuse to generate such SQL itself, so this path is easiest to trigger via a direct Kafka message — see `docker exec ... kafka-console-producer` or a similar producer tool). Either way, watch `query_attempts` fill in with one row per attempt before `query_executions.status` settles to `success` or `failed`.

## Roadmap

- [ ] Scope validation against live `information_schema`
- [ ] Angular dashboard (executions, failed queries, error stats)
- [ ] Demo video and architecture write-up

## License

Personal project — license TBD.