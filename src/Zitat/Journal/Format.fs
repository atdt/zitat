namespace Zitat.Journal

open System

/// Structural constants of the journal file format, as declared in
/// systemd's src/libsystemd/sd-journal/journal-def.h.
module Format =
    [<Literal>]
    let Signature = 0x48524848534b504cUL // "LPKSHHRH", little-endian

    [<Literal>]
    let HeaderMinimumSize = 200L

    /// Every object begins on an 8-byte boundary and is padded to a multiple
    /// of 8 bytes.
    [<Literal>]
    let Alignment = 8L

    [<Literal>]
    let ObjectHeaderSize = 16L

    module ObjectType =
        [<Literal>]
        let Data = 1uy

        [<Literal>]
        let Field = 2uy

        [<Literal>]
        let Entry = 3uy

        [<Literal>]
        let DataHashTable = 4uy

        [<Literal>]
        let FieldHashTable = 5uy

        [<Literal>]
        let EntryArray = 6uy

        [<Literal>]
        let Tag = 7uy

    /// Field offsets within an ENTRY object. Items begin at EntryItems and are
    /// 4 bytes wide in compact files, 16 bytes otherwise.
    module Entry =
        [<Literal>]
        let Seqnum = 16L

        [<Literal>]
        let Realtime = 24L

        [<Literal>]
        let Monotonic = 32L

        [<Literal>]
        let BootId = 40L

        [<Literal>]
        let XorHash = 56L

        [<Literal>]
        let Items = 64L

    /// Field offsets within a DATA object.
    module Data =
        [<Literal>]
        let Hash = 16L

        [<Literal>]
        let NextHashOffset = 24L

        [<Literal>]
        let NextFieldOffset = 32L

        [<Literal>]
        let EntryOffset = 40L

        [<Literal>]
        let EntryArrayOffset = 48L

        [<Literal>]
        let NEntries = 56L

        /// Compact files store a tail-array shortcut ahead of the payload.
        [<Literal>]
        let CompactPayload = 72L

        [<Literal>]
        let RegularPayload = 64L

    /// Field offsets within an ENTRY_ARRAY object.
    module EntryArray =
        [<Literal>]
        let NextOffset = 16L

        [<Literal>]
        let Items = 24L

    /// A DATA_HASH_TABLE cell: head and tail offsets of a collision chain.
    [<Literal>]
    let HashItemSize = 16L

    /// systemd's DATA_SIZE_MAX. A larger decompressed payload means a corrupt
    /// or hostile frame rather than a log line.
    [<Literal>]
    let MaxPayloadSize = 768 * 1024 * 1024

[<Flags>]
type IncompatibleFlags =
    | None = 0u
    | CompressedXz = 1u
    | CompressedLz4 = 2u
    | KeyedHash = 4u
    | CompressedZstd = 8u
    | Compact = 16u

[<Flags>]
type CompatibleFlags =
    | None = 0u
    | Sealed = 1u
    | TailEntryBootId = 2u
    | SealedContinuous = 4u

/// Payload compression, taken from the low bits of an object header's flags.
type Compression =
    | Uncompressed
    | Zstd
    | Xz
    | Lz4

type FileState =
    | Offline
    | Online
    | Archived
    | UnknownState of byte

/// Raised for any file this reader will not interpret. The caller is expected
/// to surface it rather than fall back: a file carrying flags we do not
/// implement cannot be read correctly, only incorrectly.
exception UnsupportedJournal of path: string * reason: string

exception CorruptJournal of path: string * reason: string
