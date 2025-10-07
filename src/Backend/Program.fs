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

[<CLIMutable>]
type AflMatchSnapshotResponseDto =
  { StreamId: string
    AggregateVersion: int
    Etag: string
    State: SnapshotDto }

module private ProgramHelpers =
  let toMatchState (snapshot: SnapshotDto) =
    { Score = { Home = snapshot.Score.Home; Away = snapshot.Score.Away }
      Quarter = snapshot.Quarter
      Clock = snapshot.Clock }

  let toSnapshotDto (state: MatchState) : SnapshotDto =
    { Score = { Home = state.Score.Home; Away = state.Score.Away }
      Quarter = state.Quarter
      Clock = state.Clock }

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

  let private toHubMatchState (state: MatchState) : MatchStateDto =
    { ScoreHome = state.Score.Home
      ScoreAway = state.Score.Away
      Quarter = state.Quarter
      Clock = state.Clock }

  let toSnapshotEnvelope streamId version etag (state: MatchState) : AflMatchSnapshotResponseDto =
    { StreamId = streamId
      AggregateVersion = version
      Etag = etag
      State = toSnapshotDto state }

  let tryReadSnapshot (streamId: string) (client: CosmosClient) (cfg: CosmosConfig) =
    task {
      try
        let container = client.GetContainer(cfg.Database, cfg.Containers.Es)
        let! response = container.ReadItemAsync<JObject>($"snap-{streamId}", PartitionKey(streamId))
        match response.Resource.["state"] with
        | null -> return ValueNone
        | stateToken ->
            let state = stateToken.ToObject<MatchState>()
            let aggVersionNullable = response.Resource.Value<Nullable<int>>("aggVersion")
            let aggVersion = if aggVersionNullable.HasValue then aggVersionNullable.Value else 0
            return ValueSome(state, aggVersion, response.ETag)
      with :? CosmosException as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
        return ValueNone
    }

  let private broadcastMatchState (hub: IHubContext<MatchHub>) (events: NewEvent list) =
    task {
      match events |> List.tryFind (fun ev -> ev.Kind.Equals("MatchStateUpdated", StringComparison.OrdinalIgnoreCase)) with
      | Some evt ->
          match evt.Payload with
          | EventPayload.MatchStateUpdated state ->
              let hubState = toHubMatchState state
              do! hub.Clients.All.SendAsync("matchState", hubState)
          | _ -> ()
      | None -> ()
    }

  let handleAppendEvents returnSnapshot streamId (dto: AppendEventsRequestDto) (client: CosmosClient) (cfg: CosmosConfig) (hub: IHubContext<MatchHub>) =
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
          do! broadcastMatchState hub domainEvents
          if returnSnapshot then
            let! snapshot = tryReadSnapshot streamId client cfg
            match snapshot with
            | ValueSome(state, version, etag) ->
                let envelope = toSnapshotEnvelope streamId version etag state
                return Results.Ok(envelope)
            | ValueNone ->
                return Results.Accepted()
          else
            return Results.Accepted()
      | Error err ->
          return Results.Problem(err, statusCode = 412)
    }

open ProgramHelpers

module Program =
  [<EntryPoint>]
  let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services.Configure<CosmosConfig>(builder.Configuration.GetSection("cosmos")) |> ignore
    builder.Services.AddSingleton<CosmosConfig>(fun sp -> sp.GetRequiredService<IOptions<CosmosConfig>>().Value) |> ignore
    builder.Services.Configure<ChangeFeedConfig>(builder.Configuration.GetSection("changeFeed")) |> ignore
    builder.Services.AddSingleton<ChangeFeedConfig>(fun sp -> sp.GetRequiredService<IOptions<ChangeFeedConfig>>().Value) |> ignore
    builder.Services.Configure<AflFeedConfig>(builder.Configuration.GetSection("aflFeed")) |> ignore
    builder.Services.AddSingleton<AflFeedConfig>(fun sp -> sp.GetRequiredService<IOptions<AflFeedConfig>>().Value) |> ignore

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
      CosmosConfiguration.ensureDataParity cfg
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
    builder.Services.AddSingleton<IReadModelService, ReadModelService>() |> ignore
    builder.Services.AddHostedService<ChangeFeedProjector>() |> ignore
    builder.Services.AddHttpClient<IAflFeedClient, AflFeedClient>() |> ignore
    builder.Services.AddHostedService<AflFeedIngestionService>() |> ignore

    let app = builder.Build()

    app.UseCors("frontend") |> ignore

    app.MapGet("/", Func<IResult>(fun () -> Results.Ok("FanRide backend running"))) |> ignore

    app.MapGet("/api/matches/{streamId}", Func<string, CosmosClient, CosmosConfig, Task<IResult>>(fun streamId client cfg ->
      task {
        let! snapshot = tryReadSnapshot streamId client cfg
        match snapshot with
        | ValueSome(state, _, _) -> return Results.Ok(state)
        | ValueNone -> return Results.NotFound()
      }
    )) |> ignore

    app.MapGet("/api/afl/matches/{streamId}", Func<string, CosmosClient, CosmosConfig, Task<IResult>>(fun streamId client cfg ->
      task {
        let! snapshot = tryReadSnapshot streamId client cfg
        match snapshot with
        | ValueSome(state, version, etag) ->
            let envelope = toSnapshotEnvelope streamId version etag state
            return Results.Ok(envelope)
        | ValueNone -> return Results.NotFound()
      }
    )) |> ignore

    app.MapGet(
      "/api/readmodels/tes/{streamId}",
      Func<string, IReadModelService, Task<IResult>>(fun streamId readModels ->
        task {
          let! momentum = readModels.GetTesMomentum(streamId)
          match momentum with
          | Some dto -> return Results.Ok(dto)
          | None -> return Results.NotFound()
        }
      )
    )
    |> ignore

    app.MapGet(
      "/api/readmodels/leaderboard",
      Func<IReadModelService, Task<IResult>>(fun readModels ->
        task {
          let! dto = readModels.GetLeaderboard()
          return Results.Ok(dto)
        }
      )
    )
    |> ignore

    let appendRoute returnSnapshot =
      Func<string, AppendEventsRequestDto, CosmosClient, CosmosConfig, IHubContext<MatchHub>, Task<IResult>>
        (fun streamId dto client cfg hub -> handleAppendEvents returnSnapshot streamId dto client cfg hub)

    app.MapPost("/api/matches/{streamId}/events", appendRoute false) |> ignore

    app.MapPost("/api/afl/matches/{streamId}/apply", appendRoute true) |> ignore

    app.MapHub<MatchHub>("/hub/match") |> ignore

    app.MapHealthChecks("/health") |> ignore

    app.Run()
    0

