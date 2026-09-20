namespace Zitat

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Zitat.Journal

/// Refreshes the journal file set and notifies live subscribers after each scan.
type JournalFollower
    (options: ZitatOptions, set: JournalSet, live: LiveHub, logger: ILogger<JournalFollower>) =
    inherit BackgroundService()

    let changed = new SemaphoreSlim(0, 1)
    // FileSystemWatcher can miss notifications; poll to recover.
    let pollInterval = TimeSpan.FromSeconds 1.

    let signal () =
        if changed.CurrentCount = 0 then
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

            watcher.Error.Add(fun error ->
                logger.LogWarning(error.GetException(), "journal watch failed"))

            watcher.EnableRaisingEvents <- true

            while not token.IsCancellationRequested do
                try
                    set.Refresh()
                    live.Notify()
                with
                | :? OperationCanceledException -> ()
                | error -> logger.LogError(error, "journal refresh failed")

                try
                    let! _ = changed.WaitAsync(pollInterval, token)
                    ()
                with :? OperationCanceledException ->
                    ()
        }
        :> Task
