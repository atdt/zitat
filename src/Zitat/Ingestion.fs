namespace Zitat

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type IngestMetrics() =
    let mutable received = 0L
    let mutable stored = 0L
    let mutable floodDropped = 0L
    let mutable queueDropped = 0L
    let mutable storageFailed = 0L
    let mutable malformed = 0L

    member _.Received = Interlocked.Read(&received)
    member _.Stored = Interlocked.Read(&stored)
    member _.FloodDropped = Interlocked.Read(&floodDropped)
    member _.QueueDropped = Interlocked.Read(&queueDropped)
    member _.StorageFailed = Interlocked.Read(&storageFailed)
    member _.Malformed = Interlocked.Read(&malformed)
    member _.IncrementReceived() = Interlocked.Increment(&received) |> ignore
    member _.IncrementStored() = Interlocked.Increment(&stored) |> ignore
    member _.IncrementFloodDropped() = Interlocked.Increment(&floodDropped) |> ignore
    member _.IncrementQueueDropped() = Interlocked.Increment(&queueDropped) |> ignore
    member _.IncrementStorageFailed() = Interlocked.Increment(&storageFailed) |> ignore
    member _.IncrementMalformed() = Interlocked.Increment(&malformed) |> ignore

type private Bucket(rate: float, capacity: float) =
    let gate = obj ()
    let mutable tokens = capacity
    let mutable last = Stopwatch.GetTimestamp()

    member _.Take() =
        lock gate (fun () ->
            let now = Stopwatch.GetTimestamp()
            let elapsed = float (now - last) / float Stopwatch.Frequency
            last <- now
            tokens <- min capacity (tokens + elapsed * rate)

            if tokens >= 1.0 then
                tokens <- tokens - 1.0
                true
            else
                false)

type IngestSink(options: ZitatOptions, metrics: IngestMetrics) =
    let channelOptions = BoundedChannelOptions(10_000)
    do channelOptions.FullMode <- BoundedChannelFullMode.Wait
    do channelOptions.SingleReader <- true

    let channel = Channel.CreateBounded<PendingLogEntry>(channelOptions)
    let buckets = ConcurrentDictionary<string, Bucket>()

    member _.Reader = channel.Reader

    member this.Submit(source: string, raw: string) =
        if not (String.IsNullOrWhiteSpace raw) then this.Accept(source, raw)

    member private _.Accept(source: string, raw: string) =
        metrics.IncrementReceived()
        let bucket =
            buckets.GetOrAdd(
                source,
                fun _ -> Bucket(options.FloodMessagesPerSecond, options.FloodBurst)
            )

        if not (bucket.Take()) then
            metrics.IncrementFloodDropped()
        else
            let entry = Syslog.parse DateTimeOffset.UtcNow source raw
            if entry.Facility.IsNone then metrics.IncrementMalformed()
            if not (channel.Writer.TryWrite entry) then metrics.IncrementQueueDropped()

type StorageWriter(
    database: Database,
    sink: IngestSink,
    live: LiveHub,
    metrics: IngestMetrics,
    logger: ILogger<StorageWriter>
) =
    inherit BackgroundService()

    let drain () =
        let mutable item = Unchecked.defaultof<PendingLogEntry>
        while sink.Reader.TryRead(&item) do
            try
                let stored = database.Insert item
                metrics.IncrementStored()
                live.Publish stored
            with error ->
                metrics.IncrementStorageFailed()
                logger.LogError(error, "Failed to store a syslog message")

    override _.ExecuteAsync(stoppingToken) =
        task {
            try
                while not stoppingToken.IsCancellationRequested do
                    let! available = sink.Reader.WaitToReadAsync(stoppingToken)
                    if available then drain ()
            with :? OperationCanceledException -> ()

            // Whatever is still queued was already accepted from the network.
            // The inner loop usually empties it before cancellation is
            // observed, so this pass matters only when the loop exits with a
            // backlog: a cancelled wait that raced a write, or a future change
            // to the order hosted services stop in.
            drain ()
        }

module private SocketHelpers =
    let listenerSocket socketType protocol port =
        let socket = new Socket(AddressFamily.InterNetworkV6, socketType, protocol)
        socket.DualMode <- true
        socket.Bind(IPEndPoint(IPAddress.IPv6Any, port))
        socket

    let source (endpoint: EndPoint | null) =
        match Option.ofObj endpoint with
        | Some (:? IPEndPoint as ip) when ip.Address.IsIPv4MappedToIPv6 ->
            ip.Address.MapToIPv4().ToString()
        | Some (:? IPEndPoint as ip) -> ip.Address.ToString()
        | Some value -> value.ToString() |> Option.ofObj |> Option.defaultValue "unknown"
        | None -> "unknown"

type UdpIngestion(options: ZitatOptions, sink: IngestSink, logger: ILogger<UdpIngestion>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken) =
        task {
            use socket =
                SocketHelpers.listenerSocket
                    SocketType.Dgram
                    ProtocolType.Udp
                    options.UdpPort
            logger.LogInformation("Listening for UDP syslog on port {Port}", options.UdpPort)
            let buffer = Array.zeroCreate<byte> options.MaxMessageBytes
            let mutable endpoint: EndPoint = IPEndPoint(IPAddress.IPv6Any, 0)

            while not stoppingToken.IsCancellationRequested do
                let! result =
                    socket.ReceiveFromAsync(
                        buffer,
                        SocketFlags.None,
                        endpoint,
                        stoppingToken
                    )
                let raw = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes)
                sink.Submit(SocketHelpers.source result.RemoteEndPoint, raw)
        }

type private FrameDecoder(maxBytes: int) =
    let buffer = ResizeArray<byte>()

    let newlineFrame () =
        match buffer.IndexOf(byte '\n') with
        | -1 -> None
        | index ->
            let count = if index > 0 && buffer[index - 1] = byte '\r' then index - 1 else index
            let frame = buffer.GetRange(0, count).ToArray()
            buffer.RemoveRange(0, index + 1)
            Some frame

    // RFC 6587 octet counting: decimal length, one space, then a frame that
    // always opens with the priority. Anything else is newline framing, even
    // when it starts with a digit.
    let octetLength () =
        let space = buffer.IndexOf(byte ' ')
        let counted =
            space > 0
            && space <= 10
            && buffer.Count > space + 1
            && buffer[space + 1] = byte '<'

        if not counted then
            None
        else
            let prefix = Encoding.ASCII.GetString(buffer.GetRange(0, space).ToArray())
            match Int32.TryParse prefix with
            | true, length when length >= 0 && length <= maxBytes -> Some(space, length)
            | _ -> None

    let octetFrame (space, length) =
        if buffer.Count < space + 1 + length then None
        else
            let frame = buffer.GetRange(space + 1, length).ToArray()
            buffer.RemoveRange(0, space + 1 + length)
            Some frame

    let nextFrame () =
        match octetLength () with
        | Some counted -> octetFrame counted
        | None -> newlineFrame ()

    member _.Feed(bytes: byte array, count: int) =
        for index in 0 .. count - 1 do buffer.Add(bytes[index])
        if buffer.Count > maxBytes * 2 then buffer.Clear()

        let frames = ResizeArray<string>()
        let mutable reading = true
        while reading do
            match nextFrame () with
            | Some value -> frames.Add(Encoding.UTF8.GetString value)
            | None -> reading <- false
        List.ofSeq frames

type TcpIngestion(options: ZitatOptions, sink: IngestSink, logger: ILogger<TcpIngestion>) =
    inherit BackgroundService()

    let handle (socket: Socket) (stoppingToken: CancellationToken) =
        task {
            use client = socket
            let source = SocketHelpers.source client.RemoteEndPoint
            let decoder = FrameDecoder(options.MaxMessageBytes)
            let bytes = Array.zeroCreate<byte> 8192
            let mutable connected = true

            while connected && not stoppingToken.IsCancellationRequested do
                let! count = client.ReceiveAsync(bytes, SocketFlags.None, stoppingToken)
                if count = 0 then connected <- false
                else
                    for frame in decoder.Feed(bytes, count) do sink.Submit(source, frame)
        }

    override _.ExecuteAsync(stoppingToken) =
        task {
            use listener =
                SocketHelpers.listenerSocket
                    SocketType.Stream
                    ProtocolType.Tcp
                    options.TcpPort
            listener.Listen(128)
            logger.LogInformation("Listening for TCP syslog on port {Port}", options.TcpPort)

            while not stoppingToken.IsCancellationRequested do
                let! socket = listener.AcceptAsync(stoppingToken)
                handle socket stoppingToken
                |> fun work -> work.ContinueWith(
                    (fun (finished: Task) ->
                        if finished.IsFaulted then
                            logger.LogWarning(finished.Exception, "TCP syslog connection failed")),
                    CancellationToken.None
                )
                |> ignore
        }
