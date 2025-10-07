# FanRide

FanRide implements the architecture outlined in `FanRide_Spec.md`. The solution ships a strongly consistent Cosmos DB event store, Change Feed powered read-model projectors, a SignalR hub surface for real-time AFL match telemetry, and a Fable/Feliz Elmish dashboard backed by Tailwind CSS.

## Projects

- `src/Backend` – ASP.NET Core (F#) application hosting the event store API, change feed projectors, SignalR hub, and OpenTelemetry/App Insights instrumentation.
- `src/Frontend` – Fable + Feliz + Elmish application styled with Tailwind that consumes the SignalR hub for live match state and trainer metrics.
- `src/Trainer` – F# console agent that simulates an FTMS/FE-C trainer by streaming metrics into the backend hub.

## Getting Started

```bash
cd src/Backend
dotnet run
```

The application expects a Cosmos DB account. For local development configure the Cosmos DB Emulator endpoint/key in environment variables or rely on the defaults in `appsettings.json`.

### Frontend Dev Server

```bash
cd src/Frontend
dotnet tool restore
npm install
npm run start
```

The Vite dev server proxies API and SignalR calls to `http://localhost:5240`. The `start` script restores the local `fable` tool, runs it in watch mode to emit JavaScript into `.fable-build`, and serves the Elmish app with Tailwind styles via Vite.

### Trainer Agent

```bash
dotnet run --project src/Trainer/Trainer.fsproj -- http://localhost:5240
```

The trainer agent streams simulated cadence/power/heart-rate metrics to the backend SignalR hub and prints score updates received from the backend.

## API Surface

- `POST /api/matches/{streamId}/events` – atomically appends events, snapshot, and outbox messages for a stream.
- `GET /api/matches/{streamId}` – returns the latest snapshot for the stream.
- `POST /api/afl/matches/{streamId}/apply` – Apply AFL feed payloads and receive the updated snapshot envelope.
- `GET /api/afl/matches/{streamId}` – Retrieve the current AFL snapshot envelope including ETag and aggregate version.
- SignalR hub at `/hub/match` – publishes match state and trainer metrics to connected clients.
- `GET /health` – application liveness probe.

When configured, the backend also runs an `AflFeedIngestionService` background worker that polls the external AFL feed and
automatically appends `MatchStateUpdated` events into Cosmos, broadcasting the latest scoreboard to clients without manual API
calls.

## Configuration

Configuration mirrors the values described in the spec, including strong consistency and shared data store types across environments. The `changeFeed.mode` setting toggles between `live` (start from now) and `rebuild` (start from beginning/reset leases) to support read-model rebuilds.

The `aflFeed` section configures live ingestion:

```json
"aflFeed": {
  "enabled": true,
  "streamId": "afl-live",
  "endpoint": "https://feed.example.com/match/live",
  "pollIntervalSeconds": 2,
  "apiKeyHeader": "Ocp-Apim-Subscription-Key",
  "apiKey": "<secret>"
}
```

- `enabled` – turn the ingestion worker on/off.
- `streamId` – event stream that receives scoreboard updates.
- `endpoint` – absolute URL returning the latest `MatchState` payload from the AFL provider.
- `pollIntervalSeconds` – cadence used when polling the feed (defaults to 5 seconds if unspecified).
- `apiKeyHeader` / `apiKey` – optional header/value added to each feed request for authenticated sources.

Observability is provided via OpenTelemetry tracing/metrics/logs (exported over OTLP) and Application Insights telemetry if configured.

