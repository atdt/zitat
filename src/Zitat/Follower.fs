namespace Zitat

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Zitat.Journal

/// Reads entries appended to journal files and publishes them to the live hub.
/// The cursor starts at the newest existing entry and advances after publication.
type JournalFollower
    (options: ZitatOptions, set: JournalSet, reader: JournalReader, live: LiveHub, logger: ILogger<JournalFollower>) =
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
                new FileSystemWatcher(options.JournalDirectory, "*.journal", IncludeSubdirectories = true)

            watcher.NotifyFilter <- NotifyFilters.Size ||| NotifyFilters.LastWrite ||| NotifyFilters.FileName
            watcher.Changed.Add(fun _ -> signal ())
            watcher.Created.Add(fun _ -> signal ())
            watcher.Error.Add(fun error -> logger.LogWarning(error.GetException(), "journal watch failed"))
            watcher.EnableRaisingEvents <- true

            let startedAt = DateTimeOffset.UtcNow

            // Start at the newest entry so subscribers see what arrives from
            // now on, not the whole retained history.
            let mutable checkpointCursor =
                reader.Search { Query.empty with Limit = 1 }
                |> List.tryHead
                |> Option.map _.Cursor

            while not token.IsCancellationRequested do
                try
                    set.Refresh()

                    let query =
                        match checkpointCursor with
                        | Some position ->
                            { Query.empty with
                                Before = Some position
                                Limit = 1000
                            }
                        | None ->
                            { Query.empty with
                                Since = Some startedAt
                                Limit = 1000
                            }

                    for entry in reader.Forward query do
                        live.Publish entry
                        checkpointCursor <- Some entry.Cursor
                with
                | :? OperationCanceledException -> ()
                | error -> logger.LogError(error, "journal tail failed")

                try
                    let! _ = changed.WaitAsync(pollInterval, token)
                    ()
                with :? OperationCanceledException ->
                    ()
        }
        :> Task
