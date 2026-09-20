namespace Zitat.Journal

open System
open System.Collections.Generic
open System.IO
open System.Text
open Zitat

/// Journal field names this reader gives first-class meaning to. Everything
/// else an entry carries is preserved in LogEntry.Fields.
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
    /// Journal timestamps are microseconds since the Unix epoch; a tick is
    /// 100ns.
    let toInstant (microseconds: uint64) =
        DateTimeOffset.UnixEpoch.AddTicks(int64 microseconds * 10L)

    let ofInstant (instant: DateTimeOffset) =
        let ticks = (instant - DateTimeOffset.UnixEpoch).Ticks
        if ticks <= 0L then 0UL else uint64 ticks / 10UL

/// An opaque position in the merged stream: the writing file's sequence
/// number identity, its sequence number, and the entry's timestamp.
module Cursor =
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
                Some(buffer[0..15], BitConverter.ToUInt64(buffer, 16), BitConverter.ToUInt64(buffer, 24))
        with :? FormatException ->
            None

/// The sender a journal file holds. systemd-journal-remote names files after
/// the source it received them from, and rotation appends an @-suffix, so the
/// sender is the filename up to the first `@`.
module Source =
    let ofPath (path: string) =
        let name =
            Path.GetFileNameWithoutExtension path |> Option.ofObj |> Option.defaultValue ""

        let stem =
            match name.IndexOf '@' with
            | -1 -> name
            | at -> name.Substring(0, at)

        if stem.StartsWith "remote-" then stem.Substring 7 else stem

/// The journal files under a directory, kept open and memory-mapped.
///
/// A file systemd is still writing grows in place, so refreshing remaps any
/// file whose length has changed and picks up files that rotation has created.
/// Unmapping a file while another thread is reading it is a segmentation
/// fault rather than an exception, so refreshing and reading are serialised.
/// Both are short: a refresh stats the directory, a query runs in milliseconds.
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
                    // A file whose format we do not implement is fatal by
                    // design: reading it with the wrong assumptions would show
                    // wrong logs rather than none.
                    | UnsupportedJournal _ -> reraise ()
                    // Corruption is expected rather than exceptional, and the
                    // format requires readers to degrade around it. A file being
                    // created right now also lands here.
                    | CorruptJournal(path, reason) -> log $"skipping %s{path}: %s{reason}")

    /// Runs `action` against the open files with the set held still.
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
            Encoding.UTF8.GetString(payload, 0, at), Encoding.UTF8.GetString(payload, at + 1, payload.Length - at - 1)

    /// The DATA objects a query's exact-value filters resolve to in one file.
    /// Returns None when a filter names a value this file never recorded, in
    /// which case the file cannot contribute a single entry.
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

    /// Translates a timestamp bound into an entry offset within one file.
    /// `None` means the file holds nothing on the wanted side of the bound.
    let boundFor (file: JournalFile) (direction: Direction) (limit: uint64 option) =
        match limit with
        | None -> Some ValueNone
        | Some instant ->
            let chain = file.GlobalChain()

            match direction with
            | Newest ->
                let position = chain.LowerBound(file.EntryRealtime, instant + 1UL)

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
            | ValueSome value -> Encoding.UTF8.GetString(value).Contains(needle, StringComparison.OrdinalIgnoreCase)
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

    /// Walks every candidate file at once, always taking the entry that sorts
    /// next in `direction`, so the result is one ordered stream across senders.
    ///
    /// Files are ordered by realtime and then sequence number. Two entries from
    /// different senders sharing a microsecond fall back to file order, which
    /// is stable but arbitrary; at microsecond resolution this is not a case
    /// that arises in practice.
    member private _.Collect(query: LogQuery, direction: Direction) =
        set.Use(fun openFiles ->
            let limit = Math.Clamp(query.Limit, 1, 1000)
            let cursor = query.Before |> Option.bind Cursor.decode
            let sinceUsec = query.Since |> Option.map Clock.ofInstant
            let untilUsec = query.Until |> Option.map Clock.ofInstant

            // A cursor tightens the bound that iteration starts from: paging back
            // resumes below its timestamp, tailing resumes above it.
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
                        |> Option.map (fun bound -> file, FileScan(file, terms, direction, bound))))
                |> List.toArray

            let heads = candidates |> Array.map (fun (_, scan) -> scan.Next())
            let results = ResizeArray<LogEntry>()
            let mutable running = true

            while running && results.Count < limit do
                let mutable chosen = -1
                let mutable chosenRealtime = 0UL
                let mutable chosenSeqnum = 0UL

                for index in 0 .. candidates.Length - 1 do
                    match heads[index] with
                    | ValueNone -> ()
                    | ValueSome offset ->
                        let file, _ = candidates[index]
                        let realtime = file.EntryRealtime offset
                        let seqnum = file.EntrySeqnum offset

                        let better =
                            chosen < 0
                            || (match direction with
                                | Newest ->
                                    realtime > chosenRealtime
                                    || (realtime = chosenRealtime && seqnum > chosenSeqnum)
                                | Oldest ->
                                    realtime < chosenRealtime
                                    || (realtime = chosenRealtime && seqnum < chosenSeqnum))

                        if better then
                            chosen <- index
                            chosenRealtime <- realtime
                            chosenSeqnum <- seqnum

                if chosen < 0 then
                    running <- false
                else
                    let file, scan = candidates[chosen]
                    let offset = heads[chosen].Value

                    let past =
                        match direction, stopAt with
                        | Newest, Some bound -> chosenRealtime < bound
                        | Oldest, Some bound -> chosenRealtime > bound
                        | _ -> false

                    if past then
                        // Entries only get further from the bound from here, so
                        // this file is finished rather than merely skipped.
                        heads[chosen] <- ValueNone
                    else
                        let afterCursor =
                            match direction, cursor with
                            | Newest, Some(_, seqnum, realtime) ->
                                chosenRealtime < realtime
                                || (chosenRealtime = realtime && chosenSeqnum < seqnum)
                            | Oldest, Some(_, seqnum, realtime) ->
                                chosenRealtime > realtime
                                || (chosenRealtime = realtime && chosenSeqnum > seqnum)
                            | _ -> true

                        if afterCursor && textMatches file offset query.Text then
                            results.Add(materialize file offset)

                        heads[chosen] <- scan.Next()

            List.ofSeq results)

    /// Matching entries, newest first.
    member this.Search(query: LogQuery) = this.Collect(query, Newest)

    /// Matching entries after the given cursor, oldest first. Used to tail.
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
