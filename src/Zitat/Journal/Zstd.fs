namespace Zitat.Journal

open System
open System.Threading

/// Journal payloads above 512 bytes are stored compressed; journal-remote
/// defaults to zstd. Decompressor instances are stateful, so each thread keeps
/// its own.
module Zstd =
    let private decompressor =
        new ThreadLocal<ZstdSharp.Decompressor>(fun () -> new ZstdSharp.Decompressor())

    let decompress (source: ReadOnlySpan<byte>) : byte[] =
        decompressor.Value.Unwrap(source, Format.MaxPayloadSize).ToArray()
