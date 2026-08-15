# DB Agent

A self-healing, event-driven pipeline that turns natural language commands into SQL, executes them against PostgreSQL, and automatically classifies and retries failed queries — with a Kafka backbone tying it all together.

## Status

🚧 **In progress.** Infrastructure, happy-path execution, error classification, the SQL-generation agent, and retry handling are all complete. Natural language commands flow end-to-end: API → agent → Kafka → executor → PostgreSQL, with automatic retry-with-backoff on transient failures. The fix agent, scope validation, and the dashboard are not yet built.

| Phase | Description | Status |
|---|---|---|
| 1 | Infrastructure (Docker, Kafka topics, Postgres tables) | ✅ Done |
| 2 | Happy path (API → Kafka → Executor → Postgres) | ✅ Done |
| 3 | Error classification & routing | ✅ Done |
| 4 | Python SQL-generation agent | ✅ Done |
| 5 | Retry handling (exponential backoff) | ✅ Done |
| 6 | Fix agent | ⬜ Not started |
| 7 | Scope validation | ⬜ Not started |
| 8 | Angular dashboard | ⬜ Not started |
| 9 | Polish / demo / resume | ⬜ Not started |

## Architecture

```
User command (NL)
      │
      ▼
 .NET API  ──────────►  Python SQL Agent (FastAPI)
      │                  - takes schema + NL command
      │                  - returns generated SQL
      ▼
 Kafka: pending-queries
      │
      ▼
 DbExecutor (.NET Worker Service)
      │  executes SQL against PostgreSQL
      │  classifies errors on failure
      │
      ├──────────────┬─────────────────────────┐
      ▼               ▼                          
 retry-queue    fixable-schema-errors            
      │               │                          
      ▼               ▼                          
 RetryChannelProcessor  FixAgentConsumer
 (in-process, fire-       (planned)
  and-forget retries
  with backoff)
      │
      ├─ success  → query_executions.status = "success"
      └─ exhausted → query_executions.status = "failed"
```

Every execution and retry attempt is logged to PostgreSQL for auditability (`query_executions`, `query_attempts`). Terminal failure state is written directly to `query_executions` — there is no separate dead-letter Kafka topic (see Design Notes).

## Tech Stack

- **API / Backend:** .NET 9 (Api, DbExecutor, Common projects)
- **Messaging:** Apache Kafka + Zookeeper, inspected via Kafdrop
- **Database:** PostgreSQL (via Dapper + Npgsql)
- **SQL Generation:** Python (FastAPI, Anthropic SDK / LangChain)
- **Dashboard (planned):** Angular
- **Orchestration:** Docker Compose

## Design Notes

A few deliberate deviations from the original plan, kept here so the reasoning isn't lost:

- **No reflection loop in the SQL agent.** The original plan called for a self-critique/reflection step before returning generated SQL. This was dropped in favor of relying on the system-level safety net already built into the pipeline — error classification and retries — rather than duplicating that work inside every generation call. Testing has since surfaced at least one case where reflection likely would have helped (an ambiguous command where the agent misidentified a data value as a table name and rejected the command incorrectly). Not reversed yet, but a live candidate to revisit if this pattern recurs.

- **Retry logic lives inside `DbExecutor`, not a separate `RetryConsumer` project.** This means `DbExecutor` now handles both initial execution and retries in one process, sharing the same `IDatabaseService`. Simpler than coordinating shared execution logic across two separate .NET projects via `Common`.

- **Retries use an in-process `Channel<T>`, not a fixed worker pool.** A single Kafka consumer reads `retry-queue` and writes items to a channel. A single reader loop drains the channel, but instead of awaiting each retry's backoff delay (which would block the loop), each retry is dispatched as an independent fire-and-forget task (`_ = ProcessRetryAsync(...)`). This means Kafka consumption is never blocked by a query mid-backoff, and there's no need to guess a fixed worker count up front — concurrency scales naturally with how many retries are actually in flight. A delay-queue-with-single-timer design (`PriorityQueue` keyed by due-time) was considered as a more scalable alternative but judged unnecessary at this project's scale.

- **`DatabaseService` is registered as a singleton**, safely — every method opens its own `NpgsqlConnection` rather than holding one on the instance, so concurrent fire-and-forget retries each get their own connection via Npgsql's connection pool. It's also extracted behind `IDatabaseService` for testability. A connection-disposal bug (an un-disposed connection in `ExecuteSqlAsync`) was found and fixed during Phase 5 testing.

- **No `failed-queries` Kafka topic.** Originally planned as a fourth topic for permanently failed queries, it was cut after testing confirmed nothing needs to consume it — `query_executions.status` already becomes `"failed"` directly once retries are exhausted, and `query_attempts` already holds full per-attempt history. A Kafka topic with zero consumers was pure overhead with no functional benefit; the same information is fully queryable from Postgres. (Only three Kafka topics are in active use as a result — see below.)

## Kafka Topics

| Topic | Purpose |
|---|---|
| `pending-queries` | New SQL commands awaiting execution |
| `retry-queue` | Failed queries eligible for retry (transient/backoff-classified errors) |
| `fixable-schema-errors` | Failures the fix agent may be able to repair (e.g. bad column/table references) |

> Note: the original plan included a fourth topic, `failed-queries`. It was cut — see Design Notes above.

## Database Schema

- `query_executions` — one row per submitted command: original command, generated SQL, and current status (`pending`, `retrying`, `fixing`, `success`, `failed`, `manual_review`, etc.). Updated in place as the command moves through the pipeline.
- `query_attempts` — one row per execution *attempt* (initial try + every retry): attempt number, error type/message, timestamp, resolved flag. Append-only audit trail, linked to `query_executions` via `execution_id`. Powers the planned Phase 8 "attempt history" view.

## Project Structure

```
db-agent/
├── docker-compose.yml
├── src/
│   ├── Api/                  # .NET 9 — receives commands, calls agent, publishes to Kafka
│   ├── DbExecutor/           # .NET 9 Worker Service — executes SQL, classifies errors,
│   │                         #   AND handles retries (RetryChannel, RetryChannelProcessor,
│   │                         #   RetryQueryConsumer) — see Design Notes
│   ├── FixAgentConsumer/     # .NET 9 Worker Service — planned
│   └── Common/                # Shared Kafka message models
├── agents/
│   ├── sql_generator/         # Python FastAPI — NL → SQL
│   └── fix_agent/             # Python FastAPI — planned
└── dashboard/                  # Angular — planned
```

## Setup

### Prerequisites

- Docker & Docker Compose
- .NET 9 SDK
- Python 3.11+
- An Anthropic API key

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

Connect to PostgreSQL (e.g. via DBeaver/pgAdmin) and create the `query_executions` and `query_attempts` tables.

### 4. Set up the Python agent

```bash
cd agents/sql_generator
python -m venv venv
source venv/bin/activate        # Windows: venv\Scripts\activate
pip install -r requirements.txt
```

Create a `.env` file (never commit this):

```
ANTHROPIC_API_KEY=your_key_here
```

Run the agent:

```bash
uvicorn main:app --reload --port 8001
```

### 5. Run the .NET services

```bash
cd src/Api
dotnet run

cd src/DbExecutor
dotnet run
```

`DbExecutor` handles both initial execution and retries — no separate retry service to run.

### 6. Try it

Send a natural language command to the API:

```bash
curl -X POST http://localhost:5000/command \
  -H "Content-Type: application/json" \
  -d '{"command": "show me all customers from New York"}'
```

Watch the message flow through Kafdrop, and check `query_executions` in Postgres for the result.

To see retry behavior, try a command that will transiently fail (e.g. stop the Postgres container briefly, or point at a locked row) and watch `query_attempts` fill in with one row per attempt, backing off 2s / 4s / 8s, before `query_executions.status` settles to `success` or `failed`.

## Roadmap

- [ ] Fix agent for schema-fixable errors
- [ ] Scope validation against live `information_schema`
- [ ] Angular dashboard (executions, failed queries, error stats)
- [ ] Demo video and architecture write-up

## License

Personal project — license TBD.