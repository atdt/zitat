namespace Zitat

open System
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

    // Parses one query parameter, returning the parsed value (if any)
    // alongside the error it produced (if any).
    let private validated name parser context : 'a option * string list =
        match value name context with
        | None -> None, []
        | Some text ->
            match parser text with
            | Some parsed -> Some parsed, []
            | None -> None, [ $"invalid {name}" ]

    let private finish (errors: string list) value =
        match errors with
        | [] -> Ok value
        | _ -> Error(String.concat "; " errors)

    let query (context: HttpContext) =
        let textQuery, textErrors =
            match value "q" context with
            | None -> Query.empty, []
            | Some text ->
                match Query.parseText text with
                | Ok parsed -> parsed, []
                | Error error -> Query.empty, [ error ]

        let facility, facilityErrors = validated "facility" Query.facilityValue context

        let severity, severityErrors = validated "severity" Query.severityFilter context

        let before, beforeErrors = validated "before" Cursor.validate context

        let limit, limitErrors =
            validated
                "limit"
                (fun text -> Query.integer text |> Option.filter (fun n -> n >= 1 && n <= 1000))
                context

        let errors =
            List.concat [ textErrors; facilityErrors; severityErrors; beforeErrors; limitErrors ]

        finish
            errors
            { textQuery with
                Hostname = value "host" context |> Option.orElse textQuery.Hostname
                Application = value "app" context |> Option.orElse textQuery.Application
                Unit = value "unit" context |> Option.orElse textQuery.Unit
                Source = value "source" context |> Option.orElse textQuery.Source
                BootId = value "boot" context |> Option.orElse textQuery.BootId
                Facility = facility |> Option.orElse textQuery.Facility
                Severity = severity |> Option.orElse textQuery.Severity
                Before = before
                Limit = limit |> Option.defaultValue 200
            }

    let private badRequest message (context: HttpContext) =
        context.Response.StatusCode <- StatusCodes.Status400BadRequest
        context.Response.ContentType <- "text/plain; charset=utf-8"
        context.Response.WriteAsync(message)

    let private logs (reader: JournalReader) (context: HttpContext) =
        match query context with
        | Error message -> badRequest message context
        | Ok parsed ->
            let liveSince = DateTimeOffset.UtcNow

            let liveAfter =
                reader.Search { Query.empty with Limit = 1 }
                |> List.tryHead
                |> Option.map _.Cursor

            let items = reader.Search parsed

            // A full page does not prove that another page exists.
            let next =
                if items.Length = parsed.Limit then
                    items |> List.tryLast |> Option.map _.Cursor
                else
                    None

            Response.ofJsonOptions
                jsonOptions
                {|
                    items = items
                    nextBefore = next
                    liveAfter = liveAfter
                    liveSince = liveSince
                    effectiveSince = parsed.Since
                    effectiveUntil = parsed.Until
                |}
                context

    let private status (reader: JournalReader) context =
        Response.ofJsonOptions
            jsonOptions
            {|
                status = "ok"
                journal = reader.Status()
            |}
            context

    let private resume (context: HttpContext) =
        let after, afterErrors = validated "after" Cursor.validate context
        let fromTime, fromErrors = validated "from" Query.timestamp context
        let header = context.Request.Headers["Last-Event-ID"].ToString()

        let lastEventId, lastEventErrors =
            if String.IsNullOrWhiteSpace header then
                None, []
            else
                match Cursor.validate header with
                | Some _ as valid -> valid, []
                | None -> None, [ "invalid Last-Event-ID" ]

        let errors = List.concat [ afterErrors; fromErrors; lastEventErrors ]

        finish errors (lastEventId |> Option.orElse after, fromTime)

    // Writes every entry after `checkpoint` matching `filter` as an SSE
    // event, batching reads from the journal, and returns the cursor to
    // resume from on the next call.
    let private replayEntries
        (reader: JournalReader)
        (filter: LogQuery)
        (context: HttpContext)
        (token: CancellationToken)
        (startedAt: DateTimeOffset)
        (checkpoint: string option)
        : Task<string option> =
        task {
            let mutable checkpoint = checkpoint
            let mutable more = true

            while more && not token.IsCancellationRequested do
                let scan =
                    { Query.empty with
                        Before = checkpoint
                        Since = if checkpoint.IsNone then Some startedAt else None
                        Limit = 1000
                    }

                let entries = reader.Forward scan
                let mutable wrote = false

                for entry in entries do
                    checkpoint <- Some entry.Cursor

                    if Query.matches filter entry then
                        let json = JsonSerializer.Serialize(entry, jsonOptions)

                        do!
                            context.Response.WriteAsync(
                                $"id: {entry.Cursor}\ndata: {json}\n\n",
                                token
                            )

                        wrote <- true

                // Flush once per batch.
                if wrote then
                    do! context.Response.Body.FlushAsync(token)

                more <- entries.Length = scan.Limit

            return checkpoint
        }

    let private stream
        (stopping: CancellationToken)
        (reader: JournalReader)
        (live: LiveHub)
        (context: HttpContext)
        : Task =
        match query context, resume context with
        | Error message, _
        | _, Error message -> badRequest message context
        | Ok filter, Ok(initialCursor, fromTime) ->
            task {
                let startedAt = fromTime |> Option.defaultValue DateTimeOffset.UtcNow

                use linked =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        context.RequestAborted,
                        stopping
                    )

                let token = linked.Token
                let subscription = live.Subscribe()

                try
                    try
                        context.Response.StatusCode <- StatusCodes.Status200OK
                        context.Response.ContentType <- "text/event-stream"
                        context.Response.Headers.CacheControl <- "no-cache"
                        // EventSource waits for response headers before it opens.
                        do! context.Response.Body.FlushAsync(token)

                        let! checkpoint =
                            replayEntries reader filter context token startedAt initialCursor

                        let mutable checkpoint = checkpoint

                        while not token.IsCancellationRequested do
                            let! _ = subscription.Reader.ReadAsync(token)

                            let! next =
                                replayEntries reader filter context token startedAt checkpoint

                            checkpoint <- next
                    with :? OperationCanceledException when token.IsCancellationRequested ->
                        ()
                finally
                    subscription.Dispose()
            }
            :> Task

    let endpoints stopping reader live =
        [
            get "/api/logs" (logs reader)
            get "/api/tail" (stream stopping reader live)
            get "/api/status" (status reader)
        ]
