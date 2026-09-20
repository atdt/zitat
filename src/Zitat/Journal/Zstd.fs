namespace Zitat.Journal

open System
open System.Threading

/// Decompressor instances retain state. Each thread uses its own instance.
module Zstd =
    let private decompressor =
        new ThreadLocal<ZstdSharp.Decompressor>(fun () -> new ZstdSharp.Decompressor())

    let decompress (source: ReadOnlySpan<byte>) : byte[] =
        decompressor.Value.Unwrap(source, Format.MaxPayloadSize).ToArray()
