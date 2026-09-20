namespace Zitat.Tests

open System
open System.IO
open System.Threading
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Zitat

module IngestionTests =
    let private options = {
        DatabasePath = "unused"
        UdpPort = 0
        TcpPort = 0
        Retention = TimeSpan.FromDays 1.0
        MaxStorageBytes = 1L
        MaxMessageBytes = 1024
        FloodMessagesPerSecond = 1_000_000.0
        FloodBurst = 1_000_000.0
    }

    [<Fact>]
    let ``full ingest queue increments the drop counter`` () =
        let metrics = IngestMetrics()
        let sink = IngestSink(options, metrics)

        for _ in 1 .. 10_001 do
            sink.Submit("source", "message")

        Assert.Equal(10_001L, metrics.Received)
        Assert.Equal(1L, metrics.QueueDropped)

    [<Fact>]
    let ``source flood increments the drop counter`` () =
        let metrics = IngestMetrics()
        let limited =
            { options with
                FloodMessagesPerSecond = 0.0001
                FloodBurst = 1.0 }
        let sink = IngestSink(limited, metrics)

        sink.Submit("source", "first")
        sink.Submit("source", "second")

        Assert.Equal(1L, metrics.FloodDropped)

    [<Fact>]
    let ``stopping stores a queued backlog`` () =
        task {
            let directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            let database = Database(Path.Combine(directory, "test.db"))
            database.Initialize()

            try
                let metrics = IngestMetrics()
                let sink = IngestSink(options, metrics)
                let writer =
                    new StorageWriter(
                        database,
                        sink,
                        LiveHub(),
                        metrics,
                        NullLogger<StorageWriter>.Instance
                    )

                do! writer.StartAsync CancellationToken.None
                for index in 1 .. 500 do
                    sink.Submit("source", $"<14>message {index}")
                do! writer.StopAsync CancellationToken.None

                Assert.Equal(500L, metrics.Stored)
                Assert.Equal(500L, database.Count)
            finally
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                Directory.Delete(directory, true)
        }
