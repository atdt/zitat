namespace Zitat.Tests

open System
open Xunit
open Zitat

module QueryTests =
    let private parse text =
        match Query.parseText text with
        | Ok query -> query
        | Error error -> failwith error

    let private entry =
        {
            Cursor = "cursor"
            Realtime = DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)
            Source = "203.0.113.10"
            Hostname = Some "iron"
            Application = Some "sshd"
            Unit = Some "ssh.service"
            ProcessId = Some "42"
            BootId = Some "7296587132114e25afcf42a4f4e6f6bf"
            Facility = Some 4
            Severity = Some 6
            Message = "Accepted publickey for admin"
            Fields = []
        }

    [<Fact>]
    let ``text syntax separates filters from terms`` () =
        let result = parse "host:iron app:sshd severity:5 \"publickey for admin\""

        Assert.Equal(Some "iron", result.Hostname)
        Assert.Equal(Some "sshd", result.Application)
        Assert.Equal(Some(Exactly 5), result.Severity)
        Assert.Equal(Some "publickey for admin", result.Text)

    [<Fact>]
    let ``text syntax reads the journal-specific filters`` () =
        let result = parse "unit:ssh.service boot:abc123 source:203.0.113.20"

        Assert.Equal(Some "ssh.service", result.Unit)
        Assert.Equal(Some "abc123", result.BootId)
        Assert.Equal(Some "203.0.113.20", result.Source)
        Assert.Equal(None, result.Text)

    [<Fact>]
    let ``severity accepts comparison operators`` () =
        let parse text = (parse text).Severity

        Assert.Equal(Some(AtMost 3), parse "severity:<=3")
        Assert.Equal(Some(AtMost 2), parse "severity:<3")
        Assert.Equal(Some(AtLeast 4), parse "severity:>=4")
        Assert.Equal(Some(AtLeast 5), parse "severity:>4")
        Assert.Equal(Some(Exactly 3), parse "severity:3")

    [<Fact>]
    let ``severity accepts standard names`` () =
        let parse text = (parse text).Severity

        Assert.Equal(Some(Exactly 3), parse "severity:err")
        Assert.Equal(Some(AtMost 4), parse "severity:<=warning")
        Assert.Equal(Some(Exactly 7), parse "severity:debug")

    [<Fact>]
    let ``facility accepts standard names`` () =
        Assert.Equal(Some 4, (parse "facility:auth").Facility)
        Assert.Equal(Some 3, (parse "facility:daemon").Facility)
        Assert.Equal(Some 17, (parse "facility:17").Facility)

    [<Theory>]
    [<InlineData("facility:unknown")>]
    [<InlineData("facility:24")>]
    [<InlineData("severity:unknown")>]
    [<InlineData("severity:8")>]
    [<InlineData("since:bad")>]
    [<InlineData("since:-1h")>]
    [<InlineData("since:NaNh")>]
    [<InlineData("until:bad")>]
    let ``invalid named filters are rejected`` text =
        Assert.True(Query.parseText text |> Result.isError)

    [<Fact>]
    let ``since accepts a relative duration`` () =
        let before = DateTimeOffset.UtcNow.AddHours(-1.)

        match Query.parseText "since:1.5h" with
        | Ok parsed ->
            Assert.True(parsed.Since.IsSome)
            Assert.InRange(parsed.Since.Value, before.AddMinutes(-31.), before.AddMinutes(-29.))
        | Error error -> failwith error

    [<Fact>]
    let ``since and until accept absolute timestamps`` () =
        let result = parse "since:2026-09-01T00:00:00Z until:2026-09-20T00:00:00Z"

        Assert.Equal(Some(DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)), result.Since)
        Assert.Equal(Some(DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero)), result.Until)

    [<Fact>]
    let ``an empty query matches everything`` () =
        Assert.True(Query.matches Query.empty entry)

    [<Fact>]
    let ``text matching ignores case`` () =
        Assert.True(
            Query.matches
                { Query.empty with
                    Text = Some "ACCEPTED"
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Text = Some "rejected"
                }
                entry
        )

    [<Fact>]
    let ``exact filters are case sensitive`` () =
        Assert.True(
            Query.matches
                { Query.empty with
                    Hostname = Some "iron"
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Hostname = Some "IRON"
                }
                entry
        )

        Assert.True(
            Query.matches
                { Query.empty with
                    Unit = Some "ssh.service"
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Unit = Some "SSH.service"
                }
                entry
        )

    [<Fact>]
    let ``severity comparisons count down from emergency`` () =
        Assert.True(
            Query.matches
                { Query.empty with
                    Severity = Some(AtLeast 6)
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Severity = Some(AtMost 3)
                }
                entry
        )

    [<Fact>]
    let ``the time range is inclusive at both ends`` () =
        Assert.True(
            Query.matches
                { Query.empty with
                    Since = Some entry.Realtime
                }
                entry
        )

        Assert.True(
            Query.matches
                { Query.empty with
                    Until = Some entry.Realtime
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Since = Some(entry.Realtime.AddTicks 1L)
                }
                entry
        )
