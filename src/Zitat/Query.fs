namespace Zitat

open System
open System.Text

module Query =
    let empty =
        {
            Text = None
            Hostname = None
            Application = None
            Unit = None
            Source = None
            BootId = None
            Facility = None
            Severity = None
            Since = None
            Until = None
            Before = None
            Limit = 200
        }

    let private tokens (value: string) =
        let output = ResizeArray<string>()
        let current = StringBuilder()
        let mutable quoted = false

        let flush () =
            if current.Length > 0 then
                output.Add(current.ToString())
                current.Clear() |> ignore

        for character in value do
            match character with
            | '"' -> quoted <- not quoted
            | character when Char.IsWhiteSpace character && not quoted -> flush ()
            | character -> current.Append(character) |> ignore

        flush ()
        List.ofSeq output

    let private integer (value: string) =
        match Int32.TryParse value with
        | true, parsed -> Some parsed
        | _ -> None

    let private named values (value: string) =
        values |> Map.tryFind (value.ToLowerInvariant())

    let private severityValue value =
        integer value
        |> Option.orElseWith (fun () ->
            named
                (Map
                    [
                        "emerg", 0
                        "emergency", 0
                        "alert", 1
                        "crit", 2
                        "critical", 2
                        "err", 3
                        "error", 3
                        "warn", 4
                        "warning", 4
                        "notice", 5
                        "info", 6
                        "informational", 6
                        "debug", 7
                    ])
                value)

    let private facilityValue value =
        integer value
        |> Option.orElseWith (fun () ->
            named
                (Map
                    [
                        "kern", 0
                        "kernel", 0
                        "user", 1
                        "mail", 2
                        "daemon", 3
                        "auth", 4
                        "security", 4
                        "syslog", 5
                        "lpr", 6
                        "news", 7
                        "uucp", 8
                        "clock", 9
                        "authpriv", 10
                        "ftp", 11
                        "ntp", 12
                        "audit", 13
                        "alert", 14
                        "clock2", 15
                        "local0", 16
                        "local1", 17
                        "local2", 18
                        "local3", 19
                        "local4", 20
                        "local5", 21
                        "local6", 22
                        "local7", 23
                    ])
                value)

    /// Reads a severity name or number, optionally prefixed by a comparison.
    let numericFilter (value: string) =
        let after (prefix: string) =
            severityValue (value.Substring prefix.Length)

        if value.StartsWith "<=" then
            after "<=" |> Option.map AtMost
        elif value.StartsWith ">=" then
            after ">=" |> Option.map AtLeast
        elif value.StartsWith "<" then
            after "<" |> Option.map (fun n -> AtMost(n - 1))
        elif value.StartsWith ">" then
            after ">" |> Option.map (fun n -> AtLeast(n + 1))
        else
            severityValue value |> Option.map Exactly

    let parseText (value: string) (query: LogQuery) =
        let apply (result: LogQuery, text: string list) (token: string) =
            match token.Split(':', 2) with
            | [| "host"; value |] -> { result with Hostname = Some value }, text
            | [| "app"; value |] -> { result with Application = Some value }, text
            | [| "unit"; value |] -> { result with Unit = Some value }, text
            | [| "source"; value |] -> { result with Source = Some value }, text
            | [| "boot"; value |] -> { result with BootId = Some value }, text
            | [| "facility"; value |] ->
                { result with
                    Facility = facilityValue value
                },
                text
            | [| "severity"; value |] ->
                { result with
                    Severity = numericFilter value
                },
                text
            | _ -> result, token :: text

        let parsed, remaining = tokens value |> List.fold apply (query, [])
        let joined = remaining |> List.rev |> String.concat " "

        let text =
            if String.IsNullOrWhiteSpace joined then
                None
            else
                Some joined

        { parsed with Text = text }

    /// Tests an entry against a query in memory, for filtering the live
    /// stream. Exact-value filters are case-sensitive here for the same reason
    /// they are in the index: that is what the stored bytes mean.
    let matches (query: LogQuery) (entry: LogEntry) =
        let same expected actual =
            expected |> Option.forall (fun value -> actual = Some value)

        let within expected actual =
            match expected with
            | None -> true
            | Some filter ->
                actual
                |> Option.exists (fun found ->
                    match filter with
                    | Exactly value -> found = value
                    | AtMost value -> found <= value
                    | AtLeast value -> found >= value)

        query.Text
        |> Option.forall (fun needle -> entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase))
        && same query.Hostname entry.Hostname
        && same query.Application entry.Application
        && same query.Unit entry.Unit
        && same query.BootId entry.BootId
        && query.Source |> Option.forall (fun value -> entry.Source = value)
        && same query.Facility entry.Facility
        && within query.Severity entry.Severity
        && query.Since |> Option.forall (fun value -> entry.Realtime >= value)
        && query.Until |> Option.forall (fun value -> entry.Realtime <= value)
