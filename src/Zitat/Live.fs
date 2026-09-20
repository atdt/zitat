namespace Zitat

open System
open System.Collections.Concurrent
open System.Threading.Channels

type LiveSubscription =
    {
        Reader: ChannelReader<unit>
        Dispose: unit -> unit
    }

type LiveHub() =
    let subscribers = ConcurrentDictionary<Guid, Channel<unit>>()

    member _.Notify() =
        for subscriber in subscribers.Values do
            subscriber.Writer.TryWrite(()) |> ignore

    member _.Subscribe() =
        // Notifications coalesce; subscribers replay entries from the journal.
        let options = BoundedChannelOptions(1)
        options.FullMode <- BoundedChannelFullMode.DropOldest
        options.SingleReader <- true
        let channel = Channel.CreateBounded<unit>(options)
        let id = Guid.NewGuid()
        subscribers[id] <- channel

        {
            Reader = channel.Reader
            Dispose =
                fun () ->
                    match subscribers.TryRemove id with
                    | true, removed -> removed.Writer.TryComplete() |> ignore
                    | _ -> ()
        }
