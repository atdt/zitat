using System;
using System.Text.Json.Serialization;

namespace Zitat.Json;

public sealed record LogEntryDto(
    string Cursor,
    DateTimeOffset Realtime,
    string Source,
    string? Hostname,
    string? Application,
    string? Unit,
    string? ProcessId,
    string? BootId,
    int? Facility,
    int? Severity,
    string Message,
    string[][] Fields);

public sealed record LogPageDto(
    LogEntryDto[] Items,
    string? NextBefore,
    string? LiveAfter,
    DateTimeOffset LiveSince,
    DateTimeOffset? EffectiveSince,
    DateTimeOffset? EffectiveUntil);

public sealed record JournalStatusDto(
    long Bytes,
    string Directory,
    long Entries,
    int Files,
    DateTimeOffset? Newest,
    DateTimeOffset? Oldest,
    string[] Sources);

public sealed record StatusDto(JournalStatusDto Journal, string Status);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogEntryDto))]
[JsonSerializable(typeof(LogPageDto))]
[JsonSerializable(typeof(StatusDto))]
public partial class JsonContext : JsonSerializerContext { }
