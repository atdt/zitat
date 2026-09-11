namespace Zitat.Tests

open System
open System.IO
open Xunit
open Zitat

module DatabaseTests =
    let private withDatabase action =
        let directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        let database = Database(Path.Combine(directory, "test.db"))

        try
            database.Initialize()
            action database
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            Directory.Delete(directory, true)

    let private item received message host = {
        ReceivedAt = received
        SentAt = None
        Hostname = host
        Application = Some "test"
        ProcessId = None
        Facility = Some 1
        Severity = Some 5
        Message = message
        SourceAddress = "127.0.0.1"
        RawMessage = message
    }

    [<Fact>]
    let ``inserted logs can be filtered and paged`` () =
        withDatabase (fun database ->
            let now = DateTimeOffset.UtcNow
            let first = database.Insert(item now "first lease" (Some "router"))
            database.Insert(item (now.AddSeconds 1) "second lease" (Some "server"))
            |> ignore

            let query =
                { Query.empty with
                    Hostname = Some "router"
                    Text = Some "lease" }

            let results = database.Search query
            Assert.Single(results) |> ignore
            Assert.Equal(first.Id, results.Head.Id))

    [<Fact>]
    let ``search orders and pages by receive time`` () =
        withDatabase (fun database ->
            let now = DateTimeOffset.UtcNow
            let newer = database.Insert(item now "newer" None)
            database.Insert(item (now.AddSeconds -1) "older" None) |> ignore

            let results = database.Search { Query.empty with Limit = 1 }
            Assert.Equal(newer.Id, results.Head.Id)

            let next =
                database.Search
                    { Query.empty with
                        BeforeId = Some newer.Id
                        Limit = 1 }

            Assert.Equal("older", next.Head.Message))

    [<Fact>]
    let ``severity threshold and source narrow the search`` () =
        withDatabase (fun database ->
            let now = DateTimeOffset.UtcNow
            let at severity =
                { item now $"severity {severity}" (Some "router") with
                    Severity = Some severity }

            for severity in 0 .. 7 do database.Insert(at severity) |> ignore

            let search filter = database.Search { Query.empty with Severity = Some filter }
            Assert.Equal(4, (search (AtMost 3)).Length)
            Assert.Equal(1, (search (Exactly 3)).Length)
            Assert.Equal(3, (search (AtLeast 5)).Length)

            let bySource =
                database.Search { Query.empty with SourceAddress = Some "127.0.0.1" }
            Assert.Equal(8, bySource.Length)

            let missing =
                database.Search { Query.empty with SourceAddress = Some "10.0.0.9" }
            Assert.Empty(missing))

    [<Fact>]
    let ``receive-time retention deletes old logs`` () =
        withDatabase (fun database ->
            let now = DateTimeOffset.UtcNow
            database.Insert(item (now.AddDays -10) "old" None) |> ignore
            database.Insert(item now "new" None) |> ignore

            Assert.Equal(1, database.DeleteBefore(now.AddDays -7))
            Assert.Equal(1L, database.Count))
