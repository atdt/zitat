namespace Zitat.Tests

open System
open Microsoft.AspNetCore.Http
open Xunit
open Zitat

module WebTests =
    let private query text =
        let context = DefaultHttpContext()
        context.Request.QueryString <- QueryString text
        Web.query context

    [<Theory>]
    [<InlineData("?range=bad&since=2026-09-20T12:00:00Z")>]
    [<InlineData("?range=-1h")>]
    [<InlineData("?range=999999999999d")>]
    [<InlineData("?range=NaNh")>]
    [<InlineData("?range=1,5h")>]
    [<InlineData("?since=bad")>]
    [<InlineData("?facility=bad")>]
    [<InlineData("?severity=bad")>]
    [<InlineData("?before=bad")>]
    [<InlineData("?limit=0")>]
    [<InlineData("?q=severity:bad")>]
    [<InlineData("?q=facility:bad")>]
    let ``invalid query values are rejected`` text =
        Assert.True(query text |> Result.isError)

    [<Fact>]
    let ``relative range uses an invariant positive duration`` () =
        let before = DateTimeOffset.UtcNow.AddHours(-1.)

        match query "?range=1.5h" with
        | Ok parsed ->
            Assert.True(parsed.Since.IsSome)
            Assert.InRange(parsed.Since.Value, before.AddMinutes(-31.), before.AddMinutes(-29.))
        | Error error -> failwith error

    [<Theory>]
    [<InlineData("?facility=auth", 4)>]
    [<InlineData("?facility=AUTH", 4)>]
    [<InlineData("?facility=17", 17)>]
    let ``facility parameter accepts names and numbers`` text expected =
        match query text with
        | Ok parsed -> Assert.Equal(Some expected, parsed.Facility)
        | Error error -> failwith error
