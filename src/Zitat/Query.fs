namespace Zitat

open System
open System.Text

module Query =
    let empty = {
        Text = None
        Hostname = None
        Application = None
        SourceAddress = None
        Facility = None
        Severity = None
        Since = None
        Until = None
        BeforeId = None
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

    /// Reads a plain number or one prefixed by a comparison operator.
    let numericFilter (value: string) =
        let after (prefix: string) = integer (value.Substring prefix.Length)

        if value.StartsWith "<=" then after "<=" |> Option.map AtMost
        elif value.StartsWith ">=" then after ">=" |> Option.map AtLeast
        elif value.StartsWith "<" then after "<" |> Option.map (fun n -> AtMost(n - 1))
        elif value.StartsWith ">" then after ">" |> Option.map (fun n -> AtLeast(n + 1))
        else integer value |> Option.map Exactly

    let parseText (value: string) (query: LogQuery) =
        let apply (result: LogQuery, text: string list) (token: string) =
            let parts = token.Split(':', 2)

            match parts with
            | [| "host"; value |] -> { result with Hostname = Some value }, text
            | [| "app"; value |] -> { result with Application = Some value }, text
            | [| "source"; value |] -> { result with SourceAddress = Some value }, text
            | [| "facility"; value |] ->
                { result with Facility = integer value }, text
            | [| "severity"; value |] ->
                { result with Severity = numericFilter value }, text
            | _ -> result, token :: text

        let parsed, remaining =
            tokens value
            |> List.fold apply (query, [])

        let joined = remaining |> List.rev |> String.concat " "
        let text = if String.IsNullOrWhiteSpace joined then None else Some joined

        { parsed with Text = text }

    let matches (query: LogQuery) (entry: LogEntry) =
        let same (expected: string option) (actual: string option) =
            match expected with
            | None -> true
            | Some value ->
                actual
                |> Option.exists (fun found ->
                    String.Equals(value, found, StringComparison.OrdinalIgnoreCase))

        let contains (expected: string option) (actual: string) =
            expected
            |> Option.forall (fun value ->
                actual.Contains(value, StringComparison.OrdinalIgnoreCase))

        let equal expected actual =
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

        contains query.Text entry.Message
        && same query.Hostname entry.Hostname
        && same query.Application entry.Application
        && same query.SourceAddress (Some entry.SourceAddress)
        && equal query.Facility entry.Facility
        && within query.Severity entry.Severity
        && query.Since |> Option.forall (fun value -> entry.ReceivedAt >= value)
        && query.Until |> Option.forall (fun value -> entry.ReceivedAt <= value)
