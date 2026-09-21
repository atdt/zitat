namespace Zitat

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions

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

    let integer (value: string) =
        match Int32.TryParse value with
        | true, parsed -> Some parsed
        | _ -> None

    let private named values (value: string) =
        values |> Map.tryFind (value.ToLowerInvariant())

    let private severityNames =
        Map
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
            ]

    let private facilityNames =
        Map
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
            ]

    let private numberOrName table lo hi value =
        integer value
        |> Option.filter (fun number -> number >= lo && number <= hi)
        |> Option.orElseWith (fun () -> named table value)

    let private severityValue value = numberOrName severityNames 0 7 value

    let facilityValue value = numberOrName facilityNames 0 23 value

    let severityFilter (value: string) =
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

    // A timestamp without an offset means UTC.
    let timestamp (text: string) =
        match
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )
        with
        | true, parsed -> Some parsed
        | _ -> None

    let private duration (value: string) =
        if not (Regex.IsMatch(value, @"^\d+(\.\d+)?[smhdw]$")) then
            None
        else
            let number = value[.. value.Length - 2]

            match Double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, amount when Double.IsFinite amount && amount > 0. ->
                try
                    match value[value.Length - 1] with
                    | 's' -> Some(TimeSpan.FromSeconds amount)
                    | 'm' -> Some(TimeSpan.FromMinutes amount)
                    | 'h' -> Some(TimeSpan.FromHours amount)
                    | 'd' -> Some(TimeSpan.FromDays amount)
                    | 'w' -> Some(TimeSpan.FromDays(amount * 7.))
                    | _ -> None
                with
                | :? ArgumentOutOfRangeException
                | :? OverflowException -> None
            | _ -> None

    let private relativeTime (now: DateTimeOffset) (value: string) =
        let offset sign text =
            duration text
            |> Option.bind (fun span ->
                try
                    Some(now.Add(sign span))
                with
                | :? ArgumentOutOfRangeException
                | :? OverflowException -> None)

        if value = "now" then Some now
        elif value.StartsWith "now-" then offset (~-) value[4..]
        elif value.StartsWith "now+" then offset id value[4..]
        else None

    let private absoluteTime (value: string) =
        if Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$") then
            match
                DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
            with
            | true, date ->
                Some(DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero))
            | _ -> None
        elif
            Regex.IsMatch(
                value,
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?([Zz]|[+-]\d{2}:\d{2})$"
            )
        then
            timestamp value
        else
            None

    let private time now value =
        relativeTime now value |> Option.orElseWith (fun () -> absoluteTime value)

    let parseText (value: string) =
        let now = DateTimeOffset.UtcNow

        let apply (state: Result<LogQuery * string list, string>) (token: string) =
            state
            |> Result.bind (fun (result, text) ->
                match token.Split(':', 2) with
                | [| "host"; value |] -> Ok({ result with Hostname = Some value }, text)
                | [| "app"; value |] -> Ok({ result with Application = Some value }, text)
                | [| "unit"; value |] -> Ok({ result with Unit = Some value }, text)
                | [| "source"; value |] -> Ok({ result with Source = Some value }, text)
                | [| "boot"; value |] -> Ok({ result with BootId = Some value }, text)
                | [| "facility"; value |] ->
                    match facilityValue value with
                    | Some number -> Ok({ result with Facility = Some number }, text)
                    | None -> Error $"invalid facility: {value}; expected a name or number 0–23"
                | [| "severity"; value |] ->
                    match severityFilter value with
                    | Some filter -> Ok({ result with Severity = Some filter }, text)
                    | None -> Error $"invalid severity: {value}; expected a name or number 0–7"
                | [| "since"; value |] ->
                    match time now value with
                    | Some parsed -> Ok({ result with Since = Some parsed }, text)
                    | None ->
                        Error
                            $"invalid since: {value}; expected now, now-30m, or an ISO date or timestamp with offset"
                | [| "until"; value |] ->
                    match time now value with
                    | Some parsed -> Ok({ result with Until = Some parsed }, text)
                    | None ->
                        Error
                            $"invalid until: {value}; expected now, now-30m, or an ISO date or timestamp with offset"
                | _ -> Ok(result, token :: text))

        tokens value
        |> List.fold apply (Ok(empty, []))
        |> Result.map (fun (parsed, remaining) ->
            let joined = remaining |> List.rev |> String.concat " "

            let text =
                if String.IsNullOrWhiteSpace joined then
                    None
                else
                    Some joined

            { parsed with Text = text })

    // Match fields case-sensitively.
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
        |> Option.forall (fun needle ->
            entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase))
        && same query.Hostname entry.Hostname
        && same query.Application entry.Application
        && same query.Unit entry.Unit
        && same query.BootId entry.BootId
        && query.Source |> Option.forall (fun value -> entry.Source = value)
        && same query.Facility entry.Facility
        && within query.Severity entry.Severity
        && query.Since |> Option.forall (fun value -> entry.Realtime >= value)
        && query.Until |> Option.forall (fun value -> entry.Realtime < value)
