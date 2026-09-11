namespace Zitat

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type Maintenance(
    database: Database,
    options: ZitatOptions,
    logger: ILogger<Maintenance>
) =
    inherit BackgroundService()

    let run () =
        let expired = database.DeleteBefore(DateTimeOffset.UtcNow - options.Retention)
        if expired > 0 then logger.LogInformation("Deleted {Count} expired log messages", expired)

        database.Compact()
        let mutable deleted = 0

        while database.SizeBytes > options.MaxStorageBytes && database.Count > 0L do
            deleted <- deleted + database.DeleteOldest(5000)
            database.Compact()

        if deleted > 0 then
            logger.LogWarning(
                "Deleted {Count} oldest log messages to satisfy the storage limit",
                deleted
            )

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try run ()
                with error -> logger.LogError(error, "Log retention failed")

                do! Task.Delay(TimeSpan.FromMinutes 1.0, stoppingToken)
        }
