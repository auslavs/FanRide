module FanRide.Trainer.Program

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR.Client

[<CLIMutable>]
type TrainerMetrics =
  { Watts: int
    Cadence: int
    HeartRate: int }

[<CLIMutable>]
type MatchState =
  { ScoreHome: int
    ScoreAway: int
    Quarter: int
    Clock: string }

module private ConsoleUi =
  let renderState (state: MatchState) =
    printfn "Match: %d - %d · Q%d %s" state.ScoreHome state.ScoreAway state.Quarter state.Clock

  let renderMetrics metrics =
    printfn "Trainer Metrics -> %dw / %drpm / %dbpm" metrics.Watts metrics.Cadence metrics.HeartRate

module TrainerApp =
  let connect (backendUrl: string) =
    let connection =
      HubConnectionBuilder()
        .WithUrl(sprintf "%s/hub/match" backendUrl)
        .WithAutomaticReconnect()
        .Build()
    connection.On<MatchState>("matchState", fun state -> ConsoleUi.renderState state) |> ignore
    connection

  let rec pushMetrics (connection: HubConnection) (rng: Random) (ct: CancellationToken) =
    task {
      if not ct.IsCancellationRequested then
        let watts = rng.Next(150, 420)
        let cadence = rng.Next(70, 110)
        let heart = rng.Next(120, 180)
        let metrics =
          { Watts = watts
            Cadence = cadence
            HeartRate = heart }
        do! connection.SendAsync("SendMetrics", metrics.Watts, metrics.Cadence, metrics.HeartRate, ct)
        ConsoleUi.renderMetrics metrics
        do! Task.Delay(TimeSpan.FromSeconds(5.), ct)
        return! pushMetrics connection rng ct
    }

[<EntryPoint>]
let main argv =
  let backendUrl =
    if argv.Length > 0 then argv.[0] else "http://localhost:5240"
  let rng = Random()
  use cts = new CancellationTokenSource()
  Console.CancelKeyPress.Add(fun args ->
    args.Cancel <- true
    cts.Cancel())
  task {
    let connection = TrainerApp.connect backendUrl
    do! connection.StartAsync()
    printfn "Connected to FanRide backend at %s" backendUrl
    do! TrainerApp.pushMetrics connection rng cts.Token
  }
  |> fun t ->
      try
        t.GetAwaiter().GetResult()
        0
      with ex ->
        eprintfn "Trainer agent failed: %s" ex.Message
        1
