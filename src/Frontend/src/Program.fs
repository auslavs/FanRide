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

  let private registerHandlers (conn: obj) (dispatch: Msg -> unit) =
    conn?on("matchState", fun payload ->
      payload |> toMatchState |> MatchStateArrived |> dispatch)
    conn?on("metrics", fun payload ->
      payload |> toMetrics |> MetricsTick |> dispatch)
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
#else
  let private sendMetrics (_: TrainerMetrics) = async { return Ok() }
  let private startConnection (_: Msg -> unit) = ()
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
    | ConnectionFailed _ ->
#if FABLE_COMPILER
        connection <- None
#endif
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
          prop.className "px-6 py-10 grid gap-6 md:grid-cols-2"
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
              prop.className "md:col-span-2 rounded-xl border border-slate-800 bg-slate-900/70 p-6"
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
