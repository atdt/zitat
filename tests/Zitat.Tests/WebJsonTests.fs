namespace Zitat.Tests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open Xunit
open Zitat
open Zitat.Journal

/// The writer calls in WebJson are written by hand, so adding a field to an F#
/// record cannot break the build. These tests compare each record against the
/// keys the API actually emits.
module WebJsonTests =
    let private camelCase (name: string) =
        string (Char.ToLowerInvariant name[0]) + name[1..]

    let private recordKeys (recordType: Type) =
        FSharpType.GetRecordFields recordType
        |> Array.map (fun field -> camelCase field.Name)

    let private keys (element: JsonElement) =
        element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq

    let private written (write: HttpContext -> Task) =
        let context = DefaultHttpContext()
        use body = new MemoryStream()
        context.Response.Body <- body
        (write context).GetAwaiter().GetResult()
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType)
        JsonDocument.Parse(body.ToArray()).RootElement

    let private sample =
        {
            Cursor = "s=1;i=2;b=3;m=4;t=5;x=6"
            Realtime = DateTimeOffset.UnixEpoch
            Source = "system"
            Hostname = Some "iron"
            Application = Some "sshd"
            Unit = Some "ssh.service"
            ProcessId = Some "42"
            BootId = Some "b"
            Facility = Some 4
            Severity = Some 6
            Message = "accepted"
            Fields = [ "_PID", "42" ]
        }

    [<Fact>]
    let ``every LogEntry field is serialized`` () =
        let element = JsonDocument.Parse(WebJson.serializeEntry sample).RootElement
        Assert.Equal<Set<string>>(recordKeys typeof<LogEntry> |> Set.ofArray, keys element)

    [<Fact>]
    let ``fields are serialized as name and value pairs`` () =
        let element = JsonDocument.Parse(WebJson.serializeEntry sample).RootElement
        let pair = element.GetProperty("fields")[0]
        Assert.Equal("_PID", pair[0].GetString())
        Assert.Equal("42", pair[1].GetString())

    [<Fact>]
    let ``every LogPage field is serialized`` () =
        let page: WebJson.LogPage =
            {
                Items = [ sample ]
                NextBefore = Some "next"
                LiveAfter = Some "live"
                LiveSince = DateTimeOffset.UnixEpoch
                EffectiveSince = Some DateTimeOffset.UnixEpoch
                EffectiveUntil = None
            }

        let element = written (WebJson.writeLogPage page)
        Assert.Equal<Set<string>>(recordKeys typeof<WebJson.LogPage> |> Set.ofArray, keys element)
        Assert.Equal("next", element.GetProperty("nextBefore").GetString())
        Assert.Equal("live", element.GetProperty("liveAfter").GetString())
        Assert.Equal(JsonValueKind.Null, element.GetProperty("effectiveUntil").ValueKind)

    [<Fact>]
    let ``every journal status field is serialized`` () =
        use set = new JournalSet(Corpus.path, ignore)
        set.Refresh()
        let reader = JournalReader set
        let element = written (WebJson.writeStatus reader)
        Assert.Equal("ok", element.GetProperty("status").GetString())

        Assert.Equal<Set<string>>(
            recordKeys ((reader.Status() :> obj).GetType()) |> Set.ofArray,
            keys (element.GetProperty "journal")
        )
