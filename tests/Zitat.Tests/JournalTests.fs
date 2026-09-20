namespace Zitat.Tests

open System
open Xunit
open Zitat.Journal

module JournalTests =
    [<Fact>]
    let ``cursors round-trip`` () =
        let seqnumId = Array.init 16 (fun index -> byte (index * 7))
        let encoded = Cursor.encode seqnumId 4242UL 1789568880276665UL

        match Cursor.decode encoded with
        | Some(id, seqnum, realtime) ->
            Assert.Equal<byte[]>(seqnumId, id)
            Assert.Equal(4242UL, seqnum)
            Assert.Equal(1789568880276665UL, realtime)
        | None -> failwith "cursor did not decode"

    [<Fact>]
    let ``cursors are URL safe`` () =
        let encoded = Cursor.encode (Array.create 16 0xffuy) UInt64.MaxValue UInt64.MaxValue
        Assert.DoesNotContain("+", encoded)
        Assert.DoesNotContain("/", encoded)
        Assert.DoesNotContain("=", encoded)

    [<Fact>]
    let ``malformed cursors are rejected rather than thrown`` () =
        Assert.Equal(None, Cursor.decode "not a cursor")
        Assert.Equal(None, Cursor.decode "")
        Assert.Equal(None, Cursor.decode "AAAA")

    [<Theory>]
    // systemd-journal-remote names a file after its sender and appends an
    // @-suffix on rotation.
    [<InlineData("/var/log/journal/remote/remote-100.91.171.10.journal", "100.91.171.10")>]
    [<InlineData("/var/log/journal/remote/remote-100.91.171.10@25a7-0000-0006.journal", "100.91.171.10")>]
    [<InlineData("/var/log/journal/abc123/system.journal", "system")>]
    [<InlineData("/var/log/journal/abc123/system@8a31-0001-0006.journal", "system")>]
    [<InlineData("/var/log/journal/abc123/user-1000.journal", "user-1000")>]
    let ``source is derived from the file name`` (path: string) (expected: string) =
        Assert.Equal(expected, Source.ofPath path)
