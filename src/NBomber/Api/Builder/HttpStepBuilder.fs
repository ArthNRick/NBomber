namespace NBomber.Builders

open System
open System.Diagnostics
open System.Threading.Tasks
open System.Net.Http
open FSharp.Control.Tasks.V2.ContextInsensitive
open NBomber.Contracts
open NBomber.FSharp
open Microsoft.Extensions.DependencyInjection
open Serilog


type HttpStepIncomplete =
    { HttpClientFactory: unit -> HttpClient
      CompletionOption: HttpCompletionOption
      Checks: (HttpResponseMessage -> Task<Response>) list
    }

type HttpStepCreateRequest<'a, 'b> =
    { CreateRequest: IStepContext<'a, 'b> -> Task<HttpRequestMessage>
      CompletionOption: HttpCompletionOption
      HttpClientFactory: unit -> HttpClient
      Checks: (HttpResponseMessage -> Task<Response>) list
    }

type HttpStepRequest =
    { Url: Uri
      Version: Version
      Method: HttpMethod
      Headers: Map<string, string>
      Content: HttpContent
      CompletionOption: HttpCompletionOption
      HttpClientFactory: unit -> HttpClient
      Checks: (HttpResponseMessage -> Task<Response>) list
    }

type HttpStepBuilder(name: string) =
    let defaultClientFactory =
        ServiceCollection().AddHttpClient().BuildServiceProvider().GetService<IHttpClientFactory>()

    let logRequest (logger: ILogger) (req: HttpRequestMessage) =
        if logger.IsEnabled Events.LogEventLevel.Verbose then
            let body = if isNull req.Content then "" else req.Content.ReadAsStringAsync().Result
            logger.Verbose("\n [REQUEST]: \n {0} \n [REQ_BODY] \n {1} \n", req.ToString(), body)

    let logResponse (logger: ILogger) (res: HttpResponseMessage) =
        if logger.IsEnabled Events.LogEventLevel.Verbose then
            let body = if isNull res.Content then "" else res.Content.ReadAsStringAsync().Result
            logger.Verbose("\n [RESPONSE]: \n {0} \n [RES_BODY] \n {1} \n", res.ToString(), body)

    let handleResponse (response: HttpResponseMessage) latencyMs =
        if response.IsSuccessStatusCode then
            let headersSize = response.Headers.ToString().Length
            let bodySize =
                if response.Content.Headers.ContentLength.HasValue
                then int response.Content.Headers.ContentLength.Value
                else 0
            Response.Ok(sizeBytes = headersSize + bodySize, latencyMs = int latencyMs)
        else
            Response.Fail(response.ReasonPhrase)
    let empty =
        { HttpClientFactory = fun () -> defaultClientFactory.CreateClient name
          CompletionOption = HttpCompletionOption.ResponseHeadersRead
          Checks = []
        }

    member _.Zero() = empty
    member _.Yield _ = empty

    [<CustomOperation "request">]
    member _.Request(state: HttpStepIncomplete, method: string, url: string) =
        { Url = Uri url
          Version = Version(1, 1)
          Method = HttpMethod method
          Headers = Map.empty
          Content = Unchecked.defaultof<HttpContent>
          CompletionOption = HttpCompletionOption.ResponseHeadersRead
          HttpClientFactory = state.HttpClientFactory
          Checks = []
        }

    [<CustomOperation "GET">]
    member __.GetRequest(state: HttpStepIncomplete, url: string) =
        __.Request(state, "GET", url)

    [<CustomOperation "POST">]
    member __.PostRequest(state: HttpStepIncomplete, url: string, content: string) =
        let req = __.Request(state, "Post", url)
        { req with Content = new StringContent(content) }
    member __.PostRequest(state: HttpStepIncomplete, url: string, content: #HttpContent) =
        let req = __.Request(state, "Post", url)
        { req with Content = content }
    // member __.PostRequest(state : HttpStepIncomplete, url: string, content: 'a) =
    //     let req = __.Request(state, "Post", url)
    //     { req with Content = new StringContent(content.ToJson()) }

    [<CustomOperation "version">]
    member _.Version(state: HttpStepRequest, version: string) = { state with Version = Version version }

    [<CustomOperation "headers">]
    member _.Headers(state: HttpStepRequest, headers) = { state with Headers = headers }

    [<CustomOperation "content">]
    member _.Content(state: HttpStepRequest, content) = { state with Content = content }

    [<CustomOperation "createRequest">]
    member inline _.CreateRequest(state: HttpStepIncomplete, createRequest) =
        { CreateRequest = createRequest
          CompletionOption = HttpCompletionOption.ResponseHeadersRead
          HttpClientFactory = state.HttpClientFactory
          Checks = []
        }

    [<CustomOperation "httpClient">]
    member _.HttpClient(state: HttpStepRequest, httpClient) =
        { state with HttpClientFactory = fun () -> httpClient }
    member _.HttpClient(state: HttpStepCreateRequest<_, _>, httpClient) =
        { state with HttpClientFactory = httpClient }

    [<CustomOperation "clientFactory">]
    member _.HttpClientFactory(state: HttpStepRequest, httpClientFactory) =
        { state with HttpClientFactory = httpClientFactory }
    member _.HttpClientFactory(state: HttpStepCreateRequest<_, _>, httpClientFactory) =
        { state with HttpClientFactory = httpClientFactory }
    member _.HttpClientFactory(state: HttpStepRequest, httpClientFactory: IHttpClientFactory) =
        { state with HttpClientFactory = httpClientFactory.CreateClient }
    member _.HttpClientFactory(state: HttpStepCreateRequest<_, _>, httpClientFactory: IHttpClientFactory) =
        { state with HttpClientFactory = httpClientFactory.CreateClient }

    [<CustomOperation "check">]
    member _.WithCheck(state: HttpStepRequest, check) =
        { state with Checks = check :: state.Checks }
    member _.WithCheck(state: HttpStepCreateRequest<_, _>, check) =
        { state with Checks = check :: state.Checks }

    member _.Run(state: HttpStepRequest) =
        let action (ctx: IStepContext<_, _>) =
            task {
                let request = new HttpRequestMessage()
                request.Method <- state.Method
                request.RequestUri <- state.Url
                request.Version <- state.Version
                request.Content <- state.Content

                state.Headers
                |> Map.iter (fun name value ->
                    request.Headers.TryAddWithoutValidation(name, value)
                    |> ignore)

                logRequest ctx.Logger request

                let sw = Stopwatch.StartNew()

                let! response =
                    state.HttpClientFactory().SendAsync(request, state.CompletionOption, ctx.CancellationToken)

                sw.Stop()
                let latencyMs = sw.ElapsedMilliseconds
                logResponse ctx.Logger response

                return handleResponse response latencyMs
            }

        Step.create (name, execute = action)

    member _.Run(state: HttpStepCreateRequest<_, _>) =
        let action (ctx: IStepContext<_, _>) =
            task {
                let! request = state.CreateRequest ctx
                logRequest ctx.Logger request

                let sw = Stopwatch.StartNew()

                let! response =
                    state.HttpClientFactory().SendAsync(request, state.CompletionOption, ctx.CancellationToken)

                sw.Stop()
                let latencyMs = sw.ElapsedMilliseconds
                logResponse ctx.Logger response

                return handleResponse response latencyMs
            }

        Step.create (name, execute = action)
