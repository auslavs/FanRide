namespace FanRide

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Logging
open Newtonsoft.Json.Linq

[<CLIMutable>]
type TesMomentumPointDto =
  { Watts: int
    Cadence: int
    HeartRate: int
    CapturedAt: DateTime }

[<CLIMutable>]
type TesMomentumDto =
  { StreamId: string
    Points: TesMomentumPointDto array }

[<CLIMutable>]
type LeaderboardEntryDto =
  { RiderId: string
    Watts: int
    Cadence: int
    HeartRate: int
    UpdatedAt: DateTime }

[<CLIMutable>]
type LeaderboardDto =
  { Entries: LeaderboardEntryDto array
    GeneratedAt: DateTime }

[<CLIMutable>]
type MatchStateReadModelDto =
  { StreamId: string
    ScoreHome: int
    ScoreAway: int
    Quarter: int
    Clock: string
    UpdatedAt: DateTime }

[<CLIMutable>]
type TrainerEffectDto =
  { StreamId: string
    Id: string
    Kind: string
    Payload: IDictionary<string, obj>
    EnqueuedAt: DateTime }

[<CLIMutable>]
type TrainerEffectEnvelopeDto =
  { Effects: TrainerEffectDto array
    Cursor: string option }

type IReadModelService =
  abstract member GetMatchState : streamId:string -> Task<MatchStateReadModelDto option>
  abstract member GetTesMomentum : streamId:string * ?maxPoints:int -> Task<TesMomentumDto option>
  abstract member GetLeaderboard : ?top:int -> Task<LeaderboardDto>

type ReadModelService(
  client: CosmosClient,
  cosmosCfg: CosmosConfig,
  logger: ILogger<ReadModelService>
) =
  let database = client.GetDatabase(cosmosCfg.Database)
  let matchStateContainer = database.GetContainer(cosmosCfg.Containers.RmMatchState)
  let tesHistoryContainer = database.GetContainer(cosmosCfg.Containers.RmTesHistory)
  let leaderboardContainer = database.GetContainer(cosmosCfg.Containers.RmLeaderboard)

  let tryGetToken (token: JToken) (name: string) =
    let child = token.[name]
    if obj.ReferenceEquals(child, null) then None else Some child

  let tryGetInt (token: JToken) (names: string list) =
    names
    |> List.tryPick (fun name ->
      match tryGetToken token name with
      | None -> None
      | Some value ->
          try
            Some(value.Value<int>())
          with _ -> None)

  let tryGetString (token: JToken) (names: string list) =
    names
    |> List.tryPick (fun name ->
      match tryGetToken token name with
      | None -> None
      | Some value ->
          try
            Some(value.Value<string>())
          with _ -> None)

  let tryGetDate (token: JToken) (names: string list) =
    names
    |> List.tryPick (fun name ->
      match tryGetToken token name with
      | None -> None
      | Some value ->
          try
            Some(value.Value<DateTime>())
          with _ -> None)

  let toMomentumPoint (token: JToken) (timestamp: DateTime) =
    let watts = tryGetInt token [ "watts"; "Watts" ] |> Option.defaultValue 0
    let cadence = tryGetInt token [ "cadence"; "Cadence" ] |> Option.defaultValue 0
    let heartRate = tryGetInt token [ "heartRate"; "HeartRate" ] |> Option.defaultValue 0
    { Watts = watts
      Cadence = cadence
      HeartRate = heartRate
      CapturedAt = timestamp }

  let toLeaderboardEntry (doc: JObject) (metrics: JToken) =
    let riderId =
      tryGetString doc [ "streamId"; "id" ]
      |> Option.defaultValue "unknown"
    { RiderId = riderId
      Watts = tryGetInt metrics [ "watts"; "Watts" ] |> Option.defaultValue 0
      Cadence = tryGetInt metrics [ "cadence"; "Cadence" ] |> Option.defaultValue 0
      HeartRate = tryGetInt metrics [ "heartRate"; "HeartRate" ] |> Option.defaultValue 0
      UpdatedAt = tryGetDate doc [ "updatedAt"; "ts" ] |> Option.defaultValue DateTime.UtcNow }

  interface IReadModelService with
    member _.GetMatchState(streamId: string) =
      task {
        if String.IsNullOrWhiteSpace(streamId) then
          return None
        else
          try
            let! response = matchStateContainer.ReadItemAsync<JObject>(streamId, PartitionKey streamId)
            let resource = response.Resource
            if obj.ReferenceEquals(resource, null) then
              return None
            else
              match tryGetToken resource "state" with
              | None -> return None
              | Some snapshot ->
                  let scoreToken = tryGetToken snapshot "score"
                  let scoreHome =
                    match scoreToken with
                    | None -> 0
                    | Some token -> tryGetInt token [ "home"; "Home" ] |> Option.defaultValue 0
                  let scoreAway =
                    match scoreToken with
                    | None -> 0
                    | Some token -> tryGetInt token [ "away"; "Away" ] |> Option.defaultValue 0
                  let quarter = tryGetInt snapshot [ "quarter"; "Quarter" ] |> Option.defaultValue 0
                  let clock = tryGetString snapshot [ "clock"; "Clock" ] |> Option.defaultValue "00:00"
                  let updatedAt =
                    tryGetDate resource [ "updatedAt"; "ts" ]
                    |> Option.defaultValue DateTime.UtcNow
                  return Some
                    { StreamId = streamId
                      ScoreHome = scoreHome
                      ScoreAway = scoreAway
                      Quarter = quarter
                      Clock = clock
                      UpdatedAt = updatedAt }
          with :? CosmosException as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
            logger.LogDebug("Match state read model not found for {StreamId}", streamId)
            return None
      }

    member _.GetTesMomentum(streamId: string, ?maxPoints: int) =
      task {
        if String.IsNullOrWhiteSpace(streamId) then
          return None
        else
          let limit = defaultArg maxPoints 60
          let query =
            QueryDefinition("SELECT TOP @limit c.metrics, c.ts FROM c WHERE c.streamId = @streamId ORDER BY c.ts DESC")
              .WithParameter("@limit", limit)
              .WithParameter("@streamId", streamId)
          let iterator = tesHistoryContainer.GetItemQueryIterator<JObject>(query)
          let points = ResizeArray()
          while iterator.HasMoreResults do
            let! response = iterator.ReadNextAsync()
            for doc in response do
              match tryGetToken doc "metrics" with
              | None -> ()
              | Some metricsToken ->
                  let timestamp =
                    tryGetDate doc [ "ts"; "timestamp"; "Timestamp" ]
                    |> Option.orElseWith(fun () -> tryGetDate metricsToken [ "capturedAt"; "CapturedAt"; "timestamp"; "Timestamp" ])
                    |> Option.defaultValue DateTime.UtcNow
                  points.Add(toMomentumPoint metricsToken timestamp)
          if points.Count = 0 then return None
          else
            let ordered =
              points
              |> Seq.sortBy (fun p -> p.CapturedAt)
              |> Seq.toArray
            return Some { StreamId = streamId; Points = ordered }
      }

    member _.GetLeaderboard(?top: int) =
      task {
        let limit = defaultArg top 10
        let query =
          QueryDefinition("SELECT TOP @limit c.streamId, c.metrics, c.updatedAt FROM c ORDER BY c.metrics.watts DESC")
            .WithParameter("@limit", limit)
        let iterator = leaderboardContainer.GetItemQueryIterator<JObject>(query)
        let entries = ResizeArray()
        while iterator.HasMoreResults do
          let! response = iterator.ReadNextAsync()
          for doc in response do
            match tryGetToken doc "metrics" with
            | None -> ()
            | Some metricsToken ->
                entries.Add(toLeaderboardEntry doc metricsToken)
        return
          { Entries = entries |> Seq.toArray
            GeneratedAt = DateTime.UtcNow }
      }
