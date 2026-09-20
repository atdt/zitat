namespace Zitat.Tests

open System
open Xunit
open Zitat

module QueryTests =
    let private entry =
        {
            Cursor = "cursor"
            Realtime = DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)
            Source = "100.91.171.10"
            Hostname = Some "imp"
            Application = Some "sshd"
            Unit = Some "ssh.service"
            ProcessId = Some "42"
            BootId = Some "7296587132114e25afcf42a4f4e6f6bf"
            Facility = Some 4
            Severity = Some 6
            Message = "Accepted publickey for ori"
            Fields = []
        }

    [<Fact>]
    let ``text syntax separates filters from terms`` () =
        let result =
            Query.parseText "host:imp app:sshd severity:5 \"publickey for ori\"" Query.empty

        Assert.Equal(Some "imp", result.Hostname)
        Assert.Equal(Some "sshd", result.Application)
        Assert.Equal(Some(Exactly 5), result.Severity)
        Assert.Equal(Some "publickey for ori", result.Text)

    [<Fact>]
    let ``text syntax reads the journal-specific filters`` () =
        let result =
            Query.parseText "unit:ssh.service boot:abc123 source:100.71.212.2" Query.empty

        Assert.Equal(Some "ssh.service", result.Unit)
        Assert.Equal(Some "abc123", result.BootId)
        Assert.Equal(Some "100.71.212.2", result.Source)
        Assert.Equal(None, result.Text)

    [<Fact>]
    let ``severity accepts comparison operators`` () =
        let parse text =
            (Query.parseText text Query.empty).Severity

        Assert.Equal(Some(AtMost 3), parse "severity:<=3")
        Assert.Equal(Some(AtMost 2), parse "severity:<3")
        Assert.Equal(Some(AtLeast 4), parse "severity:>=4")
        Assert.Equal(Some(AtLeast 5), parse "severity:>4")
        Assert.Equal(Some(Exactly 3), parse "severity:3")

    [<Fact>]
    let ``severity accepts standard names`` () =
        let parse text =
            (Query.parseText text Query.empty).Severity

        Assert.Equal(Some(Exactly 3), parse "severity:err")
        Assert.Equal(Some(AtMost 4), parse "severity:<=warning")
        Assert.Equal(Some(Exactly 7), parse "severity:debug")

    [<Fact>]
    let ``facility accepts standard names`` () =
        Assert.Equal(Some 4, (Query.parseText "facility:auth" Query.empty).Facility)
        Assert.Equal(Some 3, (Query.parseText "facility:daemon" Query.empty).Facility)
        Assert.Equal(Some 17, (Query.parseText "facility:17" Query.empty).Facility)

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

    /// Exact-value filters go through the journal's hash index, which matches
    /// bytes. In-memory filtering has to agree with that or the live stream
    /// would show entries a search cannot find.
    [<Fact>]
    let ``exact filters are case sensitive`` () =
        Assert.True(
            Query.matches
                { Query.empty with
                    Hostname = Some "imp"
                }
                entry
        )

        Assert.False(
            Query.matches
                { Query.empty with
                    Hostname = Some "IMP"
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
