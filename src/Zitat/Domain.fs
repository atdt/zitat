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

/// Severity counts down from 0, so "error and worse" is AtMost 3.
type NumericFilter =
    | Exactly of int
    | AtMost of int
    | AtLeast of int

type LogQuery = {
    Text: string option
    Hostname: string option
    Application: string option
    SourceAddress: string option
    Facility: int option
    Severity: NumericFilter option
    Since: DateTimeOffset option
    Until: DateTimeOffset option
    BeforeId: int64 option
    Limit: int
}

