module NBomber.FSharp.Builders.Example

open System.Net.WebSockets
open System
open System.Threading.Tasks
open FSharp.Control.Tasks.V2.ContextInsensitive
open Serilog
open NBomber
open NBomber.Contracts
open NBomber.FSharp


let ms = milliseconds

let delay (time: TimeSpan) (logger: ILogger) =
  task {
    logger.Information("start wait {time}", time)
    do! Task.Delay time
    logger.Information("end wait {time}", time)
  }

let exampleScenario simulations =
  scenario "test delays" {
    warmUp (seconds 5)
    load simulations
    steps [
      step "wait 100" {
        execute (fun ctx -> delay (ms 100) ctx.Logger)
      }
      step "wait 10" {
        execute (fun ctx -> delay (ms 10) ctx.Logger)
      }
      step "wait 0" {
        execute (fun _ -> Task.CompletedTask)
      }
    ]
  }

let test() =
    let data = FeedData.fromJson<int> "jsonPath" |> Feed.createCircular "none"
    let conns =
        let url = ""
        ConnectionPoolArgs.create(
          name = "websockets pool",
          openConnection = (fun (_nr,cancel) -> task {
                let ws = new ClientWebSocket()
                do! ws.ConnectAsync(Uri url, cancel)
                return ws
              }),
          closeConnection = (fun (ws : ClientWebSocket, cancel ) -> task {
                do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancel)
              }))

    let steps =
        [ step "task step" {
            feed data
            connectionPool conns
            execute (fun _ -> Response.Ok() |> Task.FromResult)
            doNotTrack
          }
          step "async step" {
            doNotTrack
            execute (fun _ -> async { return Response.Ok() } )
          }

          step "wait 100" {
            execute (fun ctx -> delay (ms 100) ctx.Logger)
          }
          step "wait 10" {
            execute (fun ctx -> delay (ms 10) ctx.Logger)
          }
          step "wait 0" {
            execute (fun _ -> Task.CompletedTask)
          }

          step "wait pause 100" {
            pause 100
          }

        //   httpStep "get homepage" {
        //     request "GET" "https://nbomber.com"
        //   }
        //   httpStep "GET homepage" {
        //     GET "nbomber.com"
        //   }
        //   httpStep "POST" {
        //     POST "http://nbomber.com" "{}"
        //   }
        ]

    steps
