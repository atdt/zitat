namespace Zitat.Journal

open System
open System.Text

[<Struct>]
type private Segment =
    {
        Start: int64
        Items: int64
        Count: int64
    }

/// ENTRY_ARRAY chains store offsets from oldest to newest.
/// Appended arrays double in capacity, limiting a chain to O(log n) segments.
[<Sealed>]
type EntryArrayChain
    (mapping: Mapping, compact: bool, head: int64, first: int64 voption, total: int64) =

    let width = if compact then 4L else 8L
    let inlineCount = if first.IsSome then 1L else 0L

    let segments =
        lazy
            let found = ResizeArray<Segment>()
            let mutable remaining = total - inlineCount
            let mutable index = inlineCount
            let mutable arrayOffset = head

            while arrayOffset <> 0L && remaining > 0L do
                let size = int64 (mapping.ReadUInt64(arrayOffset + 8L))
                let capacity = (size - Format.EntryArray.Items) / width

                if capacity <= 0L then
                    raise (
                        CorruptJournalException(
                            mapping.Path,
                            $"entry array at %d{arrayOffset} holds no items"
                        )
                    )

                let count = min capacity remaining

                found.Add
                    {
                        Start = index
                        Items = arrayOffset + Format.EntryArray.Items
                        Count = count
                    }

                index <- index + count
                remaining <- remaining - count

                arrayOffset <-
                    int64 (mapping.ReadUInt64(arrayOffset + Format.EntryArray.NextOffset))

            // Only use entries reachable through the chain.
            found.ToArray()

    let count =
        lazy (inlineCount + (segments.Value |> Array.sumBy (fun segment -> segment.Count)))

    /// The number of entries reachable through the chain.
    member _.Count = count.Value

    member _.Item
        with get (index: int64): int64 =
            match first with
            | ValueSome entry when index = 0L -> entry
            | _ ->
                let found = segments.Value
                let mutable low = 0
                let mutable high = found.Length - 1
                let mutable segment = -1

                while low <= high do
                    let mid = (low + high) / 2

                    if found[mid].Start <= index then
                        segment <- mid
                        low <- mid + 1
                    else
                        high <- mid - 1

                if segment < 0 then
                    raise (
                        CorruptJournalException(
                            mapping.Path,
                            $"entry index %d{index} is outside the chain"
                        )
                    )

                let slot = found[segment]

                if index - slot.Start >= slot.Count then
                    raise (
                        CorruptJournalException(
                            mapping.Path,
                            $"entry index %d{index} is outside the chain"
                        )
                    )

                let position = slot.Items + (index - slot.Start) * width

                if compact then
                    int64 (mapping.ReadUInt32 position)
                else
                    int64 (mapping.ReadUInt64 position)

    /// Returns the first index with an offset at least `target`, or Count.
    member this.LowerBoundOffset(target: int64) = this.LowerBound(uint64, uint64 target)

    /// Returns the first index with a key at least `target`, or Count.
    member this.LowerBound(keyOf: int64 -> uint64, target: uint64) =
        let mutable low = 0L
        let mutable high = this.Count

        while low < high do
            let mid = low + (high - low) / 2L

            if keyOf this[mid] < target then
                low <- mid + 1L
            else
                high <- mid

        low

/// Read dynamic header fields on each access.
[<Sealed>]
type JournalFile private (mapping: Mapping) =
    let path = mapping.Path

    let compatible =
        LanguagePrimitives.EnumOfValue<uint32, CompatibleFlags>(mapping.ReadUInt32 8L)

    let incompatible =
        LanguagePrimitives.EnumOfValue<uint32, IncompatibleFlags>(mapping.ReadUInt32 12L)

    let compact = incompatible.HasFlag IncompatibleFlags.Compact
    let headerSize = int64 (mapping.ReadUInt64 88L)
    let fileId = mapping.ToArray(24L, 16)
    let seqnumId = mapping.ToArray(72L, 16)
    let dataHashTableOffset = int64 (mapping.ReadUInt64 104L)
    let dataHashTableSize = int64 (mapping.ReadUInt64 112L)

    let entryItemWidth = if compact then 4L else 16L

    let dataPayloadOffset =
        if compact then
            Format.Data.CompactPayload
        else
            Format.Data.RegularPayload

    /// Reject files with unknown incompatible flags.
    static let supported =
        IncompatibleFlags.KeyedHash
        ||| IncompatibleFlags.Compact
        ||| IncompatibleFlags.CompressedZstd

    static member Open(path: string) =
        let mapping = Mapping.Open path

        try
            if mapping.ReadUInt64 0L <> Format.Signature then
                raise (CorruptJournalException(path, "missing LPKSHHRH signature"))

            let incompatible =
                LanguagePrimitives.EnumOfValue<uint32, IncompatibleFlags>(mapping.ReadUInt32 12L)

            let unknown = incompatible &&& ~~~supported

            if unknown <> IncompatibleFlags.None then
                raise (
                    UnsupportedJournalException(
                        path,
                        $"incompatible flags %A{unknown} are not implemented (file declares %A{incompatible})"
                    )
                )

            if not (incompatible.HasFlag IncompatibleFlags.KeyedHash) then
                raise (
                    UnsupportedJournalException(
                        path,
                        "file predates keyed hashing and would need the Jenkins lookup3 hash"
                    )
                )

            let headerSize = int64 (mapping.ReadUInt64 88L)

            if headerSize < Format.HeaderMinimumSize then
                raise (
                    CorruptJournalException(path, $"header of %d{headerSize} bytes is too short")
                )

            new JournalFile(mapping)
        with _ ->
            (mapping :> IDisposable).Dispose()
            reraise ()

    member _.Path = path
    member _.FileId = fileId
    member _.SeqnumId = seqnumId
    member _.IsCompact = compact
    member _.CompatibleFlags = compatible
    member _.IncompatibleFlags = incompatible

    member _.MappedLength = mapping.Length

    member _.State =
        match mapping.ReadByte 16L with
        | 0uy -> Offline
        | 1uy -> Online
        | 2uy -> Archived
        | other -> UnknownState other

    member _.EntryCount = int64 (mapping.ReadUInt64 152L)
    member _.HeadRealtime = mapping.ReadUInt64 184L
    member _.TailRealtime = mapping.ReadUInt64 192L

    member private _.CheckObject(offset: int64, expected: byte) =
        if offset < headerSize || offset % Format.Alignment <> 0L then
            raise (
                CorruptJournalException(
                    path,
                    $"object offset %d{offset} is unaligned or inside the header"
                )
            )

        let actual = mapping.ReadByte offset

        if actual <> expected then
            raise (
                CorruptJournalException(
                    path,
                    $"expected object type %d{expected} at %d{offset}, found %d{actual}"
                )
            )

    member private _.ObjectSize(offset: int64) = int64 (mapping.ReadUInt64(offset + 8L))

    member private _.Compression(offset: int64) =
        match mapping.ReadByte(offset + 1L) &&& 0b111uy with
        | 0uy -> Uncompressed
        | 1uy -> Xz
        | 2uy -> Lz4
        | 4uy -> Zstd
        | other ->
            raise (
                CorruptJournalException(
                    path,
                    $"object at %d{offset} has compression bits %d{other}"
                )
            )

    member this.DataPayload(offset: int64) : byte[] =
        this.CheckObject(offset, Format.ObjectType.Data)
        let size = this.ObjectSize offset
        let length = int (size - dataPayloadOffset)

        if length < 0 then
            raise (
                CorruptJournalException(
                    path,
                    $"data object at %d{offset} is smaller than its header"
                )
            )

        match this.Compression offset with
        | Uncompressed -> mapping.ToArray(offset + dataPayloadOffset, length)
        | Zstd -> Zstd.decompress (mapping.Span(offset + dataPayloadOffset, length))
        | other ->
            raise (
                UnsupportedJournalException(
                    path,
                    $"data object at %d{offset} uses %A{other} compression"
                )
            )

    member _.EntryRealtime(offset: int64) =
        mapping.ReadUInt64(offset + Format.Entry.Realtime)

    member _.EntrySeqnum(offset: int64) =
        mapping.ReadUInt64(offset + Format.Entry.Seqnum)

    member _.EntryBootId(offset: int64) =
        mapping.ToArray(offset + Format.Entry.BootId, 16)

    member this.EntryItemCount(offset: int64) =
        (this.ObjectSize offset - Format.Entry.Items) / entryItemWidth

    member _.EntryItem(offset: int64, index: int64) =
        let slot = offset + Format.Entry.Items + index * entryItemWidth

        if compact then
            int64 (mapping.ReadUInt32 slot)
        else
            int64 (mapping.ReadUInt64 slot)

    /// Indexed filters resolve a DATA object once, then test entry offsets
    /// without decoding each entry's payloads.
    member this.EntryReferences(entryOffset: int64, dataOffset: int64) =
        let count = this.EntryItemCount entryOffset
        let mutable index = 0L
        let mutable found = false

        while not found && index < count do
            if this.EntryItem(entryOffset, index) = dataOffset then
                found <- true

            index <- index + 1L

        found

    /// Compares uncompressed prefixes in the mapping. Compressed payloads must
    /// be decoded before comparison.
    member this.PayloadStartsWith(dataOffset: int64, prefix: ReadOnlySpan<byte>) =
        let length = int (this.ObjectSize dataOffset - dataPayloadOffset)

        match this.Compression dataOffset with
        | Uncompressed ->
            length >= prefix.Length
            && mapping.Span(dataOffset + dataPayloadOffset, prefix.Length).SequenceEqual prefix
        | _ ->
            let payload = this.DataPayload dataOffset

            payload.Length >= prefix.Length
            && ReadOnlySpan<byte>(payload, 0, prefix.Length).SequenceEqual prefix

    /// `prefix` is the field name with its trailing `=`.
    member this.EntryField(entryOffset: int64, prefix: byte[]) : byte[] voption =
        let count = this.EntryItemCount entryOffset
        let mutable index = 0L
        let mutable result = ValueNone

        while result.IsNone && index < count do
            let dataOffset = this.EntryItem(entryOffset, index)

            if this.PayloadStartsWith(dataOffset, ReadOnlySpan<byte> prefix) then
                let payload = this.DataPayload dataOffset
                result <- ValueSome payload[prefix.Length ..]

            index <- index + 1L

        result

    member this.EntryFields(entryOffset: int64) =
        this.CheckObject(entryOffset, Format.ObjectType.Entry)
        let count = this.EntryItemCount entryOffset

        Array.init (int count) (fun index ->
            this.DataPayload(this.EntryItem(entryOffset, int64 index)))

    member this.GlobalChain() =
        EntryArrayChain(
            mapping,
            compact,
            int64 (mapping.ReadUInt64 176L),
            ValueNone,
            this.EntryCount
        )

    /// The DATA object stores its first entry inline, before its entry array.
    member _.DataChain(dataOffset: int64) =
        let inlineEntry = int64 (mapping.ReadUInt64(dataOffset + Format.Data.EntryOffset))

        let arrayHead =
            int64 (mapping.ReadUInt64(dataOffset + Format.Data.EntryArrayOffset))

        let total = int64 (mapping.ReadUInt64(dataOffset + Format.Data.NEntries))

        let first =
            if inlineEntry = 0L then
                ValueNone
            else
                ValueSome inlineEntry

        EntryArrayChain(mapping, compact, arrayHead, first, total)

    member this.FindData(payload: ReadOnlySpan<byte>) : int64 voption =
        if dataHashTableSize <= 0L then
            ValueNone
        else
            let hash = SipHash.hash (ReadOnlySpan<byte> fileId) payload
            let buckets = uint64 (dataHashTableSize / Format.HashItemSize)
            let bucket = int64 (hash % buckets)

            let mutable candidate =
                int64 (mapping.ReadUInt64(dataHashTableOffset + bucket * Format.HashItemSize))

            let mutable result = ValueNone
            let mutable guard = 0

            while result.IsNone && candidate <> 0L do
                guard <- guard + 1

                if guard > 10_000 then
                    raise (
                        CorruptJournalException(
                            path,
                            $"hash chain in bucket %d{bucket} does not terminate"
                        )
                    )

                if mapping.ReadUInt64(candidate + Format.Data.Hash) = hash then
                    // A matching hash does not rule out a collision.
                    if payload.SequenceEqual(ReadOnlySpan<byte>(this.DataPayload candidate)) then
                        result <- ValueSome candidate

                if result.IsNone then
                    candidate <- int64 (mapping.ReadUInt64(candidate + Format.Data.NextHashOffset))

            result

    member this.FindData(payload: string) =
        this.FindData(ReadOnlySpan<byte>(Encoding.UTF8.GetBytes payload))

    interface IDisposable with
        member _.Dispose() = (mapping :> IDisposable).Dispose()
