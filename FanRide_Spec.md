# FanRide — Full Specification

## 1) Stack
- **Language:** F# (.NET 8), 2-space indentation
- **Frontend:** Fable + Feliz + Elmish + Tailwind
- **Backend:** ASP.NET Core F#, SignalR
- **Datastore:** Azure Cosmos DB (Core SQL API) via Azure.Cosmos .NET SDK
- **Trainer Agent:** F# console (BLE FTMS / ANT+ FE-C)
- **Infra:** Azure App Service (Linux), Azure Cosmos DB, Azure SignalR
- **Observability:** App Insights + OpenTelemetry

**Data source parity rule:** Cosmos DB for dev/test/prod.  
Local uses Cosmos Emulator.

---

## 2) Consistency & Transactions

### 2.1 Consistency Level
- Strong (single write region for production)
- Strong for emulator/test for deterministic behavior

### 2.2 Atomic Batch (Events + Snapshot + Outbox)
Use TransactionalBatch per partition (partition key = streamId).
All steps occur in one atomic operation.

---

## 3) Containers & Partitioning

### 3.1 `es` (Event-Sourcing Container)
- Partition Key: `/streamId`
- Item Types: `event`, `snapshot`, `outbox`

### 3.2 Read Models
Separate containers:
- `rm_match_state`
- `rm_tes_history`
- `rm_leaderboard`
- `leases` (Change Feed leases)

---

## 4) Event-Sourcing Model (F# Example)

```fsharp
let handleApplyAflEvent (cs: CosmosClient) (dbName, contName) (streamId, expectedEtag, expectedVersion, newEvents, newSnapshot) = task {
  let container = cs.GetContainer(dbName, contName)
  let pk = new PartitionKey(streamId)
  let batch = container.CreateTransactionalBatch(pk)

  batch.ReplaceItem(
    id = $"snap-{streamId}",
    item = {| type = "snapshot-guard" |},
    requestOptions = TransactionalBatchItemRequestOptions(IfMatchEtag = expectedEtag)
  )
  |> ignore

  for i, ev in newEvents |> List.indexed do
    batch.CreateItem(
      {| id = ev.id; type = "event"; streamId = streamId; seq = expectedVersion + i + 1; kind = ev.kind; data = ev.data; ts = System.DateTime.UtcNow |}
    )
    |> ignore

  batch.UpsertItem(
    {| id = $"snap-{streamId}"; type = "snapshot"; streamId = streamId; aggVersion = expectedVersion + List.length newEvents; state = newSnapshot |}
  )
  |> ignore

  for msg in toOutbox newEvents do
    batch.CreateItem(
      {| id = msg.id; type = "outbox"; streamId = streamId; kind = msg.kind; payload = msg.payload; ts = System.DateTime.UtcNow |}
    )
    |> ignore

  let! resp = batch.ExecuteAsync()
  if not resp.IsSuccessStatusCode then
    failwithf "Batch failed: %A" resp.StatusCode
}
```

---

## 5) Read Models (Change Feed)

- Driven by Change Feed Processor
- Mode: StartFromNow (live) / StartFromBeginning (rebuild)
- Filters by `type`

**Containers updated:**
- `rm_match_state` (live match info)
- `rm_tes_history` (momentum sparkline)
- `rm_leaderboard` (rider performance)

---

## 6) Data Shapes

### Event
```json
{ "id": "ULID", "type": "event", "streamId": "match-1", "seq": 204, "kind": "AflEventApplied", "data": {}, "ts": "2025-10-06T01:23:45Z" }
```

### Snapshot
```json
{ "id": "snap-match-1", "type": "snapshot", "streamId": "match-1", "aggVersion": 204, "state": {}, "_etag": "etag" }
```

### Outbox
```json
{ "id": "out-1", "type": "outbox", "streamId": "match-1", "kind": "trainerEffect", "payload": {}, "ts": "2025-10-06T01:23:45Z" }
```

---

## 7) Configuration

```yaml
cosmos:
  accountEndpoint:
    dev:  "https://localhost:8081/"
    test: "https://cosmos-test.documents.azure.com:443/"
    prod: "https://cosmos-prod.documents.azure.com:443/"
  key:
    dev:  "EmulatorKey"
    test: "env:COSMOS_KEY_TEST"
    prod: "env:COSMOS_KEY_PROD"
  database: "fanride"
  containers:
    es: "es"
    rmMatchState: "rm_match_state"
    rmTesHistory: "rm_tes_history"
    rmLeaderboard: "rm_leaderboard"
    leases: "leases"
  consistencyLevel: "Strong"
  useSameType: true
```

---

## 8) Frontend (Elmish Example)

```fsharp
let update msg model =
  match msg with
  | Connected -> { model with connection = Connected }, Cmd.none
  | MatchStateArrived s ->
    { model with scoreHome = s.score.home; scoreAway = s.score.away; quarter = s.quarter; clock = s.clock },
    Cmd.none
  | MetricsTick (w,c,h) ->
    { model with myWatts = w; myCadence = c; myHr = h }, Cmd.signalR.sendMetrics w c h
```

---

## 9) Acceptance Criteria

- Live AFL → Cosmos events → Change Feed → UI within 2.5s (p95)
- Strong consistency on write
- Same datastore type for all envs (Cosmos DB SQL API)
- Atomic append/snapshot/outbox per stream
- Read models rebuildable from Change Feed

---

## 10) Deployment

| Component | Hosting |
|------------|----------|
| Backend | Azure App Service (Linux) |
| SignalR | Azure SignalR Serverless |
| Cosmos | Azure Cosmos DB (SQL API) |
| Frontend | Azure Static Web Apps or CDN |

---

**✅ Ready for Codex implementation and CI/CD scaffold generation.**
