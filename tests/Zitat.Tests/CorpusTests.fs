namespace Zitat.Tests

open System
open System.IO
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Zitat
open Zitat.Journal

/// The journal tree in this repository contains synthetic entries. Hostnames use
/// `iron`, `cobalt`, and `nickel`. IP addresses use the RFC 5737 documentation
/// range `203.0.113.0/24`.
module Corpus =
    let path = Path.Combine(__SOURCE_DIRECTORY__, "Corpus", "journal")

module CorpusTests =
    let private withReader (test: JournalReader -> unit) =
        use set = new JournalSet(Corpus.path, NullLogger.Instance)
        set.Refresh()
        test (JournalReader set)

    [<Theory>]
    [<InlineData(true)>]
    [<InlineData(false)>]
    let ``refresh discovers a replacement journal of the same size`` (archive: bool) =
        let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory directory |> ignore

        try
            let corpus = Path.Combine(Corpus.path, "1887c08eb8464b0998290f8fd4bd0b6e")

            let original =
                Directory.EnumerateFiles(corpus, "system@*-000000000000045d-*.journal")
                |> Seq.exactlyOne

            let replacement = Path.Combine(corpus, "system.journal")
            Assert.Equal(FileInfo(original).Length, FileInfo(replacement).Length)

            let path = Path.Combine(directory, "system.journal")
            File.Copy(original, path)

            use set = new JournalSet(directory, NullLogger.Instance)
            set.Refresh()
            let reader = JournalReader set
            let anchor = reader.Search Query.empty |> Assert.Single

            if archive then
                File.Move(path, Path.Combine(directory, "system@rotated.journal"))
            else
                File.Delete path

            File.Copy(replacement, path)
            set.Refresh()

            use fresh = new JournalSet(directory, NullLogger.Instance)
            fresh.Refresh()
            let expected = JournalReader fresh
            let page = expected.Search Query.empty
            Assert.Equal((if archive then 5 else 4), page.Length)

            Assert.Equal<string list>(
                page |> List.map _.Cursor,
                reader.Search Query.empty |> List.map _.Cursor
            )

            let query =
                { Query.empty with
                    Before = Some anchor.Cursor
                }

            let forward = expected.Forward query
            Assert.Equal(4, forward.Length)

            Assert.Equal<string list>(
                forward |> List.map _.Cursor,
                reader.Forward query |> List.map _.Cursor
            )
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    let ``indexed filters see a hash table initialized after the file is opened`` () =
        let directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
        Directory.CreateDirectory directory |> ignore

        try
            let original =
                Directory.EnumerateFiles(Corpus.path, "*.journal", SearchOption.AllDirectories)
                |> Seq.head

            let path = Path.Combine(directory, "system.journal")
            File.Copy(original, path)

            use writer =
                new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)

            writer.Position <- 104L
            let table = Array.zeroCreate<byte> 16
            writer.ReadExactly table
            writer.Position <- 104L
            writer.Write(Array.zeroCreate<byte> 16)
            writer.Flush()

            use set = new JournalSet(directory, NullLogger.Instance)
            set.Refresh()
            let reader = JournalReader set
            let page = reader.Search { Query.empty with Limit = 1000 }
            Assert.NotEmpty page
            let application = page |> List.pick _.Application

            writer.Position <- 104L
            writer.Write table
            writer.Flush()

            let query =
                { Query.empty with
                    Application = Some application
                    Limit = 10
                }

            let expected =
                page |> List.filter (Query.matches query) |> List.truncate query.Limit

            Assert.NotEmpty expected

            Assert.Equal<string list>(
                expected |> List.map _.Cursor,
                reader.Search query |> List.map _.Cursor
            )
        finally
            Directory.Delete(directory, true)

    [<Fact>]
    let ``every field resolves to the object the entry references`` () =
        use set = new JournalSet(Corpus.path, NullLogger.Instance)
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

                            Assert.Equal(
                                ValueSome dataOffset,
                                file.FindData(ReadOnlySpan<byte> payload)
                            )

                            count <- count + 1

                count)

        Assert.True(checkedFields > 1000, $"only %d{checkedFields} fields were checked")

    [<Fact>]
    let ``results are newest first across every file`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 500 }
            Assert.NotEmpty page

            page
            |> List.pairwise
            |> List.iter (fun (newer, older) -> Assert.True(newer.Realtime >= older.Realtime)))

    [<Fact>]
    let ``paging visits each entry once`` () =
        withReader (fun reader ->
            let seen = Collections.Generic.HashSet<string>()
            let mutable before = None
            let mutable previous = DateTimeOffset.MaxValue

            for _ in 1..8 do
                let page =
                    reader.Search
                        { Query.empty with
                            Before = before
                            Limit = 100
                        }

                for entry in page do
                    Assert.True(seen.Add entry.Cursor, "an entry was returned on two pages")
                    Assert.True(entry.Realtime <= previous)
                    previous <- entry.Realtime

                before <- page |> List.tryLast |> Option.map _.Cursor

            Assert.True(seen.Count > 500))

    [<Fact>]
    let ``the indexed host filter agrees with filtering a scan`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 3000 }

            match
                page |> List.choose _.Hostname |> List.countBy id |> List.sortByDescending snd
            with
            | [] -> ()
            | (host, _) :: _ ->
                let scanned =
                    page
                    |> List.filter (fun entry -> entry.Hostname = Some host)
                    |> List.truncate 25

                let indexed =
                    reader.Search
                        { Query.empty with
                            Hostname = Some host
                            Limit = 25
                        }

                Assert.Equal<string list>(
                    scanned |> List.map _.Cursor,
                    indexed |> List.map _.Cursor
                ))

    [<Fact>]
    let ``severity filters admit only the severities asked for`` () =
        withReader (fun reader ->
            reader.Search
                { Query.empty with
                    Severity = Some(AtMost 3)
                    Limit = 200
                }
            |> List.iter (fun entry ->
                match entry.Severity with
                | Some value -> Assert.True(value <= 3, $"severity %d{value} passed a <=3 filter")
                | None -> failwith "an entry matched a severity filter without a severity"))

    [<Fact>]
    let ``text search matches the message it claims to`` () =
        withReader (fun reader ->
            let hits =
                reader.Search
                    { Query.empty with
                        Text = Some "systemd"
                        Limit = 50
                    }

            hits
            |> List.iter (fun entry ->
                Assert.Contains("systemd", entry.Message, StringComparison.OrdinalIgnoreCase)))

    [<Fact>]
    let ``tailing forward retraces the page that walked backward`` () =
        withReader (fun reader ->
            let page = reader.Search { Query.empty with Limit = 60 }
            let anchor = page |> List.item 40

            let forward =
                reader.Forward
                    { Query.empty with
                        Before = Some anchor.Cursor
                        Limit = 40
                    }
                |> List.map _.Cursor

            let backward = page |> List.take 40 |> List.rev |> List.map _.Cursor
            Assert.Equal<string list>(backward, forward))

    [<Fact>]
    let ``forward replay crosses the reader page limit`` () =
        withReader (fun reader ->
            let recent = reader.Search { Query.empty with Limit = 1000 }

            let older =
                reader.Search
                    { Query.empty with
                        Before = recent |> List.last |> fun entry -> Some entry.Cursor
                        Limit = 201
                    }

            Assert.Equal(201, older.Length)
            let anchor = older |> List.last

            let first =
                reader.Forward
                    { Query.empty with
                        Before = Some anchor.Cursor
                        Limit = 1000
                    }

            let second =
                reader.Forward
                    { Query.empty with
                        Before = first |> List.last |> fun entry -> Some entry.Cursor
                        Limit = 1000
                    }

            let expected = (older |> List.take 200 |> List.rev) @ (recent |> List.rev)
            let actual = first @ second
            Assert.Equal<string list>(expected |> List.map _.Cursor, actual |> List.map _.Cursor))

    [<Fact>]
    let ``a time range excludes everything outside it`` () =
        withReader (fun reader ->
            let newest = reader.Search({ Query.empty with Limit = 1 }) |> List.head
            let since = newest.Realtime.AddHours -6.0
            let until = newest.Realtime.AddHours -1.0

            reader.Search
                { Query.empty with
                    Since = Some since
                    Until = Some until
                    Limit = 300
                }
            |> List.iter (fun entry -> Assert.InRange(entry.Realtime, since, until)))

    [<Fact>]
    let ``since excludes an entry one tick before the bound`` () =
        withReader (fun reader ->
            let newest = reader.Search({ Query.empty with Limit = 1 }) |> List.head

            let later =
                reader.Search
                    { Query.empty with
                        Since = Some(newest.Realtime.AddTicks 1L)
                        Limit = 1
                    }

            Assert.Empty later)

    [<Fact>]
    let ``until excludes an entry at the bound`` () =
        withReader (fun reader ->
            let newest = reader.Search({ Query.empty with Limit = 1 }) |> List.head

            let atBound =
                reader.Search
                    { Query.empty with
                        Since = Some newest.Realtime
                        Until = Some newest.Realtime
                        Limit = 1
                    }

            let beforeBound =
                reader.Search
                    { Query.empty with
                        Since = Some newest.Realtime
                        Until = Some(newest.Realtime.AddTicks 1L)
                        Limit = 1
                    }

            Assert.Empty atBound
            Assert.Equal(newest.Cursor, beforeBound.Head.Cursor))

    [<Fact>]
    let ``maximum cursor timestamp does not wrap the search bound`` () =
        withReader (fun reader ->
            let expected = reader.Search { Query.empty with Limit = 10 }
            let cursor = Cursor.encode (Array.zeroCreate 16) 0UL UInt64.MaxValue

            let actual =
                reader.Search
                    { Query.empty with
                        Before = Some cursor
                        Limit = 10
                    }

            Assert.Equal<string list>(expected |> List.map _.Cursor, actual |> List.map _.Cursor))
