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

    // Delete the share of the records that the overage represents. A fixed
    // batch empties the whole store whenever the limit is small relative to
    // the batch.
    let batchSize (size: int64) =
        let excess = size - options.MaxStorageBytes
        let share = 1L + database.Count * excess / size
        int (min 5000L share)

    // Each pass must reclaim space. Without that condition a size limit the
    // files can never meet deletes every stored message.
    let shrinkToLimit () =
        let mutable deleted = 0
        let mutable shrinking = true

        while shrinking && database.SizeBytes > options.MaxStorageBytes do
            let before = database.SizeBytes
            let removed = database.DeleteOldest(batchSize before)
            database.Compact()
            deleted <- deleted + removed
            shrinking <- removed > 0 && database.SizeBytes < before

        deleted

    let run () =
        let expired = database.DeleteBefore(DateTimeOffset.UtcNow - options.Retention)
        if expired > 0 then logger.LogInformation("Deleted {Count} expired log messages", expired)

        database.Compact()
        let deleted = shrinkToLimit ()

        if deleted > 0 then
            logger.LogWarning(
                "Deleted {Count} oldest log messages to satisfy the storage limit",
                deleted
            )

        if database.SizeBytes > options.MaxStorageBytes then
            logger.LogError(
                "Storage is {Bytes} bytes and cannot be reduced below the {Limit} byte limit",
                database.SizeBytes,
                options.MaxStorageBytes
            )

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            while not stoppingToken.IsCancellationRequested do
                try run ()
                with error -> logger.LogError(error, "Log retention failed")

                do! Task.Delay(TimeSpan.FromMinutes 1.0, stoppingToken)
        }
