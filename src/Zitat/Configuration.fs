namespace Zitat

open Microsoft.Extensions.Configuration

type ZitatOptions = { JournalDirectory: string }

module Configuration =
    let load (config: IConfiguration) =
        {
            JournalDirectory =
                config["Zitat:JournalDirectory"]
                |> Option.ofObj
                |> Option.defaultValue "/var/log/journal"
        }
