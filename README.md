# DB Agent

A self-healing, event-driven pipeline that turns natural language commands into SQL, executes them against PostgreSQL, and automatically classifies, retries, and repairs failed queries — with a Kafka backbone tying it all together.

## Status

🚧 **In progress.** Infrastructure, happy-path execution, error classification, and the SQL-generation agent are all complete — natural language commands flow end-to-end from the API through the agent, Kafka, and into PostgreSQL. Retry handling, the fix agent, scope validation, and the dashboard are not yet built.

| Phase | Description | Status |
|---|---|---|
| 1 | Infrastructure (Docker, Kafka topics, Postgres tables) | ✅ Done |
| 2 | Happy path (API → Kafka → Executor → Postgres) | ✅ Done |
| 3 | Error classification & routing | ✅ Done |
| 4 | Python SQL-generation agent | ✅ Done |
| 5 | Retry consumer (exponential backoff) | ⬜ Not started |
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
      ▼
 ┌─────────────┬──────────────────┬────────────────┐
 │ retry-queue │ fixable-schema-  │ failed-queries  │
 │             │ errors           │                 │
 └─────────────┴──────────────────┴─────────────────┘
      │                  │
      ▼                  ▼
 RetryConsumer      FixAgentConsumer
 (planned)          (planned)
```

Every execution and retry attempt is logged to PostgreSQL for auditability (`query_executions`, `query_attempts`).

## Tech Stack

- **API / Backend:** .NET 9 (Api, DbExecutor, Common projects)
- **Messaging:** Apache Kafka + Zookeeper, inspected via Kafdrop
- **Database:** PostgreSQL
- **SQL Generation:** Python (FastAPI, Anthropic SDK / LangChain)
- **Dashboard (planned):** Angular
- **Orchestration:** Docker Compose

## Design notes

- **No reflection loop in the SQL agent.** Early on, the plan called for a self-critique/reflection step before returning generated SQL. This was deliberately dropped in favor of relying on the system-level safety net already built into the pipeline — error classification, retries, and (eventually) a dedicated fix agent — rather than duplicating that work inside every single generation call. If bad SQL turns out to reach the fix agent frequently in practice, this decision will be revisited with real data.

## Kafka Topics

| Topic | Purpose |
|---|---|
| `pending-queries` | New SQL commands awaiting execution |
| `retry-queue` | Failed queries eligible for retry (backoff or connection-type errors) |
| `fixable-schema-errors` | Failures the fix agent may be able to repair (e.g. bad column/table references) |
| `failed-queries` | Permanently failed queries, after retries/fix attempts are exhausted |

## Database Schema

- `query_executions` — one row per submitted command: SQL, status, timestamps, result
- `query_attempts` — one row per retry/fix attempt: linked to an execution, records attempt number and outcome

## Project Structure

```
db-agent/
├── docker-compose.yml
├── src/
│   ├── Api/              # .NET 9 — receives commands, calls agent, publishes to Kafka
│   ├── DbExecutor/       # .NET 9 Worker Service — consumes & executes SQL, classifies errors
│   ├── RetryConsumer/    # .NET 9 Worker Service — planned
│   ├── FixAgentConsumer/ # .NET 9 Worker Service — planned
│   └── Common/           # Shared Kafka message models
├── agents/
│   ├── sql_generator/    # Python FastAPI — NL → SQL
│   └── fix_agent/        # Python FastAPI — planned
└── dashboard/            # Angular — planned
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
- `failed-queries`

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

### 6. Try it

Send a natural language command to the API:

```bash
curl -X POST http://localhost:5000/command \
  -H "Content-Type: application/json" \
  -d '{"command": "show me all customers from New York"}'
```

Watch the message flow through Kafdrop, and check `query_executions` in Postgres for the result.

## Roadmap

- [ ] Exponential backoff retry consumer (2s / 4s / 8s, max 3 attempts)
- [ ] Fix agent for schema-fixable errors
- [ ] Scope validation against live `information_schema`
- [ ] Angular dashboard (executions, failed queries, error stats)
- [ ] Demo video and architecture write-up

## License

Personal project — license TBD.
