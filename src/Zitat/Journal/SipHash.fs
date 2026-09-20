namespace Zitat.Journal

open System
open System.Buffers.Binary

/// SipHash-2-4, keyed by the journal file's 128-bit file_id. Journal files
/// written since systemd 246 set HEADER_INCOMPATIBLE_KEYED_HASH and hash every
/// DATA and FIELD payload this way; the reader recomputes the hash to locate
/// objects in the hash tables.
module SipHash =

    let inline private rotl (value: uint64) (bits: int) =
        (value <<< bits) ||| (value >>> (64 - bits))

    let inline private round (v0: byref<uint64>) (v1: byref<uint64>) (v2: byref<uint64>) (v3: byref<uint64>) =
        v0 <- v0 + v1
        v1 <- rotl v1 13
        v1 <- v1 ^^^ v0
        v0 <- rotl v0 32
        v2 <- v2 + v3
        v3 <- rotl v3 16
        v3 <- v3 ^^^ v2
        v0 <- v0 + v3
        v3 <- rotl v3 21
        v3 <- v3 ^^^ v0
        v2 <- v2 + v1
        v1 <- rotl v1 17
        v1 <- v1 ^^^ v2
        v2 <- rotl v2 32

    /// `key` must be 16 bytes.
    let hash (key: ReadOnlySpan<byte>) (data: ReadOnlySpan<byte>) =
        let k0 = BinaryPrimitives.ReadUInt64LittleEndian key
        let k1 = BinaryPrimitives.ReadUInt64LittleEndian (key.Slice 8)

        let mutable v0 = k0 ^^^ 0x736f6d6570736575UL
        let mutable v1 = k1 ^^^ 0x646f72616e646f6dUL
        let mutable v2 = k0 ^^^ 0x6c7967656e657261UL
        let mutable v3 = k1 ^^^ 0x7465646279746573UL

        let blocks = data.Length / 8

        for index in 0 .. blocks - 1 do
            let m = BinaryPrimitives.ReadUInt64LittleEndian (data.Slice(index * 8, 8))
            v3 <- v3 ^^^ m
            round &v0 &v1 &v2 &v3
            round &v0 &v1 &v2 &v3
            v0 <- v0 ^^^ m

        // The final block is the remaining bytes, zero-padded, with the total
        // input length in the most significant byte.
        let mutable tail = (uint64 data.Length &&& 0xffUL) <<< 56
        let remainder = data.Slice(blocks * 8)

        for index in 0 .. remainder.Length - 1 do
            tail <- tail ||| (uint64 remainder[index] <<< (8 * index))

        v3 <- v3 ^^^ tail
        round &v0 &v1 &v2 &v3
        round &v0 &v1 &v2 &v3
        v0 <- v0 ^^^ tail

        v2 <- v2 ^^^ 0xffUL
        round &v0 &v1 &v2 &v3
        round &v0 &v1 &v2 &v3
        round &v0 &v1 &v2 &v3
        round &v0 &v1 &v2 &v3

        v0 ^^^ v1 ^^^ v2 ^^^ v3
