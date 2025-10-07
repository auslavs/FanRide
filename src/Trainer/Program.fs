module FanRide.Trainer.Program

open System
open System.Collections.Generic
open System.Globalization
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR.Client
open FanRide.Trainer

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

  let renderEffect message =
    printfn "Trainer Effect -> %s" message

[<CLIMutable>]
type CliOptions =
  { BackendUrl: string
    StreamId: string
    AdapterName: string option
    DeviceAddress: string option
    UseSimulation: bool }

module private Cli =
  let usage =
    """
FanRide Trainer Agent

Usage:
  dotnet run --project src/Trainer [options]

Options:
  --backend <url>     Backend API base URL (default: http://localhost:5240)
  --stream <id>       Match stream identifier to subscribe to (default: match-1)
  --device <mac>      BLE device address for FTMS trainer
  --adapter <name>    Bluetooth adapter name (default from system)
  --simulate          Force simulated trainer hardware
  --help              Show this message

Environment overrides:
  FANRIDE_TRAINER_DEVICE=<mac>
  FANRIDE_TRAINER_ADAPTER=<name>
  FANRIDE_TRAINER_SIMULATE=true|false
"""

  type private ParseState =
    { Options: CliOptions
      BackendSpecified: bool }

  let private defaultOptions =
    { BackendUrl = "http://localhost:5240"
      StreamId = "match-1"
      AdapterName = None
      DeviceAddress = None
      UseSimulation = false }

  let rec private parseArgs state args =
    match args with
    | [] -> state.Options
    | "--help" :: _ ->
        printfn "%s" usage
        Environment.Exit(0)
        state.Options
    | "--backend" :: url :: rest ->
        parseArgs { state with Options = { state.Options with BackendUrl = url }; BackendSpecified = true } rest
    | "--stream" :: streamId :: rest ->
        parseArgs { state with Options = { state.Options with StreamId = streamId } } rest
    | "--device" :: mac :: rest ->
        parseArgs { state with Options = { state.Options with DeviceAddress = Some mac } } rest
    | "--adapter" :: adapter :: rest ->
        parseArgs { state with Options = { state.Options with AdapterName = Some adapter } } rest
    | "--simulate" :: rest ->
        parseArgs { state with Options = { state.Options with UseSimulation = true } } rest
    | value :: rest when not state.BackendSpecified && not (value.StartsWith("--")) ->
        parseArgs { state with Options = { state.Options with BackendUrl = value }; BackendSpecified = true } rest
    | flag :: _ ->
        eprintfn "Unrecognized option: %s" flag
        printfn "%s" usage
        Environment.Exit(1)
        state.Options

  let parse argv =
    parseArgs { Options = defaultOptions; BackendSpecified = false } (argv |> Array.toList)

module TrainerApp =
  [<CLIMutable>]
  type TrainerEffectDto =
    { StreamId: string
      Id: string
      Kind: string
      Payload: IDictionary<string, obj>
      EnqueuedAt: DateTime }

  let private toCommand (effect: TrainerEffectDto) =
    let tryParseInt (value: obj) =
      match value with
      | :? int as i -> Some i
      | :? int64 as i -> Some(int i)
      | :? uint32 as u -> Some(int u)
      | :? uint64 as u -> Some(int u)
      | :? double as d when not (Double.IsNaN d) -> Some(int (Math.Round(d)))
      | :? float as f when not (Double.IsNaN f) -> Some(int (Math.Round(f)))
      | :? decimal as dec -> Some(int (Math.Round(dec)))
      | :? string as s ->
          match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
          | true, parsed -> Some parsed
          | _ -> None
      | :? IConvertible as convertible ->
          try
            Some(convertible.ToInt32(CultureInfo.InvariantCulture))
          with _ -> None
      | _ -> None

    let target =
      if isNull (box effect.Payload) then
        None
      else
        [ "targetWatts"; "targetPower"; "watts"; "power" ]
        |> List.tryPick (fun key ->
          if effect.Payload.ContainsKey(key) then
            tryParseInt effect.Payload[key]
          else
            None)

    { TargetPowerWatts = target }

  let connect (backendUrl: string) (streamId: string) (hardware: ITrainerHardware) (log: string -> unit) =
    let connection =
      HubConnectionBuilder()
        .WithUrl(sprintf "%s/hub/match" backendUrl)
        .WithAutomaticReconnect()
        .Build()

    connection.On<MatchState>("matchState", fun state ->
      ConsoleUi.renderState state)
    |> ignore

    connection.On<TrainerMetrics>("metrics", fun metrics ->
      ConsoleUi.renderMetrics metrics)
    |> ignore

    connection.On<TrainerEffectDto>("trainerEffect", fun effect ->
      task {
        if effect.StreamId.Equals(streamId, StringComparison.OrdinalIgnoreCase) then
          let command = toCommand effect
          match command.TargetPowerWatts with
          | Some watts -> ConsoleUi.renderEffect ($"Received target power {watts}w")
          | None -> ConsoleUi.renderEffect "Received trainer effect with no power target"
          do! hardware.ApplyEffect command
        else
          log ($"Ignoring trainer effect for stream {effect.StreamId}")
        return ()
      } :> Task)
    |> ignore

    connection

  let subscribe (connection: HubConnection) (streamId: string) =
    connection.InvokeAsync("SubscribeToStream", streamId)

  let rec private pumpMetricsLoop
    (connection: HubConnection)
    (ct: CancellationToken)
    (enumerator: IAsyncEnumerator<TrainerMetrics>) =
    task {
      let! hasNext = enumerator.MoveNextAsync().AsTask()
      if hasNext && not ct.IsCancellationRequested then
        let metrics = enumerator.Current
        do! connection.SendAsync("SendMetrics", metrics.Watts, metrics.Cadence, metrics.HeartRate, ct)
        ConsoleUi.renderMetrics metrics
        return! pumpMetricsLoop connection ct enumerator
    }

  let streamMetrics (connection: HubConnection) (hardware: ITrainerHardware) (ct: CancellationToken) =
    task {
      let enumerator = hardware.Metrics.GetAsyncEnumerator(ct)
      try
        try
          do! pumpMetricsLoop connection ct enumerator
        with
        | :? OperationCanceledException -> ()
      finally
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

[<EntryPoint>]
let main argv =
  let options = Cli.parse argv
  let run =
    task {
      use! hardware =
        Hardware.createHardware
          { AdapterName = options.AdapterName
            DeviceAddress = options.DeviceAddress
            UseSimulation = options.UseSimulation
            Logger = (fun message -> printfn "[Hardware] %s" message) }

      use connection = TrainerApp.connect options.BackendUrl options.StreamId hardware (fun msg -> printfn "[Trainer] %s" msg)
      use cts = new CancellationTokenSource()

      Console.CancelKeyPress.Add(fun args ->
        args.Cancel <- true
        cts.Cancel())

      do! connection.StartAsync()
      printfn "Connected to FanRide backend at %s" options.BackendUrl

      do! TrainerApp.subscribe connection options.StreamId
      printfn "Subscribed to stream %s" options.StreamId

      do! TrainerApp.streamMetrics connection hardware cts.Token
      return 0
    }
  try
    run.GetAwaiter().GetResult()
  with ex ->
    eprintfn "Trainer agent failed: %s" ex.Message
    1
