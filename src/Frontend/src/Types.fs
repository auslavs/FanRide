namespace FanRide.Frontend

open System

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
    CapturedAt: DateTime }

type ConnectionStatus =
  | Disconnected
  | Connecting
  | Connected
  | Error of string

[<CLIMutable>]
type TesMomentumPoint =
  { Watts: int
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
