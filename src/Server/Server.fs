module Server

open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Saturn

open PriceScraper
open ServerApi
open Shared

let webApp =
    Remoting.createApi ()
    |> Remoting.fromContext (fun (ctx: HttpContext) -> ctx.GetService<TodosApi>().Build())
    |> Remoting.withRouteBuilder Route.builder
    |> Remoting.buildHttpHandler

let serviceConfig (services: IServiceCollection) =
    services
        .AddSingleton<PriceScraper>()
        .AddSingleton<TodosApi>()

let configureApp (app: IApplicationBuilder) =
    app.ApplicationServices.GetRequiredService<PriceScraper>().Run()
    app

let application =
    application {
        use_router webApp
        memory_cache
        use_static "public"
        use_gzip
        service_config serviceConfig
        app_config configureApp
        host_config Env.configureHost
    }

run application