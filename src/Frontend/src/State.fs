namespace FanRide.Frontend

open System
open Elmish

[<CLIMutable>]
type Model =
  { Status: ConnectionStatus
    Match: MatchState
    Metrics: TrainerMetrics
    Notifications: string list }

type Msg =
  | Start
  | ConnectionEstablished of obj
  | ConnectionFailed of string
  | MatchStateArrived of MatchState
  | MetricsTick of TrainerMetrics
  | SendMetrics
  | MetricsSent of Result<unit, string>
  | ClearNotification of int

module State =
  let init () =
    { Status = ConnectionStatus.Disconnected
      Match =
        { Score = { Home = 0; Away = 0 }
          Quarter = 1
          Clock = "00:00" }
      Metrics =
        { Watts = 0
          Cadence = 0
          HeartRate = 0
          CapturedAt = DateTime.UtcNow }
      Notifications = [] },
    Cmd.ofMsg Start

  let private pushNotification message model =
    let notifications =
      (message :: model.Notifications)
      |> List.truncate 5
    { model with Notifications = notifications }

  let update (sendMetrics: TrainerMetrics -> Async<Result<unit, string>>) msg model =
    match msg with
    | Start ->
        { model with Status = ConnectionStatus.Connecting }, Cmd.none
    | ConnectionEstablished _ ->
        { model with Status = ConnectionStatus.Connected }, Cmd.none
    | ConnectionFailed error ->
        { model with Status = ConnectionStatus.Error error }, Cmd.none
    | MatchStateArrived state ->
        { model with Match = state }, Cmd.none
    | MetricsTick metrics ->
        let updated = { model with Metrics = metrics }
        updated, Cmd.ofMsg SendMetrics
    | SendMetrics ->
        let command =
          Cmd.OfAsync.either
            sendMetrics
            model.Metrics
            MetricsSent
            (fun ex -> MetricsSent(Result.Error ex.Message))
        model, command
    | MetricsSent (Ok _) ->
        let message =
          sprintf "Metrics sent at %s" (DateTime.UtcNow.ToString("HH:mm:ss"))
        pushNotification message model, Cmd.none
    | MetricsSent (Result.Error error) ->
        pushNotification ($"Metrics failed: {error}") model, Cmd.none
    | ClearNotification index ->
        let notifications =
          model.Notifications
          |> List.mapi (fun i n -> i, n)
          |> List.filter (fun (i, _) -> i <> index)
          |> List.map snd
        { model with Notifications = notifications }, Cmd.none
