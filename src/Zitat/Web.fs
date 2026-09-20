namespace Zitat

open System
open System.Globalization
open System.Text.Json
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open System.Text.Json.Serialization

module Web =
    let jsonOptions =
        let options = JsonSerializerOptions(JsonSerializerDefaults.Web)
        options.Converters.Add(JsonFSharpConverter())
        options

    let private value name (context: HttpContext) =
        let text = context.Request.Query[name].ToString()
        if String.IsNullOrWhiteSpace text then None else Some text

    let private integer name context =
        value name context
        |> Option.bind (fun text ->
            match Int32.TryParse text with
            | true, parsed -> Some parsed
            | _ -> None)

    let private int64 name context =
        value name context
        |> Option.bind (fun text ->
            match Int64.TryParse text with
            | true, parsed -> Some parsed
            | _ -> None)

    // A timestamp without an offset means UTC, matching stored receive times.
    let private timestamp name context =
        value name context
        |> Option.bind (fun text ->
            match DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            ) with
            | true, parsed -> Some parsed
            | _ -> None)

    let private relativeTime (value: string) =
        if value.Length < 2 then
            None
        else
            let number = value.Substring(0, value.Length - 1)
            match Double.TryParse number, value[value.Length - 1] with
            | (true, amount), 'm' -> Some(DateTimeOffset.UtcNow.AddMinutes(-amount))
            | (true, amount), 'h' -> Some(DateTimeOffset.UtcNow.AddHours(-amount))
            | (true, amount), 'd' -> Some(DateTimeOffset.UtcNow.AddDays(-amount))
            | _ -> None

    let query (context: HttpContext) =
        let textQuery =
            value "q" context
            |> Option.map (fun text -> Query.parseText text Query.empty)
            |> Option.defaultValue Query.empty

        let since =
            match value "range" context with
            | Some range -> relativeTime range
            | None -> timestamp "since" context

        { textQuery with
            Hostname = value "host" context |> Option.orElse textQuery.Hostname
            Application = value "app" context |> Option.orElse textQuery.Application
            SourceAddress =
                value "source" context |> Option.orElse textQuery.SourceAddress
            Facility = integer "facility" context |> Option.orElse textQuery.Facility
            Severity =
                value "severity" context
                |> Option.bind Query.numericFilter
                |> Option.orElse textQuery.Severity
            Since = since
            Until = timestamp "until" context
            BeforeId = int64 "before" context
            Limit = integer "limit" context |> Option.defaultValue 200 }

    let private logs (database: Database) (context: HttpContext) =
        let parsed = query context
        let items = database.Search parsed
        let limit = Math.Clamp(parsed.Limit, 1, 1000)
        let next =
            if items.Length = limit then items |> List.tryLast |> Option.map _.Id
            else None
        Response.ofJsonOptions jsonOptions {| items = items; nextBeforeId = next |} context

    let private status (database: Database) (metrics: IngestMetrics) context =
        Response.ofJsonOptions jsonOptions
            {| status = "ok"
               logCount = database.Count
               storageBytes = database.SizeBytes
               received = metrics.Received
               stored = metrics.Stored
               malformed = metrics.Malformed
               floodDropped = metrics.FloodDropped
               queueDropped = metrics.QueueDropped
               storageFailed = metrics.StorageFailed |}
            context

    let private stream (live: LiveHub) (context: HttpContext) : Task =
        task {
            context.Response.StatusCode <- StatusCodes.Status200OK
            context.Response.ContentType <- "text/event-stream"
            context.Response.Headers.CacheControl <- "no-cache"
            context.Response.Headers.Connection <- "keep-alive"
            // Send the headers now so the client reports an open stream
            // before the first matching message arrives.
            do! context.Response.Body.FlushAsync(context.RequestAborted)
            let subscription = live.Subscribe()

            try
                while not context.RequestAborted.IsCancellationRequested do
                    let! entry = subscription.Reader.ReadAsync(context.RequestAborted)
                    if Query.matches (query context) entry then
                        let json = JsonSerializer.Serialize(entry, jsonOptions)
                        do! context.Response.WriteAsync($"data: {json}\n\n")
                        do! context.Response.Body.FlushAsync(context.RequestAborted)
            finally
                subscription.Dispose()
        } :> Task

    let endpoints database live metrics = [
        get "/api/logs" (logs database)
        get "/api/tail" (stream live)
        get "/api/status" (status database metrics)
        get "/" (fun context -> context.Response.SendFileAsync("wwwroot/index.html"))
    ]
