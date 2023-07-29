module Env

open Serilog.Events
open Serilog.Sinks.SystemConsole.Themes

/// A dummy interface that will tell us where this assembly is built
type IAssemblyTag =
    interface
    end

open System
open System.IO
open System.Linq
open System.Collections.Generic
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Configuration
open Serilog

let isDevelopment =
#if DEBUG
    true
#else
    false
#endif

/// Recursively tries to find the parent of a file starting from a directory
let rec findParent (directory: string) (fileToFind: string) =
    let path =
        if Directory.Exists(directory) then directory else Directory.GetParent(directory).FullName

    let files = Directory.GetFiles(path)
    if files.Any(fun file -> Path.GetFileName(file).ToLower() = fileToFind.ToLower())
    then path
    else findParent (DirectoryInfo(path).Parent.FullName) fileToFind

let solutionRoot() = findParent __SOURCE_DIRECTORY__ "boq.sln"

let serverRoot() = findParent __SOURCE_DIRECTORY__ "Server.fs"

let serverPath() =
    if isDevelopment then
        serverRoot()
    else
        let serverDll = typeof<IAssemblyTag>.Assembly.Location
        Directory.GetParent(serverDll).FullName

/// Returns enviroment variables as a dictionary
let environmentVars() =
    let variables = Dictionary<string, string>()
    let userVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User)
    let processVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process)
    for pair in userVariables do
        let variable = unbox<Collections.DictionaryEntry> pair
        let key = unbox<string> variable.Key
        let value = unbox<string> variable.Value
        if not (variables.ContainsKey(key)) && key <> "PATH" then variables.Add(key, value)
    for pair in processVariables do
        let variable = unbox<Collections.DictionaryEntry> pair
        let key = unbox<string> variable.Key
        let value = unbox<string> variable.Value
        if not (variables.ContainsKey(key)) && key <> "PATH" then variables.Add(key, value)
    variables

let readLocalConfig configJsonPath (variables: Dictionary<string, string>) =
    let configJson = JObject.Parse(File.ReadAllText(configJsonPath))
    for property in configJson.Properties() do
        if not (variables.ContainsKey(property.Name)) then
            variables.Add(property.Name, property.Value.ToObject<string>())
    variables

/// Reads variables in a file called config.json inside {solutionRoot}/server
/// this file is for development configuration and should be git-ignored as it might contain sensitive data.
/// When in production, this function tries to read the file contents from the file next to the executing assembly
let localConfig() =
    let variables = Dictionary<string, string>()
    if isDevelopment then
        let configJsonPath = Path.Combine(solutionRoot(), "server", "config.json")
        if File.Exists configJsonPath then
            printfn "Found config.json file, loading variables"
            readLocalConfig configJsonPath variables
        else
            printfn "Configuration file config.json file was not found inside ./server"
            variables
    else
        let serverDll = typeof<IAssemblyTag>.Assembly.Location
        let serverPath = Directory.GetParent(serverDll).FullName
        let configJsonPath = Path.Combine(serverPath, "config.json")
        if File.Exists configJsonPath
        then readLocalConfig configJsonPath variables
        else variables

/// Combines variables from environment and the local config.json file by overriding values from the latter into the former
let combinedVariables() =
    let environment = environmentVars()
    let localConfig = localConfig()
    // override environment from local config
    for localPair in localConfig do
        if environment.ContainsKey(localPair.Key) then
            ignore (environment.Remove(localPair.Key))
            environment.Add(localPair.Key, localPair.Value)
        else
            environment.Add(localPair.Key, localPair.Value)
    environment

/// Configures combines variables (environment + local) into the web host builder
let configureVariables (builder: IHostBuilder) =
    builder.ConfigureAppConfiguration(fun context configBuilder ->
        let variables = combinedVariables()
        configBuilder.AddInMemoryCollection(variables) |> ignore)

let setLogCommon (configureLogger: LoggerConfiguration) =
    configureLogger
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console(
            theme= AnsiConsoleTheme.Literate,
            outputTemplate= "[{Timestamp:HH:mm:ss} {Level:u3}] <{SourceContext}>: {Message:lj}{NewLine}{Exception}",
            applyThemeToRedirectedOutput= true
        )

let aspnetcoreEnvironmentDev =
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") = Environments.Development

let setLogAzure blobCS (configureLogger: LoggerConfiguration) =
    configureLogger
        .WriteTo.AzureBlobStorage(
            connectionString= blobCS
            )

let configureHost (builder: IHostBuilder) =
    builder
        .UseSerilog(
            fun context configureLogger ->
                let blobCS = context.Configuration.GetValue<string>("BLOB_CONNECTION_STRING")

                configureLogger
                |> setLogCommon
                |> fun logCommon ->
                    if aspnetcoreEnvironmentDev then logCommon else (setLogAzure blobCS logCommon)
                |> ignore
        )
    |> configureVariables