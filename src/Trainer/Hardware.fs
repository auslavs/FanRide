namespace FanRide.Trainer

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control
open Linux.Bluetooth
open Linux.Bluetooth.Extensions

[<CLIMutable>]
type TrainerMetrics =
  { Watts: int
    Cadence: int
    HeartRate: int }

[<CLIMutable>]
type TrainerEffectCommand =
  { TargetPowerWatts: int option }

type ITrainerHardware =
  inherit IAsyncDisposable
  abstract member Metrics: IAsyncEnumerable<TrainerMetrics>
  abstract member ApplyEffect: TrainerEffectCommand -> Task

type HardwareOptions =
  { AdapterName: string option
    DeviceAddress: string option
    UseSimulation: bool
    Logger: string -> unit }

module private Logging =
  let inline info (logger: string -> unit) (message: string) =
    logger message

module private Channels =
  let inline completeWriter (writer: ChannelWriter<'a>) =
    try
      writer.TryComplete() |> ignore
    with _ -> ()

type SimulatedTrainerHardware(logger: string -> unit) as this =
  let channelOptions =
    UnboundedChannelOptions(SingleReader = true, SingleWriter = true)
  let channel = Channel.CreateUnbounded<TrainerMetrics>(channelOptions)
  let writer = channel.Writer
  let reader = channel.Reader
  let cts = new CancellationTokenSource()
  let rng = Random()
  let mutable targetWatts = 250

  let rec generateMetrics (ct: CancellationToken) =
    task {
      if not ct.IsCancellationRequested then
        let watts =
          targetWatts
          + rng.Next(-20, 21)
        let cadence = rng.Next(70, 95)
        let heartRate = rng.Next(120, 180)
        let metrics =
          { Watts = Math.Max(0, watts)
            Cadence = cadence
            HeartRate = heartRate }
        do! writer.WriteAsync(metrics, ct).AsTask()
        do! Task.Delay(TimeSpan.FromSeconds(5.), ct)
        return! generateMetrics ct
    }

  let pumpTask = Task.Run(fun () -> generateMetrics cts.Token :> Task)

  member _.StopAsync() =
    task {
      cts.Cancel()
      Channels.completeWriter writer
      try
        do! pumpTask
      with
      | :? OperationCanceledException -> ()
      cts.Dispose()
    }

  interface ITrainerHardware with
    member _.Metrics = reader.ReadAllAsync()

    member _.ApplyEffect(command: TrainerEffectCommand) =
      match command.TargetPowerWatts with
      | Some watts when watts > 0 ->
          targetWatts <- watts
          Logging.info logger ($"Simulated trainer target set to {watts}w")
          Task.CompletedTask
      | Some _ ->
          Logging.info logger "Simulated trainer received non-positive target power; ignoring"
          Task.CompletedTask
      | None -> Task.CompletedTask

    member _.DisposeAsync() =
      ValueTask(this.StopAsync())

type FtmsTrainerHardware(
  adapterName: string option,
  deviceAddress: string,
  logger: string -> unit
) as this =

  let channelOptions =
    UnboundedChannelOptions(SingleReader = true, SingleWriter = true)
  let channel = Channel.CreateUnbounded<TrainerMetrics>(channelOptions)
  let writer = channel.Writer
  let reader = channel.Reader

  let mutable connectTask: Task option = None
  let mutable adapter: Adapter option = None
  let mutable device: Device option = None
  let mutable measurement: GattCharacteristic option = None
  let mutable measurementHandler: GattCharacteristicEventHandlerAsync option = None
  let mutable control: GattCharacteristic option = None

  let ftmsServiceUuid = BlueZManager.NormalizeUUID("1826")
  let measurementUuid = BlueZManager.NormalizeUUID("2ad2")
  let controlPointUuid = BlueZManager.NormalizeUUID("2ad9")

  let hasFlag (flags: int) bit = (flags &&& (1 <<< bit)) <> 0

  let parseMeasurement (value: byte[]) =
    if isNull value || value.Length < 4 then
      None
    else
      let flags = int value.[0] ||| (int value.[1] <<< 8)
      let mutable offset = 2

      let tryAdvance count =
        if offset + count <= value.Length then
          offset <- offset + count
          true
        else
          false

      let tryReadUInt16 () =
        if offset + 2 <= value.Length then
          let raw = int value.[offset] ||| (int value.[offset + 1] <<< 8)
          offset <- offset + 2
          Some(uint16 raw)
        else
          None

      let tryReadInt16 () =
        tryReadUInt16 ()
        |> Option.map int16

      let tryReadByte () =
        if offset < value.Length then
          let b = value.[offset]
          offset <- offset + 1
          Some b
        else
          None

      // Instantaneous speed is always present per spec; skip value.
      let _ = tryReadUInt16 ()

      if hasFlag flags 1 then ignore (tryReadUInt16())

      let cadence =
        if hasFlag flags 2 then
          match tryReadUInt16 () with
          | Some raw -> Some(int (float raw / 2.0))
          | None -> None
        else
          None

      if hasFlag flags 3 then ignore (tryReadUInt16())
      if hasFlag flags 4 then ignore (tryAdvance 3)
      if hasFlag flags 5 then ignore (tryReadInt16())

      let watts =
        if hasFlag flags 6 then
          match tryReadInt16 () with
          | Some raw -> Some(int raw)
          | None -> None
        else
          None

      if hasFlag flags 7 then ignore (tryReadInt16())
      if hasFlag flags 8 then ignore (tryReadUInt16())
      if hasFlag flags 9 then ignore (tryReadUInt16())
      if hasFlag flags 10 then ignore (tryReadByte())

      let heartRate =
        if hasFlag flags 11 then
          tryReadByte () |> Option.map int
        else
          None

      Some
        { Watts = watts |> Option.defaultValue 0
          Cadence = cadence |> Option.defaultValue 0
          HeartRate = heartRate |> Option.defaultValue 0 }

  let connectInternal () =
    task {
      let log = Logging.info logger
      log "Connecting to FTMS trainer over Bluetooth..."

      let! adapterInstance =
        match adapterName with
        | Some name -> BlueZManager.GetAdapterAsync(name)
        | None ->
            task {
              let! adapters = BlueZManager.GetAdaptersAsync()
              if adapters.Count = 0 then
                return raise (InvalidOperationException "No Bluetooth adapters available")
              else
                return adapters.[0]
            }
      adapter <- Some adapterInstance

      try
        let! powered = adapterInstance.GetPoweredAsync()
        if not powered then do! adapterInstance.SetPoweredAsync(true)
      with ex ->
        log ($"Unable to ensure adapter power state: {ex.Message}")

      let! deviceInstance = adapterInstance.GetDeviceAsync(deviceAddress)
      if isNull (box deviceInstance) then
        raise (InvalidOperationException($"Device {deviceAddress} not found"))

      device <- Some deviceInstance

      log ($"Connecting to device {deviceAddress}...")
      do! deviceInstance.ConnectAsync()
      let timeout = TimeSpan.FromSeconds(20.)
      do! deviceInstance.WaitForPropertyValueAsync("Connected", true, timeout)
      do! deviceInstance.WaitForPropertyValueAsync("ServicesResolved", true, timeout)

      let! service = deviceInstance.GetServiceAsync(ftmsServiceUuid)
      if isNull (box service) then
        raise (InvalidOperationException "FTMS service not available on device")

      let! measurementChar = service.GetCharacteristicAsync(measurementUuid)
      if isNull (box measurementChar) then
        raise (InvalidOperationException "FTMS measurement characteristic not found")

      let handler =
        GattCharacteristicEventHandlerAsync(fun _ args ->
          task {
            match parseMeasurement args.Value with
            | Some metrics ->
                if not (writer.TryWrite(metrics)) then
                  log "Measurement channel back-pressure detected; dropping metric"
            | None ->
                log "Received FTMS measurement payload that could not be parsed"
            return ()
          } :> Task)

      measurementChar.add_Value(handler)
      measurement <- Some measurementChar
      measurementHandler <- Some handler

      let! controlChar = service.GetCharacteristicAsync(controlPointUuid)
      if isNull (box controlChar) then
        raise (InvalidOperationException "FTMS control point characteristic not found")

      control <- Some controlChar
      log "FTMS trainer connection established"
    }

  member _.ConnectAsync() =
    match connectTask with
    | Some task -> task
    | None ->
        let task =
          task {
            try
              do! connectInternal()
            with ex ->
              Channels.completeWriter writer
              raise ex
          } :> Task
        connectTask <- Some task
        task

  member private _.DisposeMeasurementAsync() =
    task {
      match measurement, measurementHandler with
      | Some m, Some handler ->
          try
            m.remove_Value(handler)
          with _ -> ()
          measurementHandler <- None
          try
            do! m.StopNotifyAsync()
          with _ -> ()
          m.Dispose()
          measurement <- None
      | _ -> ()
    }

  member private _.DisposeControlAsync() =
    task {
      match control with
      | Some c ->
          c.Dispose()
          control <- None
      | None -> ()
    }

  member private _.DisposeDeviceAsync() =
    task {
      match device with
      | Some d ->
          try
            do! d.DisconnectAsync()
          with _ -> ()
          d.Dispose()
          device <- None
      | None -> ()
    }

  member private _.DisposeAdapter() =
    match adapter with
    | Some a ->
        a.Dispose()
        adapter <- None
    | None -> ()

  interface ITrainerHardware with
    member _.Metrics = reader.ReadAllAsync()

    member _.ApplyEffect(command: TrainerEffectCommand) =
      task {
        match command.TargetPowerWatts, control with
        | Some watts, Some c when watts > 0 ->
            let limited = Math.Clamp(watts, 1, 2000)
            let raw = uint16 limited
            let payload =
              let value = int raw
              [| 0x04uy
                 byte value
                 byte ((value >>> 8) &&& 0xFF) |]
            try
              do! c.WriteValueAsync(payload, Dictionary<string, obj>())
              Logging.info logger ($"Applied target power {limited}w to trainer")
            with ex ->
              Logging.info logger ($"Failed to send trainer effect: {ex.Message}")
        | Some _, Some _ ->
          Logging.info logger "Trainer effect ignored because watt target was not positive"
        | Some watts, None ->
          Logging.info logger ($"Control characteristic not ready; could not apply {watts}w command")
        | None, _ -> ()
      } :> Task

    member _.DisposeAsync() =
      ValueTask(
        task {
          Channels.completeWriter writer
          do! this.DisposeMeasurementAsync()
          do! this.DisposeControlAsync()
          do! this.DisposeDeviceAsync()
          this.DisposeAdapter()
        }
      )

module Hardware =
  let private tryGetEnv name =
    match Environment.GetEnvironmentVariable(name) with
    | null | "" -> None
    | value -> Some value

  let private tryGetEnvBool name =
    match tryGetEnv name with
    | None -> None
    | Some value ->
        match Boolean.TryParse(value) with
        | true, parsed -> Some parsed
        | _ ->
            match value.Trim().ToLowerInvariant() with
            | "1" | "y" | "yes" | "true" -> Some true
            | "0" | "n" | "no" | "false" -> Some false
            | _ -> None

  let createHardware (options: HardwareOptions) =
    task {
      let logger = if isNull (box options.Logger) then ignore else options.Logger
      let envDevice = tryGetEnv "FANRIDE_TRAINER_DEVICE"
      let envAdapter = tryGetEnv "FANRIDE_TRAINER_ADAPTER"
      let envSim = tryGetEnvBool "FANRIDE_TRAINER_SIMULATE" |> Option.defaultValue false

      let useSimulation = options.UseSimulation || envSim
      let deviceAddress = options.DeviceAddress |> Option.orElse envDevice
      let adapterName = options.AdapterName |> Option.orElse envAdapter

      if useSimulation then
        Logging.info logger "Using simulated trainer hardware"
        return new SimulatedTrainerHardware(logger) :> ITrainerHardware
      else
        match deviceAddress with
        | Some address ->
            let hardware = new FtmsTrainerHardware(adapterName, address, logger)
            do! hardware.ConnectAsync()
            return hardware :> ITrainerHardware
        | None ->
            Logging.info logger "No trainer device specified; falling back to simulation"
            return new SimulatedTrainerHardware(logger) :> ITrainerHardware
    }
