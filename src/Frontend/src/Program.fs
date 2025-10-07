namespace FanRide.Frontend

open System
open Elmish
open Elmish.React
open Feliz
open FanRide.Frontend
open FanRide.Frontend.State

module App =
#if FABLE_COMPILER
  open Fable.Core
  open Fable.Core.JsInterop
  open Fable.Core.JS

  let signalR: obj = importAll "@microsoft/signalr"

  let mutable private connection: obj option = None

  let private createConnection url =
    let builder: obj = createNew signalR?HubConnectionBuilder ()
    builder?withUrl(url)?withAutomaticReconnect()?build()

  let private toMatchState (payload: obj) =
    { Score =
        { Home = payload?scoreHome |> unbox<int>
          Away = payload?scoreAway |> unbox<int> }
      Quarter = payload?quarter |> unbox<int>
      Clock = payload?clock |> unbox<string> }

  let private toMetrics (payload: obj) =
    { Watts = payload?watts |> unbox<int>
      Cadence = payload?cadence |> unbox<int>
      HeartRate = payload?heartRate |> unbox<int>
      CapturedAt = DateTime.UtcNow }

  let private parseIso (value: string) =
    match DateTime.TryParse(value) with
    | true, parsed -> parsed
    | _ -> DateTime.UtcNow

  let private toTesMomentum (payload: obj) =
    let points: obj array = payload?points |> unbox
    points
    |> Array.map (fun point ->
      { Watts = point?watts |> unbox<int>
        Cadence = point?cadence |> unbox<int>
        HeartRate = point?heartRate |> unbox<int>
        CapturedAt = point?capturedAt |> unbox<string> |> parseIso })
    |> Array.toList

  let private toLeaderboard (payload: obj) =
    let entries: obj array = payload?entries |> unbox
    entries
    |> Array.map (fun entry ->
      { RiderId = entry?riderId |> unbox<string>
        Watts = entry?watts |> unbox<int>
        Cadence = entry?cadence |> unbox<int>
        HeartRate = entry?heartRate |> unbox<int>
        UpdatedAt = entry?updatedAt |> unbox<string> |> parseIso })
    |> Array.toList

  let private registerHandlers (conn: obj) (dispatch: Msg -> unit) =
    conn?on("matchState", fun payload ->
      payload |> toMatchState |> MatchStateArrived |> dispatch)
    conn?on("metrics", fun payload ->
      payload |> toMetrics |> MetricsTick |> dispatch)
    conn?on("tesHistory", fun payload ->
      payload |> toTesMomentum |> TesMomentumUpdated |> dispatch)
    conn?on("leaderboard", fun payload ->
      payload |> toLeaderboard |> LeaderboardUpdated |> dispatch)
    conn?on("trainerEffect", fun payload ->
      let streamId: string = payload?streamId |> unbox
      let kind: string = payload?kind |> unbox
      TrainerEffectReceived(streamId, kind) |> dispatch)
    conn

  let private startConnection dispatch =
    let conn = createConnection "/hub/match" |> registerHandlers dispatch
    let startPromise: JS.Promise<obj> = conn?start()
    startPromise
      .``then``(fun _ ->
        connection <- Some conn
        dispatch (ConnectionEstablished conn)
        null)
      .``catch``(fun error ->
        connection <- None
        dispatch (ConnectionFailed(string error))
        null)
    |> ignore

  let private awaitUnit (promise: JS.Promise<obj>) =
    Async.FromContinuations(fun (resolve, reject, _) ->
      promise
        .``then``(fun _ ->
          resolve ()
          null)
        .``catch``(fun error ->
          reject (Exception(string error))
          null)
      |> ignore)

  let private sendMetrics metrics =
    async {
      match connection with
      | Some conn ->
          let promise: JS.Promise<obj> = conn?invoke("SendMetrics", metrics.Watts, metrics.Cadence, metrics.HeartRate)
          try
            do! awaitUnit promise
            return Ok()
          with ex ->
            return Result.Error ex.Message
      | None ->
          return Result.Error "No active SignalR connection"
    }

  let private subscribe streamId =
    async {
      match connection with
      | Some conn ->
          let promise: JS.Promise<obj> = conn?invoke("SubscribeToStream", streamId)
          try
            do! awaitUnit promise
            return Ok()
          with ex ->
            return Result.Error ex.Message
      | None ->
          return Result.Error "No active SignalR connection"
    }
#else
  let private sendMetrics (_: TrainerMetrics) = async { return Ok() }
  let private startConnection (_: Msg -> unit) = ()
#endif

#if !FABLE_COMPILER
  let private subscribe (_: string) = async { return Ok() }
#endif

  let init () = State.init()

  let update msg model =
    let model', cmd' = State.update sendMetrics msg model
    match msg with
    | Start ->
#if FABLE_COMPILER
        let startCmd = Cmd.ofEffect startConnection
        model', Cmd.batch [ cmd'; startCmd ]
#else
        model', cmd'
#endif
    | ConnectionEstablished _ ->
#if FABLE_COMPILER
        let trigger = Cmd.ofMsg SubscribeToStream
        model', Cmd.batch [ cmd'; trigger ]
#else
        model', cmd'
#endif
    | SubscribeToStream ->
#if FABLE_COMPILER
        let subscribeCmd =
          Cmd.OfAsync.either
            subscribe
            model'.ActiveStreamId
            SubscriptionCompleted
            (fun ex -> SubscriptionCompleted(Result.Error ex.Message))
        model', Cmd.batch [ cmd'; subscribeCmd ]
#else
        model', cmd'
#endif
    | ConnectionFailed _ ->
#if FABLE_COMPILER
        connection <- None
#endif
        model', cmd'
    | SubscriptionCompleted _ ->
        model', cmd'
    | _ ->
        model', cmd'

  let view model dispatch =
    let statusText =
      match model.Status with
      | ConnectionStatus.Disconnected -> "Disconnected"
      | ConnectionStatus.Connecting -> "Connecting"
      | ConnectionStatus.Connected -> "Connected"
      | ConnectionStatus.Error error -> $"Error: {error}"

    let momentumSeries =
      model.TesMomentum
      |> List.sortBy (fun point -> point.CapturedAt)
      |> fun points ->
           let count = List.length points
           if count > 40 then points |> List.skip (count - 40) else points

    let latestMomentum = momentumSeries |> List.tryLast

    let sparkline =
      match momentumSeries with
      | [] ->
          Html.p [
            prop.className "text-sm text-slate-500"
            prop.text "Waiting for TES momentum data"
          ]
      | data ->
          let width = 320
          let height = 120
          let widthF = float width
          let heightF = float height
          let minWatts = data |> List.minBy (fun p -> p.Watts) |> fun p -> p.Watts
          let maxWatts = data |> List.maxBy (fun p -> p.Watts) |> fun p -> p.Watts
          let range = float (max 1 (maxWatts - minWatts))
          let step = if data.Length <= 1 then 0. else widthF / float (max 1 (data.Length - 1))
          let pointsString =
            data
            |> List.mapi (fun idx point ->
              let x = if data.Length <= 1 then widthF else step * float idx
              let normalized = (float (point.Watts - minWatts)) / range
              let y = heightF - (normalized * heightF)
              sprintf "%.2f,%.2f" x y)
            |> String.concat " "
          Svg.svg [
            svg.viewBox (0, 0, width, height)
            svg.className "w-full h-32"
            svg.children [
              Svg.polyline [
                svg.points pointsString
                svg.fill "none"
                svg.stroke "#22d3ee"
                svg.strokeWidth 3
              ]
            ]
          ]

    let leaderboardRows =
      if List.isEmpty model.Leaderboard then
        Html.p [
          prop.className "text-sm text-slate-500"
          prop.text "Leaderboard projections unavailable"
        ]
      else
        Html.table [
          prop.className "w-full border-collapse text-sm"
          prop.children [
            Html.thead [
              Html.tr [
                prop.className "text-left text-slate-400"
                prop.children [
                  Html.th [ prop.className "py-2"; prop.text "Rider" ]
                  Html.th [ prop.className "py-2 text-right"; prop.text "Watts" ]
                  Html.th [ prop.className "py-2 text-right"; prop.text "Cadence" ]
                  Html.th [ prop.className "py-2 text-right"; prop.text "HR" ]
                ]
              ]
            ]
            Html.tbody [
              prop.children (
                model.Leaderboard
                |> List.mapi (fun idx entry ->
                  Html.tr [
                    prop.className (if idx = 0 then "bg-slate-800/60" else "")
                    prop.children [
                      Html.td [ prop.className "py-2 font-medium"; prop.text entry.RiderId ]
                      Html.td [ prop.className "py-2 text-right"; prop.text (string entry.Watts) ]
                      Html.td [ prop.className "py-2 text-right"; prop.text (string entry.Cadence) ]
                      Html.td [ prop.className "py-2 text-right"; prop.text (string entry.HeartRate) ]
                    ]
                  ])
                |> List.toArray)
            ]
          ]
        ]

    Html.section [
      prop.className "min-h-screen bg-slate-950 text-slate-100"
      prop.children [
        Html.header [
          prop.className "px-6 py-4 border-b border-slate-800"
          prop.children [
            Html.h1 [
              prop.className "text-2xl font-semibold"
              prop.text "FanRide Live AFL"
            ]
            Html.p [
              prop.className "text-sm text-slate-400"
              prop.text $"Connection status: {statusText}"
            ]
          ]
        ]
        Html.main [
          prop.className "px-6 py-10 grid gap-6 lg:grid-cols-3"
          prop.children [
            Html.div [
              prop.className "rounded-xl border border-slate-800 bg-slate-900/70 p-6"
              prop.children [
                Html.h2 [
                  prop.className "text-lg font-medium mb-4"
                  prop.text "Scoreboard"
                ]
                Html.div [
                  prop.className "flex items-center justify-between text-4xl font-semibold"
                  prop.children [
                    Html.span [ prop.text (string model.Match.Score.Home) ]
                    Html.span [ prop.className "text-slate-500 text-lg"; prop.text "vs" ]
                    Html.span [ prop.text (string model.Match.Score.Away) ]
                  ]
                ]
                Html.p [
                  prop.className "text-sm text-slate-400 mt-4"
                  prop.text $"Q{model.Match.Quarter} · {model.Match.Clock}"
                ]
              ]
            ]
            Html.div [
              prop.className "rounded-xl border border-slate-800 bg-slate-900/70 p-6"
              prop.children [
                Html.h2 [
                  prop.className "text-lg font-medium mb-4"
                  prop.text "Trainer Metrics"
                ]
                Html.dl [
                  prop.className "grid grid-cols-2 gap-4"
                  prop.children [
                    Html.div [
                      prop.children [
                        Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Watts" ]
                        Html.dd [ prop.className "text-3xl font-semibold"; prop.text (string model.Metrics.Watts) ]
                      ]
                    ]
                    Html.div [
                      prop.children [
                        Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Cadence" ]
                        Html.dd [ prop.className "text-3xl font-semibold"; prop.text (string model.Metrics.Cadence) ]
                      ]
                    ]
                    Html.div [
                      prop.children [
                        Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Heart Rate" ]
                        Html.dd [ prop.className "text-3xl font-semibold"; prop.text (string model.Metrics.HeartRate) ]
                      ]
                    ]
                    Html.div [
                      prop.children [
                        Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Captured" ]
                        Html.dd [ prop.className "text-sm text-slate-400"; prop.text (model.Metrics.CapturedAt.ToString("HH:mm:ss")) ]
                      ]
                    ]
                  ]
                ]
              ]
            ]
            Html.div [
              prop.className "rounded-xl border border-slate-800 bg-slate-900/70 p-6 lg:col-span-2"
              prop.children [
                Html.h2 [
                  prop.className "text-lg font-medium mb-4"
                  prop.text "TES Momentum"
                ]
                sparkline
                match latestMomentum with
                | Some point ->
                    Html.dl [
                      prop.className "mt-4 grid grid-cols-3 gap-4"
                      prop.children [
                        Html.div [
                          prop.children [
                            Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Watts" ]
                            Html.dd [ prop.className "text-2xl font-semibold"; prop.text (string point.Watts) ]
                          ]
                        ]
                        Html.div [
                          prop.children [
                            Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Cadence" ]
                            Html.dd [ prop.className "text-2xl font-semibold"; prop.text (string point.Cadence) ]
                          ]
                        ]
                        Html.div [
                          prop.children [
                            Html.dt [ prop.className "text-xs uppercase tracking-wide text-slate-500"; prop.text "Heart Rate" ]
                            Html.dd [ prop.className "text-2xl font-semibold"; prop.text (string point.HeartRate) ]
                          ]
                        ]
                      ]
                    ]
                | None -> Html.none
              ]
            ]
            Html.div [
              prop.className "rounded-xl border border-slate-800 bg-slate-900/70 p-6"
              prop.children [
                Html.h2 [
                  prop.className "text-lg font-medium mb-4"
                  prop.text "Leaderboard"
                ]
                leaderboardRows
              ]
            ]
            Html.div [
              prop.className "lg:col-span-3 rounded-xl border border-slate-800 bg-slate-900/70 p-6"
              prop.children [
                Html.h2 [
                  prop.className "text-lg font-medium mb-4"
                  prop.text "Notifications"
                ]
                Html.ul [
                  prop.className "space-y-3"
                  prop.children (
                    model.Notifications
                    |> List.mapi (fun idx notification ->
                      Html.li [
                        prop.className "flex items-center justify-between rounded-lg bg-slate-800/60 px-4 py-3"
                        prop.children [
                          Html.span [ prop.className "text-sm"; prop.text notification ]
                          Html.button [
                            prop.className "text-xs uppercase tracking-wide text-slate-400 hover:text-white"
                            prop.text "Dismiss"
                            prop.onClick (fun _ -> dispatch (ClearNotification idx))
                          ]
                        ]
                      ])
                    |> List.toArray)
                ]
              ]
            ]
          ]
        ]
      ]
    ]

  let run () =
#if FABLE_COMPILER
    Program.mkProgram init update view
    |> Program.withReactSynchronous "app"
    |> Program.run
#else
    ()
#endif
