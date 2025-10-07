namespace FanRide

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Net
open Microsoft.AspNetCore.SignalR
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

type ChangeFeedProjector
  ( 
    client: CosmosClient,
    cosmosCfg: CosmosConfig,
    changeFeedCfg: ChangeFeedConfig,
    hub: IHubContext<MatchHub>,
    readModels: IReadModelService,
    logger: ILogger<ChangeFeedProjector>
  ) =
  inherit BackgroundService()

  let database = client.GetDatabase(cosmosCfg.Database)
  let esContainer = database.GetContainer(cosmosCfg.Containers.Es)
  let leasesContainer = database.GetContainer(cosmosCfg.Containers.Leases)
  let matchStateContainer = database.GetContainer(cosmosCfg.Containers.RmMatchState)
  let tesHistoryContainer = database.GetContainer(cosmosCfg.Containers.RmTesHistory)
  let leaderboardContainer = database.GetContainer(cosmosCfg.Containers.RmLeaderboard)
  let changeFeedMode = ChangeFeedConfiguration.parseMode changeFeedCfg.Mode

  let projectorName = "fanride-read-models"

  let purgeLeasesIfNeeded () =
    task {
      if changeFeedMode = ChangeFeedMode.Rebuild then
        logger.LogWarning(
          "Clearing existing leases in container {Container} to rebuild read models",
          cosmosCfg.Containers.Leases
        )
        let iterator = leasesContainer.GetItemQueryIterator<JObject>()
        while iterator.HasMoreResults do
          let! response = iterator.ReadNextAsync()
          for lease in response do
            match lease.Value<string>("id") with
            | null -> ()
            | id ->
                try
                  do!
                    leasesContainer.DeleteItemAsync<JObject>(
                      id,
                      PartitionKey(id)
                    )
                    :> Task
                with :? CosmosException as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
                  ()
    }

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
          let! snapshot = readModels.GetMatchState(streamId)
          match snapshot with
          | Some state -> do! hub.Clients.All.SendAsync("matchState", { ScoreHome = state.ScoreHome; ScoreAway = state.ScoreAway; Quarter = state.Quarter; Clock = state.Clock })
          | None -> ()
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
          let! momentum = readModels.GetTesMomentum(streamId)
          match momentum with
          | Some dto -> do! hub.Clients.All.SendAsync("tesHistory", dto)
          | None -> ()
          let! leaderboardDto = readModels.GetLeaderboard()
          do! hub.Clients.All.SendAsync("leaderboard", leaderboardDto)
          return ()
      | _ -> return ()
    }

  let handleOutbox (doc: JObject) =
    task {
      match getStreamId doc, doc.Value<string>("id") |> Option.ofObj with
      | Some streamId, Some id ->
          let kind = doc.Value<string>("kind")
          match kind with
          | null -> return ()
          | k when k.Equals("trainerEffect", StringComparison.OrdinalIgnoreCase) ->
              let payloadToken =
                match tryGetToken doc "payload" with
                | Some token when not (isNull token) -> token :> JToken
                | _ -> JObject()
              let payload =
                match payloadToken with
                | :? JObject as obj ->
                    obj.Properties()
                    |> Seq.map (fun prop -> prop.Name, prop.Value.ToObject<obj>())
                    |> dict
                    :> IDictionary<string, obj>
                | _ -> dict [] :> IDictionary<string, obj>
              let enqueuedAt =
                doc.Value<Nullable<DateTime>>("ts")
                |> Option.ofNullable
                |> Option.defaultValue DateTime.UtcNow
              let dto : TrainerEffectDto =
                { StreamId = streamId
                  Id = id
                  Kind = kind
                  Payload = payload
                  EnqueuedAt = enqueuedAt }
              do! hub.Clients.All.SendAsync("trainerEffect", dto)
              try
                let operations = Collections.Generic.List<PatchOperation>()
                operations.Add(PatchOperation.Add("processedAt", DateTime.UtcNow))
                let! _ = esContainer.PatchItemAsync(id, PartitionKey(streamId), operations)
                ()
              with :? CosmosException as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound -> ()
          | _ -> ()
      | _ -> ()
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
        | "outbox" ->
            do! handleOutbox doc
        | _ -> ()
    }

  let changeFeedHandler =
    Container.ChangeFeedHandler<JObject>(fun context changes _ ->
      handleChanges context changes :> Task)

  let processorLazy =
    lazy (
      let builder =
        esContainer
          .GetChangeFeedProcessorBuilder<JObject>(
            projectorName,
            changeFeedHandler
          )
          .WithInstanceName(Environment.MachineName)
      let builder =
        match changeFeedMode with
        | ChangeFeedMode.Live -> builder
        | ChangeFeedMode.Rebuild ->
            logger.LogWarning("Change feed projector {Name} starting from beginning", projectorName)
            builder.WithStartTime(DateTime.MinValue)
      builder
        .WithLeaseContainer(leasesContainer)
        .Build()
    )

  override _.ExecuteAsync(ct: CancellationToken) =
    task {
      do! purgeLeasesIfNeeded()
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

