namespace Zitat

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Zitat.Journal

/// Tails the journal directory and publishes new entries to subscribers.
///
/// Unlike the in-process channel this replaces, the position is a journal
/// cursor, so a reconnecting client resumes exactly where it stopped instead
/// of having to re-run its historical query.
type JournalFollower
    (
        options: ZitatOptions,
        set: JournalSet,
        reader: JournalReader,
        live: LiveHub,
        logger: ILogger<JournalFollower>
    ) =
    inherit BackgroundService()

    let changed = new SemaphoreSlim(0, 1)

    let signal () =
        if changed.CurrentCount = 0 then
            try changed.Release() |> ignore with :? SemaphoreFullException -> ()

    override _.ExecuteAsync(token: CancellationToken) =
        task {
            use watcher =
                new FileSystemWatcher(options.JournalDirectory, "*.journal", IncludeSubdirectories = true)

            watcher.NotifyFilter <- NotifyFilters.Size ||| NotifyFilters.LastWrite ||| NotifyFilters.FileName
            watcher.Changed.Add(fun _ -> signal ())
            watcher.Created.Add(fun _ -> signal ())
            watcher.Error.Add(fun error -> logger.LogWarning(error.GetException(), "journal watch failed"))
            watcher.EnableRaisingEvents <- true

            // Start at the newest entry so subscribers see what arrives from
            // now on, not the whole retained history.
            let mutable cursor =
                reader.Search { Query.empty with Limit = 1 }
                |> List.tryHead
                |> Option.map _.Cursor

            let mutable floor = DateTimeOffset.UtcNow

            while not token.IsCancellationRequested do
                try
                    set.Refresh()

                    let query =
                        match cursor with
                        | Some position -> { Query.empty with Before = Some position; Limit = 1000 }
                        | None -> { Query.empty with Since = Some floor; Limit = 1000 }

                    for entry in reader.Forward query do
                        live.Publish entry
                        cursor <- Some entry.Cursor
                        floor <- entry.Realtime
                with
                | :? OperationCanceledException -> ()
                | error -> logger.LogError(error, "journal tail failed")

                try
                    let! _ = changed.WaitAsync(options.TailInterval, token)
                    ()
                with :? OperationCanceledException ->
                    ()
        }
        :> Task
