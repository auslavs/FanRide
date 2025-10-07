namespace FanRide

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging

[<CLIMutable>]
type MetricsDto =
  { Watts: int
    Cadence: int
    HeartRate: int }

[<CLIMutable>]
type MatchStateDto =
  { ScoreHome: int
    ScoreAway: int
    Quarter: int
    Clock: string }

type MatchHub(
  readModels: IReadModelService,
  logger: ILogger<MatchHub>
) =
  inherit Hub()

  let toHubMatchState (model: MatchStateReadModelDto) : MatchStateDto =
    { ScoreHome = model.ScoreHome
      ScoreAway = model.ScoreAway
      Quarter = model.Quarter
      Clock = model.Clock }

  member this.SendMetrics(watts: int, cadence: int, heartRate: int) =
    task {
      let payload =
        { Watts = watts
          Cadence = cadence
          HeartRate = heartRate }
      do! this.Clients.Others.SendAsync("metrics", payload)
    } :> Task

  member this.BroadcastMatchState(state: MatchStateDto) =
    this.Clients.All.SendAsync("matchState", state)

  member this.SubscribeToStream(streamId: string) =
    task {
      if String.IsNullOrWhiteSpace(streamId) then
        logger.LogWarning("Received subscription with empty stream id from connection {ConnectionId}", this.Context.ConnectionId)
      else
        let! matchState = readModels.GetMatchState(streamId)
        match matchState with
        | Some state ->
            do! this.Clients.Caller.SendAsync("matchState", toHubMatchState state)
        | None ->
            logger.LogDebug(
              "No match state read model available for stream {StreamId} when subscribing",
              streamId
            )

        let! tesMomentum = readModels.GetTesMomentum(streamId)
        match tesMomentum with
        | Some momentum -> do! this.Clients.Caller.SendAsync("tesHistory", momentum)
        | None -> ()

        let! leaderboard = readModels.GetLeaderboard()
        do! this.Clients.Caller.SendAsync("leaderboard", leaderboard)
    } :> Task

