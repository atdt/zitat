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

