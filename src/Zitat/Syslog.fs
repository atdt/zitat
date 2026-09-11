namespace Zitat

open System
open System.Globalization
open System.Text.RegularExpressions

module Syslog =
    let private invariant = CultureInfo.InvariantCulture

    let private rfc5424 =
        Regex(
            "^<(?<pri>\\d{1,3})>(?<version>\\d+) "
            + "(?<time>\\S+) (?<host>\\S+) (?<app>\\S+) "
            + "(?<pid>\\S+) (?<msgid>\\S+) "
            + "(?<sd>-|(?:\\[(?:[^\\]\\\\]|\\\\.)*\\])+)(?: (?<msg>.*))?$",
            RegexOptions.Compiled ||| RegexOptions.Singleline
        )

    let private rfc3164 =
        Regex(
            "^<(?<pri>\\d{1,3})>(?<time>[A-Z][a-z]{2}\\s+\\d{1,2} "
            + "\\d{2}:\\d{2}:\\d{2}) (?<host>\\S+) "
            + "(?<tag>[^: ]+)(?:: ?)?(?<msg>.*)$",
            RegexOptions.Compiled ||| RegexOptions.Singleline
        )

    let private tag =
        Regex("^(?<app>.+?)(?:\\[(?<pid>[^]]+)\\])?$", RegexOptions.Compiled)

    let private optional value =
        if String.IsNullOrWhiteSpace value || value = "-" then None
        else Some value

    let private priority (value: string) =
        match Int32.TryParse value with
        | true, pri when pri >= 0 && pri <= 191 ->
            Some(pri / 8), Some(pri % 8)
        | _ -> None, None

    let private timestamp (value: string) =
        match DateTimeOffset.TryParse(
            value,
            invariant,
            DateTimeStyles.AllowWhiteSpaces ||| DateTimeStyles.AssumeUniversal
        ) with
        | true, parsed -> Some parsed
        | _ -> None

    let private legacyTimestamp (receivedAt: DateTimeOffset) value =
        let withYear = $"{receivedAt.Year} {value}"
        let format = "yyyy MMM d HH:mm:ss"

        match DateTimeOffset.TryParseExact(withYear, format, invariant, DateTimeStyles.None) with
        | true, parsed ->
            let local = DateTimeOffset(parsed.DateTime, receivedAt.Offset)
            if local > receivedAt.AddDays(1.0) then Some(local.AddYears(-1))
            else Some local
        | _ -> None

    let private entry receivedAt source raw sent host app pid facility severity message =
        {
            ReceivedAt = receivedAt
            SentAt = sent
            Hostname = host
            Application = app
            ProcessId = pid
            Facility = facility
            Severity = severity
            Message = message
            SourceAddress = source
            RawMessage = raw
        }

    let private parse5424 receivedAt source raw (matched: Match) =
        let facility, severity = priority matched.Groups["pri"].Value

        entry
            receivedAt
            source
            raw
            (timestamp matched.Groups["time"].Value)
            (optional matched.Groups["host"].Value)
            (optional matched.Groups["app"].Value)
            (optional matched.Groups["pid"].Value)
            facility
            severity
            matched.Groups["msg"].Value

    let private parse3164 receivedAt source raw (matched: Match) =
        let facility, severity = priority matched.Groups["pri"].Value
        let parsedTag = tag.Match matched.Groups["tag"].Value

        entry
            receivedAt
            source
            raw
            (legacyTimestamp receivedAt matched.Groups["time"].Value)
            (optional matched.Groups["host"].Value)
            (optional parsedTag.Groups["app"].Value)
            (optional parsedTag.Groups["pid"].Value)
            facility
            severity
            matched.Groups["msg"].Value

    let parse receivedAt source (input: string) =
        let raw = input.TrimEnd('\r', '\n').TrimStart('\uFEFF')
        let modern = rfc5424.Match raw

        if modern.Success then
            parse5424 receivedAt source raw modern
        else
            let legacy = rfc3164.Match raw

            if legacy.Success then
                parse3164 receivedAt source raw legacy
            else
                entry receivedAt source raw None None None None None None raw
