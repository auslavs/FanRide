namespace FanRide

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Azure.Cosmos
open Microsoft.Azure.SignalR
open OpenTelemetry.Logs
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Newtonsoft.Json.Linq

[<CLIMutable>]
type ScoreDto =
  { Home: int
    Away: int }

[<CLIMutable>]
type SnapshotDto =
  { Score: ScoreDto
    Quarter: int
    Clock: string }

[<CLIMutable>]
type AppendEventDto =
  { Id: string
    Kind: string
    Payload: JsonElement }

[<CLIMutable>]
type AppendEventsRequestDto =
  { ExpectedVersion: int
    ExpectedEtag: string
    Snapshot: SnapshotDto
    Events: AppendEventDto list }

module private ProgramHelpers =
  let toMatchState (snapshot: SnapshotDto) =
    { Score = { Home = snapshot.Score.Home; Away = snapshot.Score.Away }
      Quarter = snapshot.Quarter
      Clock = snapshot.Clock }

  let private tryDeserialize<'T> (element: JsonElement) =
    try
      ValueSome(element.Deserialize<'T>())
    with _ -> ValueNone

  let toDomainEvent (dto: AppendEventDto) : NewEvent =
    let payload =
      if dto.Kind.Equals("MatchStateUpdated", StringComparison.OrdinalIgnoreCase) then
        match tryDeserialize<MatchState> dto.Payload with
        | ValueSome state -> EventPayload.MatchStateUpdated state
        | ValueNone -> EventPayload.GenericPayload dto.Payload
      elif dto.Kind.Equals("TrainerMetricsCaptured", StringComparison.OrdinalIgnoreCase) then
        match tryDeserialize<TrainerMetrics> dto.Payload with
        | ValueSome metrics -> EventPayload.TrainerMetricsCaptured metrics
        | ValueNone -> EventPayload.GenericPayload dto.Payload
      else
        EventPayload.GenericPayload dto.Payload
    { Id = dto.Id
      Kind = dto.Kind
      Payload = payload }

open ProgramHelpers

module Program =
  [<EntryPoint>]
  let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services.Configure<CosmosConfig>(builder.Configuration.GetSection("cosmos")) |> ignore
    builder.Services.AddSingleton<CosmosConfig>(fun sp -> sp.GetRequiredService<IOptions<CosmosConfig>>().Value) |> ignore

    builder.Services.AddApplicationInsightsTelemetry() |> ignore

    builder.Services
      .AddOpenTelemetry()
      .ConfigureResource(fun resource ->
        resource.AddService("fanride-backend") |> ignore)
      .WithMetrics(fun metrics ->
        metrics
          .AddAspNetCoreInstrumentation()
          .AddHttpClientInstrumentation()
          .AddRuntimeInstrumentation()
        |> ignore)
      .WithTracing(fun tracing ->
        tracing
          .AddAspNetCoreInstrumentation(fun options -> options.RecordException <- true)
          .AddHttpClientInstrumentation()
        |> ignore)
      |> ignore

    builder.Logging.AddOpenTelemetry(fun logging ->
      logging.IncludeFormattedMessage <- true
      logging.ParseStateValues <- true
      logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("fanride-backend")) |> ignore
    ) |> ignore

    builder.Services.AddCors(fun options ->
      options.AddPolicy("frontend", fun policy ->
        policy
          .AllowAnyHeader()
          .AllowAnyMethod()
          .AllowCredentials()
          .SetIsOriginAllowed(fun _ -> true)
        |> ignore)
    ) |> ignore

    builder.Services.AddHealthChecks() |> ignore

    builder.Services.AddSingleton<CosmosClient>(fun sp ->
      let cfg = sp.GetRequiredService<CosmosConfig>()
      CosmosConfiguration.ensureStrongConsistency cfg
      let env = CosmosConfiguration.getEnvironment cfg builder.Environment.EnvironmentName
      let options = CosmosClientOptions()
      options.ConsistencyLevel <- ConsistencyLevel.Strong
      options.SerializerOptions <- CosmosSerializationOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)
      new CosmosClient(env.Endpoint, env.Key, options)
    ) |> ignore

    let signalRBuilder = builder.Services.AddSignalR()
    match builder.Configuration.GetSection("Azure:SignalR:ConnectionString").Value with
    | null | "" -> ()
    | _ -> signalRBuilder.AddAzureSignalR() |> ignore
    builder.Services.AddHostedService<ChangeFeedProjector>() |> ignore

    let app = builder.Build()

    app.UseCors("frontend")

    app.MapGet("/", Func<IResult>(fun () -> Results.Ok("FanRide backend running"))) |> ignore

    app.MapGet("/api/matches/{streamId}", Func<string, CosmosClient, CosmosConfig, Task<IResult>>(fun streamId client cfg ->
      task {
        try
          let container = client.GetContainer(cfg.Database, cfg.Containers.Es)
          let! response = container.ReadItemAsync<JObject>($"snap-{streamId}", PartitionKey(streamId))
          let stateToken = response.Resource.["state"]
          if isNull stateToken then
            return Results.NotFound()
          else
            let state = stateToken.ToObject<MatchState>()
            return Results.Ok(state)
        with :? CosmosException as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
          return Results.NotFound()
      }
    )) |> ignore

    app.MapPost("/api/matches/{streamId}/events",
      Func<string, AppendEventsRequestDto, CosmosClient, CosmosConfig, IHubContext<MatchHub>, Task<IResult>>
        (fun streamId dto client cfg hub ->
          task {
            let expectedEtag =
              if String.IsNullOrWhiteSpace(dto.ExpectedEtag) then None else Some dto.ExpectedEtag

            let domainEvents = dto.Events |> List.map toDomainEvent
            let snapshotState = dto.Snapshot |> toMatchState
            let appendRequest =
              { StreamId = streamId
                ExpectedVersion = dto.ExpectedVersion
                ExpectedEtag = expectedEtag
                Snapshot = EventStore.serializeSnapshot snapshotState
                Events = domainEvents }

            let! result = EventStore.appendWithSnapshot client cfg.Database cfg.Containers.Es appendRequest
            match result with
            | Ok () ->
                match domainEvents |> List.tryFind (fun ev -> ev.Kind.Equals("MatchStateUpdated", StringComparison.OrdinalIgnoreCase)) with
                | Some evt ->
                    match evt.Payload with
                    | EventPayload.MatchStateUpdated state ->
                        let hubState =
                          { ScoreHome = state.Score.Home
                            ScoreAway = state.Score.Away
                            Quarter = state.Quarter
                            Clock = state.Clock }
                        do! hub.Clients.All.SendAsync("matchState", hubState)
                    | _ -> ()
                | None -> ()
                return Results.Accepted()
            | Error err ->
                return Results.Problem(err, statusCode = 412)
          }
        )
    ) |> ignore

    app.MapHub<MatchHub>("/hub/match") |> ignore

    app.MapHealthChecks("/health") |> ignore

    app.Run()
    0

