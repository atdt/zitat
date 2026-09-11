namespace Zitat

open System

type LogEntry = {
    Id: int64
    ReceivedAt: DateTimeOffset
    SentAt: DateTimeOffset option
    Hostname: string option
    Application: string option
    ProcessId: string option
    Facility: int option
    Severity: int option
    Message: string
    SourceAddress: string
    RawMessage: string
}

type PendingLogEntry = {
    ReceivedAt: DateTimeOffset
    SentAt: DateTimeOffset option
    Hostname: string option
    Application: string option
    ProcessId: string option
    Facility: int option
    Severity: int option
    Message: string
    SourceAddress: string
    RawMessage: string
}

type LogQuery = {
    Text: string option
    Hostname: string option
    Application: string option
    Facility: int option
    Severity: int option
    Since: DateTimeOffset option
    Until: DateTimeOffset option
    BeforeId: int64 option
    Limit: int
}

