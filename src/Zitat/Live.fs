namespace Zitat

open System
open System.Collections.Concurrent
open System.Threading.Channels

type LiveSubscription = {
    Reader: ChannelReader<LogEntry>
    Dispose: unit -> unit
}

type LiveHub() =
    let subscribers = ConcurrentDictionary<Guid, Channel<LogEntry>>()

    member _.Publish(entry: LogEntry) =
        for subscriber in subscribers.Values do
            subscriber.Writer.TryWrite entry |> ignore

    member _.Subscribe() =
        let options = BoundedChannelOptions(1000)
        options.FullMode <- BoundedChannelFullMode.DropOldest
        options.SingleReader <- true
        let channel = Channel.CreateBounded<LogEntry>(options)
        let id = Guid.NewGuid()
        subscribers[id] <- channel

        {
            Reader = channel.Reader
            Dispose = fun () ->
                match subscribers.TryRemove id with
                | true, removed -> removed.Writer.TryComplete() |> ignore
                | _ -> ()
        }
