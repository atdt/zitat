namespace Zitat

open System

/// One journal entry, flattened into the fields the interface shows. `Fields`
/// keeps the complete entry, including the trusted `_`-prefixed metadata that
/// a syslog transport would have discarded.
type LogEntry =
    {
        Cursor: string
        Realtime: DateTimeOffset
        Source: string
        Hostname: string option
        Application: string option
        Unit: string option
        ProcessId: string option
        BootId: string option
        Facility: int option
        Severity: int option
        Message: string
        Fields: (string * string) list
    }

/// Severity counts down from 0, so "error and worse" is AtMost 3.
type NumericFilter =
    | Exactly of int
    | AtMost of int
    | AtLeast of int

/// Filters that name an exact field value are served by the journal's hash
/// index and are therefore case-sensitive, matching journalctl. Text is a
/// case-insensitive substring of MESSAGE and is applied per candidate.
type LogQuery =
    {
        Text: string option
        Hostname: string option
        Application: string option
        Unit: string option
        Source: string option
        BootId: string option
        Facility: int option
        Severity: NumericFilter option
        Since: DateTimeOffset option
        Until: DateTimeOffset option
        Before: string option
        Limit: int
    }
