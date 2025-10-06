namespace FanRide

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq

module private ChangeFeedHelpers =
  let tryValue<'T> (token: JToken) (name: string) =
    let value = token.[name]
    if isNull value then
      ValueNone
    else
      try
        ValueSome(value.Value<'T>())
      with _ -> ValueNone

  let tryGetToken (doc: JObject) (name: string) =
    let mutable token = Unchecked.defaultof<JToken>
    if doc.TryGetValue(name, &token) then Some token else None

  let getStreamId (doc: JObject) =
    match tryValue<string> doc "streamId" with
    | ValueSome sid -> Some sid
    | ValueNone -> None

  let getSeq (doc: JObject) =
    match tryValue<int> doc "seq" with
    | ValueSome seq -> Some seq
    | ValueNone -> None

  let getTimestamp (doc: JObject) =
    match tryValue<DateTime> doc "ts" with
    | ValueSome ts -> ts
    | ValueNone -> DateTime.UtcNow

open ChangeFeedHelpers

type ChangeFeedProjector(client: CosmosClient, cfg: CosmosConfig, logger: ILogger<ChangeFeedProjector>) =
  inherit BackgroundService()

  let database = client.GetDatabase(cfg.Database)
  let esContainer = database.GetContainer(cfg.Containers.Es)
  let leasesContainer = database.GetContainer(cfg.Containers.Leases)
  let matchStateContainer = database.GetContainer(cfg.Containers.RmMatchState)
  let tesHistoryContainer = database.GetContainer(cfg.Containers.RmTesHistory)
  let leaderboardContainer = database.GetContainer(cfg.Containers.RmLeaderboard)

  let projectorName = "fanride-read-models"

  let handleSnapshot (doc: JObject) =
    task {
      match getStreamId doc with
      | Some streamId ->
          let body = JObject()
          body["id"] <- JValue.CreateString(streamId)
          body["streamId"] <- JValue.CreateString(streamId)
          match tryGetToken doc "state" with
          | Some token -> body["state"] <- token.DeepClone()
          | None -> ()
          let aggVersion = doc.Value<Nullable<int>>("aggVersion")
          if aggVersion.HasValue then
            body["aggVersion"] <- JValue(aggVersion.Value)
          body["updatedAt"] <- JValue(DateTime.UtcNow)
          let! _ = matchStateContainer.UpsertItemAsync(body, PartitionKey(streamId))
          logger.LogDebug("Snapshot projected for {StreamId}", streamId)
          return ()
      | None -> return ()
    }

  let handleTrainerMetrics (doc: JObject) =
    task {
      match getStreamId doc, getSeq doc with
      | Some streamId, Some seq ->
          let ts = getTimestamp doc
          let metricsToken =
            match tryGetToken doc "data" with
            | Some token -> token.DeepClone()
            | None -> JValue.CreateNull() :> JToken
          let history = JObject()
          history["id"] <- JValue.CreateString($"{streamId}-{seq}")
          history["streamId"] <- JValue.CreateString(streamId)
          history["metrics"] <- metricsToken
          history["ts"] <- JValue(ts)
          let leaderboard = JObject()
          leaderboard["id"] <- JValue.CreateString(streamId)
          leaderboard["streamId"] <- JValue.CreateString(streamId)
          leaderboard["metrics"] <- metricsToken.DeepClone()
          leaderboard["updatedAt"] <- JValue(DateTime.UtcNow)
          let! _ = tesHistoryContainer.UpsertItemAsync(history, PartitionKey(streamId))
          let! _ = leaderboardContainer.UpsertItemAsync(leaderboard, PartitionKey(streamId))
          logger.LogDebug("Trainer metrics projected for {StreamId}", streamId)
          return ()
      | _ -> return ()
    }

  let handleChanges (_: ChangeFeedProcessorContext) (changes: IReadOnlyCollection<JObject>) =
    task {
      for doc in changes do
        match doc.Value<string>("type") with
        | "snapshot" ->
            do! handleSnapshot doc
        | "event" ->
            match doc.Value<string>("kind") with
            | kind when String.Equals(kind, "TrainerMetricsCaptured", StringComparison.OrdinalIgnoreCase) ->
                do! handleTrainerMetrics doc
            | _ -> ()
        | _ -> ()
    }

  let changeFeedHandler =
    Container.ChangeFeedHandler<JObject>(fun context changes _ ->
      handleChanges context changes :> Task)

  let processorLazy =
    lazy (
      esContainer
        .GetChangeFeedProcessorBuilder<JObject>(
          projectorName,
          changeFeedHandler
        )
        .WithInstanceName(Environment.MachineName)
        .WithLeaseContainer(leasesContainer)
        .Build()
    )

  override _.ExecuteAsync(ct: CancellationToken) =
    task {
      let processor = processorLazy.Value
      do! processor.StartAsync()
      logger.LogInformation("Change feed processor {Name} started", projectorName)
      try
        do! Task.Delay(Timeout.Infinite, ct)
      with :? TaskCanceledException ->
        ()
    }

  override _.StopAsync(ct: CancellationToken) =
    task {
      if processorLazy.IsValueCreated then
        do! processorLazy.Value.StopAsync()
        logger.LogInformation("Change feed processor {Name} stopped", projectorName)
    } :> Task

