namespace Zitat

open System
open System.Text.Json
open System.Text.Json.Serialization.Metadata
open Microsoft.AspNetCore.Http
open Zitat.Json
open Zitat.Journal

module WebJson =
    let private nullableInt value =
        match value with
        | Some number -> Nullable number
        | None -> Nullable()

    let private nullableTime value =
        match value with
        | Some instant -> Nullable instant
        | None -> Nullable()

    let private entryDto (entry: LogEntry) =
        LogEntryDto(
            entry.Cursor,
            entry.Realtime,
            entry.Source,
            Option.toObj entry.Hostname,
            Option.toObj entry.Application,
            Option.toObj entry.Unit,
            Option.toObj entry.ProcessId,
            Option.toObj entry.BootId,
            nullableInt entry.Facility,
            nullableInt entry.Severity,
            entry.Message,
            entry.Fields
            |> List.map (fun (name, value) -> [| name; value |])
            |> List.toArray
        )

    let private write (typeInfo: JsonTypeInfo<'T>) (payload: 'T) (context: HttpContext) =
        context.Response.ContentType <- "application/json; charset=utf-8"

        JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            typeInfo,
            context.RequestAborted
        )

    let logPage items next liveAfter liveSince since until context =
        let page =
            LogPageDto(
                items |> List.map entryDto |> List.toArray,
                Option.toObj next,
                Option.toObj liveAfter,
                liveSince,
                nullableTime since,
                nullableTime until
            )

        write JsonContext.Default.LogPageDto page context

    let status (reader: JournalReader) context =
        let journal = reader.Status()

        let payload =
            StatusDto(
                JournalStatusDto(
                    journal.bytes,
                    journal.directory,
                    journal.entries,
                    journal.files,
                    nullableTime journal.newest,
                    nullableTime journal.oldest,
                    journal.sources |> List.toArray
                ),
                "ok"
            )

        write JsonContext.Default.StatusDto payload context

    let entry (value: LogEntry) =
        JsonSerializer.Serialize(entryDto value, JsonContext.Default.LogEntryDto)
