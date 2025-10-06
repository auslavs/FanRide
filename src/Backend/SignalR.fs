namespace FanRide

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR

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

type MatchHub() =
  inherit Hub()

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

