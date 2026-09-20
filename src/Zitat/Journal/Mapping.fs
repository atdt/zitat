namespace Zitat.Journal

#nowarn 9 // Native pointer reads are confined to Mapping.

open System
open System.Buffers.Binary
open System.IO
open System.IO.MemoryMappedFiles
open Microsoft.FSharp.NativeInterop

/// Reads a journal file through a read-only memory map. Offsets are absolute
/// from the start of the file. Every read checks its bounds because journal
/// files can be truncated or corrupt.
[<Sealed>]
type Mapping
    private (path: string, mapped: MemoryMappedFile, view: MemoryMappedViewAccessor, length: int64)
    =
    let handle = view.SafeMemoryMappedViewHandle

    let start =
        let mutable pointer = NativePtr.nullPtr<byte>
        handle.AcquirePointer &pointer
        NativePtr.toNativeInt pointer + nativeint view.PointerOffset

    let mutable released = false

    static member Open(path: string) =
        let length = FileInfo(path).Length

        if length < Format.HeaderMinimumSize then
            raise (CorruptJournal(path, $"file is %d{length} bytes, shorter than a journal header"))

        let mapped =
            MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                null,
                0L,
                MemoryMappedFileAccess.Read
            )

        let view = mapped.CreateViewAccessor(0L, length, MemoryMappedFileAccess.Read)
        new Mapping(path, mapped, view, length)

    member _.Path = path

    /// File size at map time. JournalSet remaps the file when it grows.
    member _.Length = length

    member private _.Address(offset: int64, size: int64) =
        if offset < 0L || size < 0L || offset > length - size then
            raise (
                CorruptJournal(
                    path,
                    $"read of %d{size} bytes at offset %d{offset} lies outside the %d{length} byte file"
                )
            )

        start + nativeint offset

    member this.ReadUInt64(offset: int64) : uint64 =
        let address = this.Address(offset, 8L)
        BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan<byte>(address.ToPointer(), 8))

    member this.ReadUInt32(offset: int64) : uint32 =
        let address = this.Address(offset, 4L)
        BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(address.ToPointer(), 4))

    member this.ReadByte(offset: int64) : byte =
        let address = this.Address(offset, 1L)
        NativePtr.read (NativePtr.ofNativeInt<byte> address)

    /// The span becomes invalid when the mapping is disposed or remapped.
    member this.Span(offset: int64, size: int) : ReadOnlySpan<byte> =
        let address = this.Address(offset, int64 size)
        ReadOnlySpan<byte>(address.ToPointer(), size)

    member this.ToArray(offset: int64, size: int) : byte[] = this.Span(offset, size).ToArray()

    interface IDisposable with
        member _.Dispose() =
            if not released then
                released <- true
                handle.ReleasePointer()
                view.Dispose()
                mapped.Dispose()
