namespace Zitat

open Falco.Extensions
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Zitat.Journal

module Program =
    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)
        // Under systemd this switches the host to Type=notify readiness and
        // prefixes log lines with journal priorities. It is a no-op elsewhere.
        builder.Host.UseSystemd() |> ignore
        let options = Configuration.load builder.Configuration

        builder.Services.AddSingleton(options) |> ignore

        builder.Services.AddSingleton<JournalSet>(fun services ->
            let logger = services.GetRequiredService<ILoggerFactory>().CreateLogger "Zitat.Journal"
            let set = new JournalSet(options.JournalDirectory, fun message -> logger.LogWarning message)
            // Serve the first request against a populated set rather than an
            // empty one.
            set.Refresh()
            set)
        |> ignore

        builder.Services.AddSingleton<JournalReader>() |> ignore
        builder.Services.AddSingleton<LiveHub>() |> ignore
        builder.Services.AddHostedService<JournalFollower>() |> ignore

        let app = builder.Build()
        app.UseDefaultFiles() |> ignore
        app.UseStaticFiles() |> ignore
        app.UseRouting() |> ignore

        let reader = app.Services.GetRequiredService<JournalReader>()
        let live = app.Services.GetRequiredService<LiveHub>()
        app.UseFalco(Web.endpoints reader live) |> ignore
        app.Run()
        0
