namespace Zitat

open System
open System.Buffers
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Zitat.Journal

/// Responses are written field by field rather than serialized from a type.
/// Reflection over F# types is what a trimmed publish cannot see, and
/// System.Text.Json's source generator runs only on C# compilations.
module WebJson =
    /// Named fields keep the six `/api/logs` response values from being
    /// transposed at the call site.
    type LogPage =
        {
            Items: LogEntry list
            NextBefore: string option
            LiveAfter: string option
            LiveSince: DateTimeOffset
            EffectiveSince: DateTimeOffset option
            EffectiveUntil: DateTimeOffset option
        }

    let private optionalText (writer: Utf8JsonWriter) (name: string) value =
        match value with
        | Some(text: string) -> writer.WriteString(name, text)
        | None -> writer.WriteNull name

    let private optionalNumber (writer: Utf8JsonWriter) (name: string) value =
        match value with
        | Some(number: int) -> writer.WriteNumber(name, number)
        | None -> writer.WriteNull name

    let private optionalTime (writer: Utf8JsonWriter) (name: string) value =
        match value with
        | Some(instant: DateTimeOffset) -> writer.WriteString(name, instant)
        | None -> writer.WriteNull name

    let private writeEntry (writer: Utf8JsonWriter) (entry: LogEntry) =
        writer.WriteStartObject()
        writer.WriteString("cursor", entry.Cursor)
        writer.WriteString("realtime", entry.Realtime)
        writer.WriteString("source", entry.Source)
        optionalText writer "hostname" entry.Hostname
        optionalText writer "application" entry.Application
        optionalText writer "unit" entry.Unit
        optionalText writer "processId" entry.ProcessId
        optionalText writer "bootId" entry.BootId
        optionalNumber writer "facility" entry.Facility
        optionalNumber writer "severity" entry.Severity
        writer.WriteString("message", entry.Message)
        writer.WriteStartArray "fields"

        for name, value in entry.Fields do
            writer.WriteStartArray()
            writer.WriteStringValue(name: string)
            writer.WriteStringValue(value: string)
            writer.WriteEndArray()

        writer.WriteEndArray()
        writer.WriteEndObject()

    let private write (writing: Utf8JsonWriter -> unit) (context: HttpContext) : Task =
        context.Response.ContentType <- "application/json; charset=utf-8"

        task {
            use writer = new Utf8JsonWriter(context.Response.Body)
            writing writer
            do! writer.FlushAsync context.RequestAborted
        }

    let writeLogPage (page: LogPage) context =
        write
            (fun writer ->
                writer.WriteStartObject()
                writer.WriteStartArray "items"

                for entry in page.Items do
                    writeEntry writer entry

                writer.WriteEndArray()
                optionalText writer "nextBefore" page.NextBefore
                optionalText writer "liveAfter" page.LiveAfter
                writer.WriteString("liveSince", page.LiveSince)
                optionalTime writer "effectiveSince" page.EffectiveSince
                optionalTime writer "effectiveUntil" page.EffectiveUntil
                writer.WriteEndObject())
            context

    let writeStatus (reader: JournalReader) context =
        let journal = reader.Status()

        write
            (fun writer ->
                writer.WriteStartObject()
                writer.WriteStartObject "journal"
                writer.WriteNumber("bytes", journal.bytes)
                writer.WriteString("directory", journal.directory)
                writer.WriteNumber("entries", journal.entries)
                writer.WriteNumber("files", journal.files)
                optionalTime writer "newest" journal.newest
                optionalTime writer "oldest" journal.oldest
                writer.WriteStartArray "sources"

                for source in journal.sources do
                    writer.WriteStringValue(source: string)

                writer.WriteEndArray()
                writer.WriteEndObject()
                writer.WriteString("status", "ok")
                writer.WriteEndObject())
            context

    /// The SSE frame in `Web.stream` needs the entry as text, not as a
    /// response body of its own.
    let serializeEntry (entry: LogEntry) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writeEntry writer entry
        writer.Flush()
        Encoding.UTF8.GetString buffer.WrittenSpan
