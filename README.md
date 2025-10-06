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
- SignalR hub at `/hub/match` – publishes match state and trainer metrics to connected clients.
- `GET /health` – application liveness probe.

## Configuration

Configuration mirrors the values described in the spec, including strong consistency and shared data store types across environments.

Observability is provided via OpenTelemetry tracing/metrics/logs (exported over OTLP) and Application Insights telemetry if configured.

