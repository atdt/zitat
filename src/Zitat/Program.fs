namespace Zitat

open Falco.Extensions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

module Program =
    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        // Under systemd this switches the host to Type=notify readiness and
        // prefixes log lines with journal priorities. It is a no-op elsewhere.
        builder.Host.UseSystemd() |> ignore
        let options = Configuration.load builder.Configuration
        let database = Database(options.DatabasePath)
        database.Initialize()

        builder.Services.AddSingleton(options) |> ignore
        builder.Services.AddSingleton(database) |> ignore
        builder.Services.AddSingleton<IngestMetrics>() |> ignore
        builder.Services.AddSingleton<IngestSink>() |> ignore
        builder.Services.AddSingleton<LiveHub>() |> ignore
        builder.Services.AddHostedService<StorageWriter>() |> ignore
        builder.Services.AddHostedService<UdpIngestion>() |> ignore
        builder.Services.AddHostedService<TcpIngestion>() |> ignore
        builder.Services.AddHostedService<Maintenance>() |> ignore

        let app = builder.Build()
        app.UseDefaultFiles() |> ignore
        app.UseStaticFiles() |> ignore
        app.UseRouting() |> ignore

        let live = app.Services.GetRequiredService<LiveHub>()
        let metrics = app.Services.GetRequiredService<IngestMetrics>()
        app.UseFalco(Web.endpoints database live metrics) |> ignore
        app.Run()
        0
