namespace FanRide

open System

[<CLIMutable>]
type CosmosEndpointConfig =
  { Dev: string
    Test: string
    Prod: string }

[<CLIMutable>]
type CosmosKeyConfig =
  { Dev: string
    Test: string
    Prod: string }

[<CLIMutable>]
type CosmosContainersConfig =
  { Es: string
    RmMatchState: string
    RmTesHistory: string
    RmLeaderboard: string
    Leases: string }

[<CLIMutable>]
type CosmosConfig =
  { AccountEndpoint: CosmosEndpointConfig
    Key: CosmosKeyConfig
    Database: string
    Containers: CosmosContainersConfig
    ConsistencyLevel: string
    UseSameType: bool }

[<RequireQualifiedAccess>]
type FanRideEnvironment =
  | Development
  | Test
  | Production

type CosmosEnvironment =
  { Environment: FanRideEnvironment
    Endpoint: string
    Key: string }

module CosmosConfiguration =
  let private resolveEnvironmentName = function
    | null | "" -> FanRideEnvironment.Development
    | name when name.Equals("production", StringComparison.OrdinalIgnoreCase) -> FanRideEnvironment.Production
    | name when name.Equals("prod", StringComparison.OrdinalIgnoreCase) -> FanRideEnvironment.Production
    | name when name.Equals("test", StringComparison.OrdinalIgnoreCase) -> FanRideEnvironment.Test
    | _ -> FanRideEnvironment.Development

  let getEnvironment (cfg: CosmosConfig) (environmentName: string) =
    let env = resolveEnvironmentName environmentName
    let endpoint =
      match env with
      | FanRideEnvironment.Development -> cfg.AccountEndpoint.Dev
      | FanRideEnvironment.Test -> cfg.AccountEndpoint.Test
      | FanRideEnvironment.Production -> cfg.AccountEndpoint.Prod
    let keyValue =
      match env with
      | FanRideEnvironment.Development -> cfg.Key.Dev
      | FanRideEnvironment.Test -> cfg.Key.Test
      | FanRideEnvironment.Production -> cfg.Key.Prod

    let resolvedKey =
      if keyValue.StartsWith("env:", StringComparison.OrdinalIgnoreCase) then
        let envVar = keyValue.Substring("env:".Length)
        let value = Environment.GetEnvironmentVariable(envVar)
        if String.IsNullOrWhiteSpace(value) then
          invalidOp ($"Environment variable '{envVar}' referenced by Cosmos key was not found or empty")
        else
          value
      else
        keyValue

    { Environment = env
      Endpoint = endpoint
      Key = resolvedKey }

  let ensureStrongConsistency (cfg: CosmosConfig) =
    if not (cfg.ConsistencyLevel.Equals("Strong", StringComparison.OrdinalIgnoreCase)) then
      invalidOp "FanRide requires Strong consistency for Cosmos DB." 

