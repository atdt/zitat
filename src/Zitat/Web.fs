namespace Zitat

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Falco
open Falco.Routing
open Microsoft.AspNetCore.Http
open Zitat.Journal

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

    // A timestamp without an offset means UTC, matching journal timestamps.
    let private timestamp name context =
        value name context
        |> Option.bind (fun text ->
            match
                DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
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
            Unit = value "unit" context |> Option.orElse textQuery.Unit
            Source = value "source" context |> Option.orElse textQuery.Source
            BootId = value "boot" context |> Option.orElse textQuery.BootId
            Facility = integer "facility" context |> Option.orElse textQuery.Facility
            Severity =
                value "severity" context
                |> Option.bind Query.numericFilter
                |> Option.orElse textQuery.Severity
            Since = since
            Until = timestamp "until" context
            Before = value "before" context
            Limit = integer "limit" context |> Option.defaultValue 200
        }

    let private logs (reader: JournalReader) (context: HttpContext) =
        let parsed = query context
        let items = reader.Search parsed
        let limit = Math.Clamp(parsed.Limit, 1, 1000)

        // A full page may have more entries; the reader does not check ahead.
        let next =
            if items.Length = limit then
                items |> List.tryLast |> Option.map _.Cursor
            else
                None

        Response.ofJsonOptions jsonOptions {| items = items; nextBefore = next |} context

    let private status (reader: JournalReader) context =
        Response.ofJsonOptions
            jsonOptions
            {|
                status = "ok"
                journal = reader.Status()
            |}
            context

    let private stream (stopping: CancellationToken) (live: LiveHub) (context: HttpContext) : Task =
        task {
            context.Response.StatusCode <- StatusCodes.Status200OK
            context.Response.ContentType <- "text/event-stream"
            context.Response.Headers.CacheControl <- "no-cache"
            context.Response.Headers.Connection <- "keep-alive"
            // Flush headers so EventSource opens before the first entry arrives.
            do! context.Response.Body.FlushAsync(context.RequestAborted)

            // Stop an open stream when the host stops, even if the client stays connected.
            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping)

            let token = linked.Token
            let subscription = live.Subscribe()
            let filter = query context

            try
                while not token.IsCancellationRequested do
                    let! entry = subscription.Reader.ReadAsync(token)

                    if Query.matches filter entry then
                        let json = JsonSerializer.Serialize(entry, jsonOptions)
                        do! context.Response.WriteAsync($"data: {json}\n\n", token)
                        do! context.Response.Body.FlushAsync(token)
            finally
                subscription.Dispose()
        }
        :> Task

    let endpoints stopping reader live =
        [
            get "/api/logs" (logs reader)
            get "/api/tail" (stream stopping live)
            get "/api/status" (status reader)
        ]
