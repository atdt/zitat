namespace Zitat.Tests

open System
open Xunit
open Zitat

module SyslogTests =
    let received = DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)

    [<Fact>]
    let ``RFC 5424 fields are parsed`` () =
        let raw = "<34>1 2003-10-11T22:14:15.003Z host app 8710 ID47 - message"
        let result = Syslog.parse received "100.64.0.1" raw

        Assert.Equal(Some 4, result.Facility)
        Assert.Equal(Some 2, result.Severity)
        Assert.Equal(Some "host", result.Hostname)
        Assert.Equal(Some "app", result.Application)
        Assert.Equal(Some "8710", result.ProcessId)
        Assert.Equal("message", result.Message)

    [<Fact>]
    let ``RFC 5424 structured data is skipped without losing the message`` () =
        let raw =
            "<165>1 2003-10-11T22:14:15.003Z host app - ID47 "
            + "[example@1 key=\"a\\]b\"] payload"
        let result = Syslog.parse received "100.64.0.1" raw

        Assert.Equal("payload", result.Message)
        Assert.Equal(Some 20, result.Facility)
        Assert.Equal(Some 5, result.Severity)

    [<Fact>]
    let ``RFC 3164 fields are parsed`` () =
        let result =
            Syslog.parse received "100.64.0.2" "<13>Sep 11 07:10:00 router dhcpd[42]: lease granted"

        Assert.Equal(Some 1, result.Facility)
        Assert.Equal(Some 5, result.Severity)
        Assert.Equal(Some "router", result.Hostname)
        Assert.Equal(Some "dhcpd", result.Application)
        Assert.Equal(Some "42", result.ProcessId)
        Assert.Equal("lease granted", result.Message)

    [<Fact>]
    let ``malformed input is retained`` () =
        let result = Syslog.parse received "unknown" "not syslog"

        Assert.Equal("not syslog", result.Message)
        Assert.Equal("not syslog", result.RawMessage)
        Assert.Equal(None, result.Severity)

    [<Fact>]
    let ``future legacy timestamp is assigned to the previous year`` () =
        let january = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        let result = Syslog.parse january "source" "<13>Dec 31 23:59:00 host app: old"

        Assert.Equal(2025, result.SentAt.Value.Year)
