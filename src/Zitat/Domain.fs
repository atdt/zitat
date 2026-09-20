namespace Zitat

open System

/// A journal entry projected for the API. Fields retains all journal fields,
/// including underscore-prefixed metadata.
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
