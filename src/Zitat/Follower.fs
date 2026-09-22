namespace Zitat

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Zitat.Journal

type JournalFollower
    (options: ZitatOptions, set: JournalSet, live: LiveHub, logger: ILogger<JournalFollower>) =
    inherit BackgroundService()

    let changed = new SemaphoreSlim(0, 1)

    // inotify does not report writes made through a memory mapping, and
    // journald appends that way, so new entries arrive without an event.
    // Reading the tails finds them without a directory scan or remapping.
    let tailInterval = TimeSpan.FromMilliseconds 200.

    // Only a new, vanished, or grown file needs a Refresh, and the watcher
    // reports those. Polling covers the events it drops.
    let refreshInterval = TimeSpan.FromSeconds 1.

    let sinceRefresh = Stopwatch.StartNew()
    let mutable refreshNeeded = true
    let mutable tails: TailSnapshot list = []

    let refreshIfDue () =
        if refreshNeeded || sinceRefresh.Elapsed >= refreshInterval then
            refreshNeeded <- false
            sinceRefresh.Restart()
            set.Refresh()

    let notifyIfTailsChanged () =
        let current = set.Tails()

        if current <> tails then
            tails <- current
            live.Notify()

    let signal () =
        try
            changed.Release() |> ignore
        with :? SemaphoreFullException ->
            ()

    override _.ExecuteAsync(token: CancellationToken) =
        task {
            use watcher =
                new FileSystemWatcher(
                    options.JournalDirectory,
                    "*.journal",
                    IncludeSubdirectories = true
                )

            watcher.NotifyFilter <-
                NotifyFilters.Size ||| NotifyFilters.LastWrite ||| NotifyFilters.FileName

            watcher.Changed.Add(fun _ -> signal ())
            watcher.Created.Add(fun _ -> signal ())
            watcher.Deleted.Add(fun _ -> signal ())
            // Rotation renames the active file before creating its replacement.
            watcher.Renamed.Add(fun _ -> signal ())

            watcher.Error.Add(fun error ->
                logger.LogWarning(error.GetException(), "journal watch failed"))

            watcher.EnableRaisingEvents <- true

            while not token.IsCancellationRequested do
                try
                    refreshIfDue ()
                    notifyIfTailsChanged ()
                with
                | :? OperationCanceledException -> ()
                | error -> logger.LogError(error, "journal follow failed")

                try
                    let! signalled = changed.WaitAsync(tailInterval, token)
                    refreshNeeded <- signalled
                with :? OperationCanceledException ->
                    ()
        }
        :> Task
