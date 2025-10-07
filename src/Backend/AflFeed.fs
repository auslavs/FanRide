namespace FanRide

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq

[<Interface>]
type IAflFeedClient =
  abstract member FetchMatchState : CancellationToken -> Task<MatchState voption>

type AflFeedClient(
  httpClient: HttpClient,
  cfg: AflFeedConfig,
  logger: ILogger<AflFeedClient>
) =
  let jsonOptions =
    JsonSerializerOptions(
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    )

  do
    if not (String.IsNullOrWhiteSpace(cfg.ApiKeyHeader) || String.IsNullOrWhiteSpace(cfg.ApiKey)) then
      if httpClient.DefaultRequestHeaders.Contains(cfg.ApiKeyHeader) then
        httpClient.DefaultRequestHeaders.Remove(cfg.ApiKeyHeader) |> ignore
      httpClient.DefaultRequestHeaders.Add(cfg.ApiKeyHeader, cfg.ApiKey)

  interface IAflFeedClient with
    member _.FetchMatchState(ct: CancellationToken) =
      task {
        if String.IsNullOrWhiteSpace(cfg.Endpoint) then
          return ValueNone
        else
          try
            use! response = httpClient.GetAsync(cfg.Endpoint, ct)
            if not response.IsSuccessStatusCode then
              logger.LogWarning(
                "AFL feed returned non-success status {StatusCode}",
                response.StatusCode
              )
              return ValueNone
            else
              use! stream = response.Content.ReadAsStreamAsync(ct)
              try
                let! state = JsonSerializer.DeserializeAsync<MatchState>(stream, jsonOptions, cancellationToken = ct)
                return ValueSome state
              with :? JsonException as ex ->
                logger.LogError(ex, "Failed to parse AFL feed payload")
                return ValueNone
          with
          | :? OperationCanceledException when ct.IsCancellationRequested ->
              return ValueNone
          | ex ->
              logger.LogError(ex, "Failed to fetch AFL feed")
              return ValueNone
      }

type AflFeedIngestionService(
  cfg: AflFeedConfig,
  cosmosCfg: CosmosConfig,
  cosmosClient: CosmosClient,
  feedClient: IAflFeedClient,
  hub: IHubContext<MatchHub>,
  logger: ILogger<AflFeedIngestionService>
) =
  inherit BackgroundService()

  let pollInterval =
    if cfg.PollIntervalSeconds > 0 then
      TimeSpan.FromSeconds(float cfg.PollIntervalSeconds)
    else
      TimeSpan.FromSeconds(5.)

  let esContainer = cosmosClient.GetContainer(cosmosCfg.Database, cosmosCfg.Containers.Es)

  let toHubMatchState (state: MatchState) : MatchStateDto =
    { ScoreHome = state.Score.Home
      ScoreAway = state.Score.Away
      Quarter = state.Quarter
      Clock = state.Clock }

  let tryReadSnapshot (streamId: string) =
    task {
      try
        let! response = esContainer.ReadItemAsync<JObject>($"snap-{streamId}", PartitionKey streamId)
        match response.Resource.["state"] with
        | null -> return ValueNone
        | stateToken ->
            let state = stateToken.ToObject<MatchState>()
            let aggVersionNullable = response.Resource.Value<Nullable<int>>("aggVersion")
            let aggVersion = if aggVersionNullable.HasValue then aggVersionNullable.Value else 0
            return ValueSome(state, aggVersion, response.ETag)
      with :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound ->
        return ValueNone
    }

  let appendSnapshot streamId (state: MatchState) ct =
    let rec appendWithRetry attempt =
      task {
        let! snapshot = tryReadSnapshot streamId
        match snapshot with
        | ValueSome(existing, _, _) when existing = state ->
            logger.LogDebug("AFL feed state unchanged for {StreamId}", streamId)
            return ()
        | _ ->
            let expectedVersion, expectedEtag =
              match snapshot with
              | ValueSome(_, version, etag) -> version, Some etag
              | ValueNone -> 0, None
            let event: NewEvent =
              { Id = Guid.NewGuid().ToString("N")
                Kind = "MatchStateUpdated"
                Payload = EventPayload.MatchStateUpdated state }
            let events = [ event ]
            let request =
              { StreamId = streamId
                ExpectedVersion = expectedVersion
                ExpectedEtag = expectedEtag
                Snapshot = EventStore.serializeSnapshot state
                Events = events }
            let! result = EventStore.appendWithSnapshot cosmosClient cosmosCfg.Database cosmosCfg.Containers.Es request
            match result with
            | Ok () ->
                logger.LogInformation(
                  "Ingested AFL feed update for {StreamId} at version {Version}",
                  streamId,
                  expectedVersion + events.Length
                )
                do! hub.Clients.All.SendAsync("matchState", toHubMatchState state, cancellationToken = ct)
            | Error err when attempt < 2 ->
                logger.LogWarning(
                  "Retrying AFL feed append for {StreamId} after error: {Error}",
                  streamId,
                  err
                )
                do! Task.Delay(TimeSpan.FromMilliseconds(200.), ct)
                return! appendWithRetry (attempt + 1)
            | Error err ->
                logger.LogError(
                  "Failed to append AFL feed update for {StreamId} after {Attempts} attempts: {Error}",
                  streamId,
                  attempt + 1,
                  err
                )
      }
    appendWithRetry 0

  let rec ingestLoop (ct: CancellationToken) =
    task {
      if ct.IsCancellationRequested then
        return ()
      else
        try
          let! stateOpt = feedClient.FetchMatchState(ct)
          match stateOpt with
          | ValueSome state when not (String.IsNullOrWhiteSpace(cfg.StreamId)) ->
              do! appendSnapshot cfg.StreamId state ct
          | _ -> ()
        with ex when not ct.IsCancellationRequested ->
          logger.LogError(ex, "Unhandled exception while processing AFL feed")
        try
          do! Task.Delay(pollInterval, ct)
        with :? TaskCanceledException -> ()
        return! ingestLoop ct
    }

  override _.ExecuteAsync(ct: CancellationToken) =
    if not cfg.Enabled then
      logger.LogInformation("AFL feed ingestion disabled")
      Task.CompletedTask
    elif String.IsNullOrWhiteSpace(cfg.StreamId) then
      logger.LogWarning("AFL feed ingestion requires a streamId configuration")
      Task.CompletedTask
    else
      logger.LogInformation(
        "Starting AFL feed ingestion for stream {StreamId} (polling {Interval}s)",
        cfg.StreamId,
        pollInterval.TotalSeconds
      )
      upcast (ingestLoop ct)
