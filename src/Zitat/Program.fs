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
        builder.Host.UseSystemd() |> ignore
        let options = Configuration.load builder.Configuration

        builder.Services.AddSingleton(options) |> ignore

        builder.Services.AddSingleton<JournalSet>(fun services ->
            let logger =
                services.GetRequiredService<ILoggerFactory>().CreateLogger "Zitat.Journal"

            let set = new JournalSet(options.JournalDirectory, logger.LogWarning)

            // Load the journal before the host accepts requests.
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
        let lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>()
        app.UseFalco(Web.endpoints lifetime.ApplicationStopping reader live) |> ignore
        app.Run()
        0
