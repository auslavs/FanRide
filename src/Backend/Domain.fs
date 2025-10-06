namespace FanRide

open System
open System.Text.Json

[<CLIMutable>]
type Score =
  { Home: int
    Away: int }

[<CLIMutable>]
type MatchState =
  { Score: Score
    Quarter: int
    Clock: string }

[<CLIMutable>]
type TrainerMetrics =
  { Watts: int
    Cadence: int
    HeartRate: int
    Timestamp: DateTime }

[<CLIMutable>]
type TesHistoryPoint =
  { StreamId: string
    Watts: int
    Cadence: int
    HeartRate: int
    CapturedAt: DateTime }

[<CLIMutable>]
type LeaderboardEntry =
  { RiderId: string
    Watts: int
    Cadence: int
    HeartRate: int
    UpdatedAt: DateTime }

type EventPayload =
  | MatchStateUpdated of MatchState
  | TrainerMetricsCaptured of TrainerMetrics
  | GenericPayload of JsonElement

[<CLIMutable>]
type NewEvent =
  { Id: string
    Kind: string
    Payload: EventPayload }

[<CLIMutable>]
type OutboxMessage =
  { Id: string
    Kind: string
    Payload: JsonElement }

module EventPayload =
  let toDocument (payload: EventPayload) =
    match payload with
    | MatchStateUpdated state -> JsonSerializer.SerializeToElement(state)
    | TrainerMetricsCaptured metrics -> JsonSerializer.SerializeToElement(metrics)
    | GenericPayload json -> json

module Outbox =
  let fromEvents (events: NewEvent list) =
    events
    |> List.choose (fun ev ->
      match ev.Payload with
      | TrainerMetricsCaptured metrics ->
          let doc = JsonSerializer.SerializeToElement(metrics)
          Some
            { Id = $"out-{ev.Id}"
              Kind = "trainerEffect"
              Payload = doc }
      | _ -> None)

