module Server

open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Saturn

open ServerApi
open Shared

let webApp =
    Remoting.createApi ()
    |> Remoting.fromContext (fun (ctx: HttpContext) -> ctx.GetService<TodosApi>().Build())
    |> Remoting.withRouteBuilder Route.builder
    |> Remoting.buildHttpHandler

let serviceConfig (services: IServiceCollection) =
    services
        .AddSingleton<TodosApi>()
        .AddLogging()

let application =
    application {
        use_router webApp
        memory_cache
        use_static "public"
        use_gzip
        service_config serviceConfig
        host_config Env.configureHost
    }

run application