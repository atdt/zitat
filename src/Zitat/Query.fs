namespace Zitat

open System
open System.Text

module Query =
    let empty = {
        Text = None
        Hostname = None
        Application = None
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

    let parseText (value: string) (query: LogQuery) =
        let apply (result: LogQuery, text: string list) (token: string) =
            let parts = token.Split(':', 2)

            match parts with
            | [| "host"; value |] -> { result with Hostname = Some value }, text
            | [| "app"; value |] -> { result with Application = Some value }, text
            | [| "facility"; value |] ->
                { result with Facility = integer value }, text
            | [| "severity"; value |] ->
                { result with Severity = integer value }, text
            | _ -> result, token :: text

        let parsed, remaining =
            tokens value
            |> List.fold apply (query, [])

        let joined = remaining |> List.rev |> String.concat " "
        let text = if String.IsNullOrWhiteSpace joined then None else Some joined

        { parsed with Text = text }
