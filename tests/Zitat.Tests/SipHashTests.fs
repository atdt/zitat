namespace Zitat.Tests

open System
open Xunit
open Zitat.Journal

module SipHashTests =
    /// The vector systemd's own src/test/test-siphash24.c asserts, which is in
    /// turn the one from the SipHash paper. Journal hash lookups are wrong in
    /// silence if this drifts.
    [<Fact>]
    let ``matches the reference vector`` () =
        let key = Array.init 16 byte
        let input = Array.init 15 byte

        Assert.Equal(0xa129ca6149be45e5UL, SipHash.hash (ReadOnlySpan<byte> key) (ReadOnlySpan<byte> input))

    [<Fact>]
    let ``length is part of the hash`` () =
        let key = Array.init 16 byte
        let hash (bytes: byte[]) = SipHash.hash (ReadOnlySpan<byte> key) (ReadOnlySpan<byte> bytes)

        Assert.NotEqual(hash [||], hash [| 0uy |])
        Assert.NotEqual(hash [| 0uy |], hash [| 0uy; 0uy |])
