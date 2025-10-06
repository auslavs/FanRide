namespace FanRide

open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Azure.Cosmos

[<CLIMutable>]
type AppendRequest =
  { StreamId: string
    ExpectedVersion: int
    ExpectedEtag: string option
    Snapshot: JsonElement
    Events: NewEvent list }

module EventStore =
  let appendWithSnapshot (client: CosmosClient) (databaseName: string) (containerName: string) (request: AppendRequest) =
    task {
      let container = client.GetContainer(databaseName, containerName)
      let pk = PartitionKey(request.StreamId)
      let batch = container.CreateTransactionalBatch(pk)

      match request.ExpectedEtag with
      | Some etag ->
          batch.ReplaceItem(
            id = $"snap-{request.StreamId}",
            item = {| ``type`` = "snapshot-guard" |},
            requestOptions = TransactionalBatchItemRequestOptions(IfMatchEtag = etag)
          )
          |> ignore
      | None ->
          batch.CreateItem(
            {| id = $"snap-{request.StreamId}"
               ``type`` = "snapshot-guard"
               streamId = request.StreamId |}
          )
          |> ignore

      for i, ev in request.Events |> List.indexed do
        let cosmosEvent =
          {| id = ev.Id
             ``type`` = "event"
             streamId = request.StreamId
             seq = request.ExpectedVersion + i + 1
             kind = ev.Kind
             data = EventPayload.toDocument ev.Payload
             ts = DateTime.UtcNow |}
        batch.CreateItem(cosmosEvent) |> ignore

      let snapshot =
        {| id = $"snap-{request.StreamId}"
           ``type`` = "snapshot"
           streamId = request.StreamId
           aggVersion = request.ExpectedVersion + request.Events.Length
           state = request.Snapshot
           updatedAt = DateTime.UtcNow |}
      batch.UpsertItem(snapshot) |> ignore

      for message in Outbox.fromEvents request.Events do
        let doc =
          {| id = message.Id
             ``type`` = "outbox"
             streamId = request.StreamId
             kind = message.Kind
             payload = message.Payload
             ts = DateTime.UtcNow |}
        batch.CreateItem(doc) |> ignore

      let! response = batch.ExecuteAsync()
      if not response.IsSuccessStatusCode then
        return Error(sprintf "Batch failed: %A" response.StatusCode)
      else
        return Ok()
    }

  let inline serializeSnapshot<'T> (value: 'T) =
    JsonSerializer.SerializeToElement(value)

