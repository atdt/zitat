namespace Zitat.Tests

open Xunit
open Zitat

module QueryTests =
    [<Fact>]
    let ``text syntax separates filters from terms`` () =
        let result =
            Query.parseText
                "host:router app:dhcpd severity:5 \"lease granted\""
                Query.empty

        Assert.Equal(Some "router", result.Hostname)
        Assert.Equal(Some "dhcpd", result.Application)
        Assert.Equal(Some(Exactly 5), result.Severity)
        Assert.Equal(Some "lease granted", result.Text)

    [<Fact>]
    let ``severity accepts comparison operators`` () =
        let parse text = (Query.parseText text Query.empty).Severity

        Assert.Equal(Some(AtMost 3), parse "severity:<=3")
        Assert.Equal(Some(AtMost 2), parse "severity:<3")
        Assert.Equal(Some(AtLeast 4), parse "severity:>=4")
        Assert.Equal(Some(AtLeast 5), parse "severity:>4")
        Assert.Equal(Some(Exactly 3), parse "severity:3")

    [<Fact>]
    let ``source filters on the sending address`` () =
        let result = Query.parseText "source:10.0.0.5 lease" Query.empty

        Assert.Equal(Some "10.0.0.5", result.SourceAddress)
        Assert.Equal(Some "lease", result.Text)

    [<Fact>]
    let ``live matching handles an absent field`` () =
        let entry = {
            Id = 1L
            ReceivedAt = System.DateTimeOffset.UtcNow
            SentAt = None
            Hostname = None
            Application = None
            ProcessId = None
            Facility = None
            Severity = None
            Message = "message"
            SourceAddress = "source"
            RawMessage = "message"
        }

        let query = { Query.empty with Facility = Some 1 }
        Assert.False(Query.matches query entry)

    [<Fact>]
    let ``live matching honors a severity threshold`` () =
        let entry = {
            Id = 1L
            ReceivedAt = System.DateTimeOffset.UtcNow
            SentAt = None
            Hostname = Some "router"
            Application = None
            ProcessId = None
            Facility = Some 1
            Severity = Some 2
            Message = "message"
            SourceAddress = "10.0.0.5"
            RawMessage = "message"
        }

        let severity filter = { Query.empty with Severity = Some filter }
        Assert.True(Query.matches (severity (AtMost 3)) entry)
        Assert.False(Query.matches (severity (AtMost 1)) entry)
        Assert.False(Query.matches (severity (Exactly 3)) entry)

        let source value = { Query.empty with SourceAddress = Some value }
        Assert.True(Query.matches (source "10.0.0.5") entry)
        Assert.False(Query.matches (source "10.0.0.6") entry)
