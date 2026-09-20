namespace Zitat

open System

/// Fields keeps every journal field, including underscore-prefixed metadata
/// that has no dedicated API property.
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

/// Journal severity 0 is the most severe. `AtMost 3` includes errors and all
/// more severe entries.
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
