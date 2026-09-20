namespace Zitat.Journal

open System
open System.Collections.Generic
open System.IO
open System.Text
open Zitat

module Fields =
    [<Literal>]
    let Message = "MESSAGE"

    [<Literal>]
    let Priority = "PRIORITY"

    [<Literal>]
    let Facility = "SYSLOG_FACILITY"

    [<Literal>]
    let Hostname = "_HOSTNAME"

    [<Literal>]
    let Identifier = "SYSLOG_IDENTIFIER"

    [<Literal>]
    let ProcessId = "_PID"

    [<Literal>]
    let Unit = "_SYSTEMD_UNIT"

    [<Literal>]
    let BootId = "_BOOT_ID"

module private Clock =
    // A journal microsecond equals ten DateTimeOffset ticks.
    let toInstant (microseconds: uint64) =
        DateTimeOffset.UnixEpoch.AddTicks(int64 microseconds * 10L)

    // Floor until and ceil since to the journal's microsecond precision.
    let upperBound (instant: DateTimeOffset) =
        let ticks = (instant - DateTimeOffset.UnixEpoch).Ticks
        if ticks <= 0L then 0UL else uint64 ticks / 10UL

    let lowerBound (instant: DateTimeOffset) =
        let ticks = (instant - DateTimeOffset.UnixEpoch).Ticks
        if ticks <= 0L then 0UL else uint64 ((ticks + 9L) / 10L)

module Cursor =
    // The sequence ID breaks ties across writers with equal timestamps and
    // sequence numbers.
    let order (seqnumId: byte[]) (seqnum: uint64) (realtime: uint64) = realtime, seqnum, seqnumId

    let encode (seqnumId: byte[]) (seqnum: uint64) (realtime: uint64) =
        let buffer = Array.zeroCreate<byte> 32
        Array.blit seqnumId 0 buffer 0 16
        BitConverter.TryWriteBytes(Span<byte>(buffer, 16, 8), seqnum) |> ignore
        BitConverter.TryWriteBytes(Span<byte>(buffer, 24, 8), realtime) |> ignore
        Convert.ToBase64String(buffer).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let decode (text: string) =
        let padded =
            let restored = text.Replace('-', '+').Replace('_', '/')
            restored + String('=', (4 - restored.Length % 4) % 4)

        try
            let buffer = Convert.FromBase64String padded

            if buffer.Length <> 32 then
                None
            else
                let seqnumId = buffer[0..15]
                let seqnum = BitConverter.ToUInt64(buffer, 16)
                let realtime = BitConverter.ToUInt64(buffer, 24)
                Some(seqnumId, seqnum, realtime)
        with :? FormatException ->
            None

module Source =
    // systemd-journal-remote uses remote-<sender>[@...].journal filenames.
    let ofPath (path: string) =
        let name =
            Path.GetFileNameWithoutExtension path |> Option.ofObj |> Option.defaultValue ""

        let stem =
            match name.IndexOf '@' with
            | -1 -> name
            | at -> name.Substring(0, at)

        if stem.StartsWith "remote-" then stem.Substring 7 else stem

/// Refresh and Use share a lock because unmapping a file during a read can
/// crash the process.
[<Sealed>]
type JournalSet(root: string, log: string -> unit) =
    let files = Dictionary<string, JournalFile>()
    let gate = obj ()

    let close path =
        match files.TryGetValue path with
        | true, file ->
            (file :> IDisposable).Dispose()
            files.Remove path |> ignore
        | _ -> ()

    member _.Root = root

    member _.Refresh() =
        lock gate (fun () ->
            let onDisk =
                if Directory.Exists root then
                    Directory.EnumerateFiles(root, "*.journal", SearchOption.AllDirectories)
                    |> Set.ofSeq
                else
                    Set.empty

            for path in files.Keys |> Seq.toArray do
                if not (onDisk.Contains path) then
                    close path

            for path in onDisk do
                let stale =
                    match files.TryGetValue path with
                    | true, file -> file.MappedLength <> FileInfo(path).Length
                    | _ -> true

                if stale then
                    close path

                    try
                        files[path] <- JournalFile.Open path
                    with
                    // Abort refresh so unsupported formats are not silently omitted.
                    | UnsupportedJournal _ -> reraise ()
                    // Skip corrupt files so other files remain readable.
                    | CorruptJournal(path, reason) -> log $"skipping %s{path}: %s{reason}")

    member _.Use(action: JournalFile list -> 'a) =
        lock gate (fun () -> action (files.Values |> Seq.sortBy _.Path |> List.ofSeq))

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                for file in files.Values do
                    (file :> IDisposable).Dispose()

                files.Clear())

[<Sealed>]
type JournalReader(set: JournalSet) =
    let messagePrefix = Encoding.UTF8.GetBytes(Fields.Message + "=")

    let decodePair (payload: byte[]) =
        match Array.IndexOf(payload, byte '=') with
        | -1 -> Encoding.UTF8.GetString payload, ""
        | at ->
            let name = Encoding.UTF8.GetString(payload, 0, at)
            let value = Encoding.UTF8.GetString(payload, at + 1, payload.Length - at - 1)
            name, value

    // An indexed value absent from this file rules out the file.
    let termsFor (file: JournalFile) (query: LogQuery) =
        let single field value =
            match value with
            | None -> Some None
            | Some text ->
                match file.FindData($"%s{field}=%s{text}") with
                | ValueSome offset ->
                    Some(
                        Some
                            {
                                Field = field
                                Objects = [| offset |]
                            }
                    )
                | ValueNone -> None

        let severities =
            match query.Severity with
            | None -> Some None
            | Some filter ->
                let admitted =
                    match filter with
                    | Exactly value -> [ value ]
                    | AtMost value -> [ 0 .. min 7 value ]
                    | AtLeast value -> [ max 0 value .. 7 ]

                let objects =
                    admitted
                    |> List.choose (fun value ->
                        match file.FindData $"%s{Fields.Priority}=%d{value}" with
                        | ValueSome offset -> Some offset
                        | ValueNone -> None)

                if objects.IsEmpty then
                    None
                else
                    Some(
                        Some
                            {
                                Field = Fields.Priority
                                Objects = List.toArray objects
                            }
                    )

        let facility = query.Facility |> Option.map (fun value -> $"%d{value}")

        [
            single Fields.Hostname query.Hostname
            single Fields.Identifier query.Application
            single Fields.Unit query.Unit
            single Fields.BootId query.BootId
            single Fields.Facility facility
            severities
        ]
        |> List.fold
            (fun state resolved ->
                match state, resolved with
                | Some found, Some(Some term) -> Some(term :: found)
                | Some found, Some None -> Some found
                | _ -> None)
            (Some [])

    // None excludes the file. Some ValueNone leaves the scan unbounded.
    let boundFor (file: JournalFile) (direction: Direction) (limit: uint64 option) =
        match limit with
        | None -> Some ValueNone
        | Some instant ->
            let chain = file.GlobalChain()

            match direction with
            | Newest ->
                let position =
                    if instant = UInt64.MaxValue then
                        chain.Count
                    else
                        chain.LowerBound(file.EntryRealtime, instant + 1UL)

                if position = 0L then
                    None
                else
                    Some(ValueSome chain[position - 1L])
            | Oldest ->
                let position = chain.LowerBound(file.EntryRealtime, instant)

                if position >= chain.Count then
                    None
                else
                    Some(ValueSome chain[position])

    let textMatches (file: JournalFile) offset (text: string option) =
        match text with
        | None -> true
        | Some needle ->
            match file.EntryField(offset, messagePrefix) with
            | ValueSome value ->
                let message = Encoding.UTF8.GetString value
                message.Contains(needle, StringComparison.OrdinalIgnoreCase)
            | ValueNone -> false

    let materialize (file: JournalFile) offset =
        let fields = file.EntryFields offset |> Array.map decodePair |> List.ofArray

        let lookup name =
            fields
            |> List.tryPick (fun (key, value) -> if key = name then Some value else None)

        let number name =
            lookup name
            |> Option.bind (fun text ->
                match Int32.TryParse text with
                | true, v -> Some v
                | _ -> None)

        let realtime = file.EntryRealtime offset

        {
            Cursor = Cursor.encode file.SeqnumId (file.EntrySeqnum offset) realtime
            Realtime = Clock.toInstant realtime
            Source = Source.ofPath file.Path
            Hostname = lookup Fields.Hostname
            Application = lookup Fields.Identifier
            Unit = lookup Fields.Unit
            ProcessId = lookup Fields.ProcessId
            BootId = lookup Fields.BootId
            Facility = number Fields.Facility
            Severity = number Fields.Priority
            Message = lookup Fields.Message |> Option.defaultValue ""
            Fields = fields
        }

    member private _.Collect(query: LogQuery, direction: Direction) =
        set.Use(fun openFiles ->
            let limit = Math.Clamp(query.Limit, 1, 1000)
            let cursor = query.Before |> Option.bind Cursor.decode

            let cursorOrder =
                cursor
                |> Option.map (fun (id, seqnum, realtime) -> Cursor.order id seqnum realtime)

            let sinceUsec = query.Since |> Option.map Clock.lowerBound
            let untilUsec = query.Until |> Option.map Clock.upperBound

            // The cursor timestamp narrows the scan before the full cursor is compared.
            let cursorRealtime = cursor |> Option.map (fun (_, _, realtime) -> realtime)

            let upper =
                match direction, cursorRealtime, untilUsec with
                | Newest, Some position, Some bound -> Some(min position bound)
                | Newest, Some position, None -> Some position
                | _, _, bound -> bound

            let lower =
                match direction, cursorRealtime, sinceUsec with
                | Oldest, Some position, Some bound -> Some(max position bound)
                | Oldest, Some position, None -> Some position
                | _, _, bound -> bound

            let startBound =
                match direction with
                | Newest -> upper
                | Oldest -> lower

            let stopAt =
                match direction with
                | Newest -> lower
                | Oldest -> upper

            let overlaps (file: JournalFile) =
                (lower |> Option.forall (fun bound -> file.TailRealtime >= bound))
                && (upper |> Option.forall (fun bound -> file.HeadRealtime <= bound))

            let candidates =
                openFiles
                |> List.filter (fun file ->
                    query.Source |> Option.forall (fun wanted -> Source.ofPath file.Path = wanted))
                |> List.filter overlaps
                |> List.choose (fun file ->
                    termsFor file query
                    |> Option.bind (fun terms ->
                        boundFor file direction startBound
                        |> Option.map (fun bound ->
                            file, FileScan(file, terms, direction, bound))))
                |> List.toArray

            let heads = candidates |> Array.map (fun (_, scan) -> scan.Next())
            let results = ResizeArray<LogEntry>()
            let mutable running = true

            while running && results.Count < limit do
                let mutable chosen = -1
                let mutable chosenOrder = None

                for index in 0 .. candidates.Length - 1 do
                    match heads[index] with
                    | ValueNone -> ()
                    | ValueSome offset ->
                        let file, _ = candidates[index]
                        let realtime = file.EntryRealtime offset
                        let seqnum = file.EntrySeqnum offset
                        let order = Cursor.order file.SeqnumId seqnum realtime

                        let better =
                            match chosenOrder with
                            | None -> true
                            | Some current ->
                                match direction with
                                | Newest -> order > current
                                | Oldest -> order < current

                        if better then
                            chosen <- index
                            chosenOrder <- Some order

                if chosen < 0 then
                    running <- false
                else
                    let file, scan = candidates[chosen]
                    let offset = heads[chosen].Value
                    let order = chosenOrder.Value
                    let realtime, _, _ = order

                    let past =
                        match direction, stopAt with
                        | Newest, Some bound -> realtime < bound
                        | Oldest, Some bound -> realtime > bound
                        | _ -> false

                    if past then
                        // This scan is time-ordered and cannot re-enter the range.
                        heads[chosen] <- ValueNone
                    else
                        let afterCursor =
                            match direction, cursorOrder with
                            | Newest, Some position -> order < position
                            | Oldest, Some position -> order > position
                            | _ -> true

                        if afterCursor && textMatches file offset query.Text then
                            results.Add(materialize file offset)

                        heads[chosen] <- scan.Next()

            List.ofSeq results)

    /// Returns matching entries newest first.
    member this.Search(query: LogQuery) = this.Collect(query, Newest)

    /// Returns matching entries oldest first, after Before when supplied.
    member this.Forward(query: LogQuery) = this.Collect(query, Oldest)

    member _.Status() =
        set.Use(fun files ->
            let realtimes =
                files |> List.collect (fun file -> [ file.HeadRealtime; file.TailRealtime ])

            {|
                directory = set.Root
                files = files.Length
                entries = files |> List.sumBy _.EntryCount
                bytes = files |> List.sumBy _.MappedLength
                sources =
                    files
                    |> List.map (fun file -> Source.ofPath file.Path)
                    |> List.distinct
                    |> List.sort
                oldest =
                    realtimes
                    |> function
                        | [] -> None
                        | values -> Some(Clock.toInstant (List.min values))
                newest =
                    realtimes
                    |> function
                        | [] -> None
                        | values -> Some(Clock.toInstant (List.max values))
            |})
