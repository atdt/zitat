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

    let private integer (text: string) =
        match Int32.TryParse text with
        | true, parsed -> Some parsed
        | _ -> None

    // A timestamp without an offset means UTC, matching journal timestamps.
    let private timestamp (text: string) =
        match
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed -> Some parsed
        | _ -> None

    let private relativeTime (value: string) =
        if value.Length < 2 then
            None
        else
            let number = value[.. value.Length - 2]

            match Double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, amount when Double.IsFinite amount && amount > 0. ->
                try
                    match value[value.Length - 1] with
                    | 'm' -> Some(DateTimeOffset.UtcNow.AddMinutes(-amount))
                    | 'h' -> Some(DateTimeOffset.UtcNow.AddHours(-amount))
                    | 'd' -> Some(DateTimeOffset.UtcNow.AddDays(-amount))
                    | _ -> None
                with
                | :? ArgumentOutOfRangeException
                | :? OverflowException -> None
            | _ -> None

    let private validated name parser (errors: ResizeArray<string>) context =
        match value name context with
        | None -> None
        | Some text ->
            match parser text with
            | Some parsed -> Some parsed
            | None ->
                errors.Add($"invalid {name}")
                None

    let query (context: HttpContext) =
        let errors = ResizeArray<string>()

        let textQuery =
            match value "q" context with
            | None -> Query.empty
            | Some text ->
                match Query.parseText text with
                | Ok parsed -> parsed
                | Error error ->
                    errors.Add error
                    Query.empty

        let range = validated "range" relativeTime errors context
        let since = validated "since" timestamp errors context
        let until = validated "until" timestamp errors context

        let facility = validated "facility" Query.facilityValue errors context

        let severity = validated "severity" Query.severityFilter errors context

        let before =
            validated
                "before"
                (fun text -> Cursor.decode text |> Option.map (fun _ -> text))
                errors
                context

        let limit =
            validated
                "limit"
                (fun text -> integer text |> Option.filter (fun n -> n >= 1 && n <= 1000))
                errors
                context

        if errors.Count > 0 then
            Error(String.concat "; " errors)
        else
            Ok
                { textQuery with
                    Hostname = value "host" context |> Option.orElse textQuery.Hostname
                    Application = value "app" context |> Option.orElse textQuery.Application
                    Unit = value "unit" context |> Option.orElse textQuery.Unit
                    Source = value "source" context |> Option.orElse textQuery.Source
                    BootId = value "boot" context |> Option.orElse textQuery.BootId
                    Facility = facility |> Option.orElse textQuery.Facility
                    Severity = severity |> Option.orElse textQuery.Severity
                    Since = range |> Option.orElse since
                    Until = until
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
        let errors = ResizeArray<string>()

        let after =
            validated
                "after"
                (fun text -> Cursor.decode text |> Option.map (fun _ -> text))
                errors
                context

        let fromTime = validated "from" timestamp errors context
        let header = context.Request.Headers["Last-Event-ID"].ToString()

        let lastEventId =
            if String.IsNullOrWhiteSpace header then
                None
            elif Cursor.decode header |> Option.isSome then
                Some header
            else
                errors.Add("invalid Last-Event-ID")
                None

        if errors.Count > 0 then
            Error(String.concat "; " errors)
        else
            Ok(lastEventId |> Option.orElse after, fromTime)

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
                let mutable checkpoint = initialCursor

                let replay () =
                    task {
                        let mutable more = true

                        while more && not token.IsCancellationRequested do
                            let scan =
                                { Query.empty with
                                    Before = checkpoint
                                    Since = if checkpoint.IsNone then Some startedAt else None
                                    Limit = 1000
                                }

                            let entries = reader.Forward scan

                            for entry in entries do
                                checkpoint <- Some entry.Cursor

                                if Query.matches filter entry then
                                    let json = JsonSerializer.Serialize(entry, jsonOptions)

                                    do!
                                        context.Response.WriteAsync(
                                            $"id: {entry.Cursor}\ndata: {json}\n\n",
                                            token
                                        )

                                    do! context.Response.Body.FlushAsync(token)

                            more <- entries.Length = scan.Limit
                    }

                try
                    try
                        context.Response.StatusCode <- StatusCodes.Status200OK
                        context.Response.ContentType <- "text/event-stream"
                        context.Response.Headers.CacheControl <- "no-cache"
                        // EventSource waits for response headers before it opens.
                        do! context.Response.Body.FlushAsync(token)
                        do! replay ()

                        while not token.IsCancellationRequested do
                            let! _ = subscription.Reader.ReadAsync(token)
                            do! replay ()
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
