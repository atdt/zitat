namespace Zitat

open System
open Microsoft.Extensions.Configuration

type ZitatOptions = {
    /// Root of the journal tree to read. systemd-journal-remote writes under
    /// /var/log/journal/remote; the parent covers local logs as well.
    JournalDirectory: string
    /// How often the follower looks for entries when the filesystem reports
    /// no change. Writes normally wake it sooner.
    TailInterval: TimeSpan
}

module Configuration =
    let private integer (config: IConfiguration) key fallback =
        match Int32.TryParse(config[key]) with
        | true, value -> value
        | _ -> fallback

    let load (config: IConfiguration) = {
        JournalDirectory =
            config["Zitat:JournalDirectory"]
            |> Option.ofObj
            |> Option.defaultValue "/var/log/journal"
        TailInterval =
            integer config "Zitat:TailIntervalMilliseconds" 1000
            |> float
            |> TimeSpan.FromMilliseconds
    }
