namespace Zitat

open System
open Microsoft.Extensions.Configuration

type ZitatOptions = {
    DatabasePath: string
    UdpPort: int
    TcpPort: int
    Retention: TimeSpan
    MaxStorageBytes: int64
    MaxMessageBytes: int
    FloodMessagesPerSecond: float
    FloodBurst: float
}

module Configuration =
    let private integer (config: IConfiguration) key fallback =
        match Int32.TryParse(config[key]) with
        | true, value -> value
        | _ -> fallback

    let private int64 (config: IConfiguration) key fallback =
        match Int64.TryParse(config[key]) with
        | true, value -> value
        | _ -> fallback

    let private number (config: IConfiguration) key fallback =
        match Double.TryParse(config[key]) with
        | true, value -> value
        | _ -> fallback

    let load (config: IConfiguration) =
        let days = integer config "Zitat:RetentionDays" 14

        {
            DatabasePath = config["Zitat:DatabasePath"] |> Option.ofObj |> Option.defaultValue "zitat.db"
            UdpPort = integer config "Zitat:UdpPort" 5514
            TcpPort = integer config "Zitat:TcpPort" 5514
            Retention = TimeSpan.FromDays(float days)
            MaxStorageBytes = int64 config "Zitat:MaxStorageBytes" 1_073_741_824L
            MaxMessageBytes = integer config "Zitat:MaxMessageBytes" 65_536
            FloodMessagesPerSecond = number config "Zitat:FloodMessagesPerSecond" 500.0
            FloodBurst = number config "Zitat:FloodBurst" 1_000.0
        }

