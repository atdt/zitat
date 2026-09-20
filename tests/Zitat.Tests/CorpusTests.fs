namespace Zitat.Tests

open System
open System.IO
open Xunit
open Zitat
open Zitat.Journal

/// Tests against a real journal tree. The corpus is a copy of a live host's
/// /var/log/journal and is too large, and too personal, to keep in the
/// repository, so these skip when it is absent. See README for how to supply
/// one.
module Corpus =
    let path =
        let rec search (directory: DirectoryInfo option) =
            match directory with
            | None -> None
            | Some current ->
                let candidate = Path.Combine(current.FullName, "testdata", "journal")

                if Directory.Exists candidate then
                    Some candidate
                else
                    search (Option.ofObj current.Parent)

        search (Some(DirectoryInfo(Directory.GetCurrentDirectory())))

/// Marks a test that needs the corpus, and skips it when there is none.
type CorpusFactAttribute() as this =
    inherit FactAttribute()

    do
        if Corpus.path.IsNone then
            this.Skip <- "no journal corpus under testdata/journal"

module CorpusTests =
    let private withReader (test: JournalReader -> unit) =
        match Corpus.path with
        | None -> ()
        | Some root ->
            use set = new JournalSet(root, ignore)
            set.Refresh()
            test (JournalReader set)

    /// The strongest check available without a second implementation: every
    /// field of every sampled entry is looked up by its own recomputed hash and
    /// must resolve to the very object the entry pointed at. It exercises
    /// siphash, the hash table, compact offsets and decompression at once.
    [<CorpusFact>]
    let ``every field resolves to the object the entry references`` () =
        match Corpus.path with
        | None -> ()
        | Some root ->
            use set = new JournalSet(root, ignore)
            set.Refresh()

            let checkedFields =
                set.Use(fun files ->
                    let mutable count = 0

                    for file in files do
                        let chain = file.GlobalChain()

                        for index in 0L .. min 200L (chain.Count - 1L) do
                            let entry = chain[index]

                            for item in 0L .. file.EntryItemCount entry - 1L do
                                let dataOffset = file.EntryItem(entry, item)
                                let payload = file.DataPayload dataOffset
                                Assert.Equal(ValueSome dataOffset, file.FindData(ReadOnlySpan<byte> payload))
                                count <- count + 1

                    count)

            Assert.True(checkedFields > 1000, $"only %d{checkedFields} fields were checked")

    [<CorpusFact>]
    let ``results are newest first across every file`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 500 }
            Assert.NotEmpty page

            page
            |> List.pairwise
            |> List.iter (fun (newer, older) -> Assert.True(newer.Realtime >= older.Realtime)))

    [<CorpusFact>]
    let ``paging visits each entry once`` () =
        withReader (fun reader ->
            let seen = Collections.Generic.HashSet<string>()
            let mutable before = None
            let mutable previous = DateTimeOffset.MaxValue

            for _ in 1..8 do
                let page = reader.Search { Query.empty with Before = before; Limit = 100 }

                for entry in page do
                    Assert.True(seen.Add entry.Cursor, "an entry was returned on two pages")
                    Assert.True(entry.Realtime <= previous)
                    previous <- entry.Realtime

                before <- page |> List.tryLast |> Option.map _.Cursor

            Assert.True(seen.Count > 500))

    /// The index must agree with the naive interpretation of the same filter.
    [<CorpusFact>]
    let ``the indexed host filter agrees with filtering a scan`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 3000 }

            match page |> List.choose _.Hostname |> List.countBy id |> List.sortByDescending snd with
            | [] -> ()
            | (host, _) :: _ ->
                let scanned =
                    page |> List.filter (fun entry -> entry.Hostname = Some host) |> List.truncate 25

                let indexed = reader.Search { Query.empty with Hostname = Some host; Limit = 25 }
                Assert.Equal<string list>(scanned |> List.map _.Cursor, indexed |> List.map _.Cursor))

    [<CorpusFact>]
    let ``severity filters admit only the severities asked for`` () =
        withReader (fun reader ->
            reader.Search { Query.empty with Severity = Some(AtMost 3); Limit = 200 }
            |> List.iter (fun entry ->
                match entry.Severity with
                | Some value -> Assert.True(value <= 3, $"severity %d{value} passed a <=3 filter")
                | None -> failwith "an entry matched a severity filter without a severity"))

    [<CorpusFact>]
    let ``text search matches the message it claims to`` () =
        withReader (fun reader ->
            let hits = reader.Search { Query.empty with Text = Some "systemd"; Limit = 50 }

            hits
            |> List.iter (fun entry ->
                Assert.Contains("systemd", entry.Message, StringComparison.OrdinalIgnoreCase)))

    [<CorpusFact>]
    let ``tailing forward retraces the page that walked backward`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 60 }
            let anchor = page |> List.item 40

            let forward =
                reader.Forward { Query.empty with Before = Some anchor.Cursor; Limit = 40 }
                |> List.map _.Cursor

            let backward = page |> List.take 40 |> List.rev |> List.map _.Cursor
            Assert.Equal<string list>(backward, forward))

    [<CorpusFact>]
    let ``a time range excludes everything outside it`` () =
        withReader (fun reader ->
            let newest = reader.Search({ Query.empty with Limit = 1 }) |> List.head
            let since = newest.Realtime.AddHours -6.0
            let until = newest.Realtime.AddHours -1.0

            reader.Search { Query.empty with Since = Some since; Until = Some until; Limit = 300 }
            |> List.iter (fun entry ->
                Assert.InRange(entry.Realtime, since, until)))
