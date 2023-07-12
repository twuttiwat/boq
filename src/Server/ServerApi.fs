module ServerApi

open Fable.Remoting.Server
open Fable.Remoting.Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Saturn

open Shared


module Storage =
    let todos = ResizeArray()

    let addTodo (todo: Todo) =
        if Todo.isValid todo.Description then
            todos.Add todo
            Ok()
        else
            Error "Invalid todo"

    do
        addTodo (Todo.create "Create new SAFE project")
        |> ignore

        addTodo (Todo.create "Write your app") |> ignore
        addTodo (Todo.create "Ship it !!!") |> ignore

let todosApi (logger: ILogger) =
    { getTodos =
        fun () ->
            async {
                logger.LogInformation("Executing {Function}", "getTodos")
                return Storage.todos |> List.ofSeq
            }
      addTodo =
        fun todo ->
            async {
                logger.LogInformation("Executing {Function} with {Todo}", "addTodo", todo)
                return
                    match Storage.addTodo todo with
                    | Ok () -> todo
                    | Error e -> failwith e
            } }

type TodosApi(logger: ILogger<ITodosApi>, config: IConfiguration) =
    member this.Build() : ITodosApi = todosApi logger