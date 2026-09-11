namespace Zitat.Tests

open System
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
