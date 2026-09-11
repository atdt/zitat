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
        Assert.Equal(Some 5, result.Severity)
        Assert.Equal(Some "lease granted", result.Text)

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
