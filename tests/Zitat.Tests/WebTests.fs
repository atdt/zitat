namespace Zitat.Tests

open Microsoft.AspNetCore.Http
open Xunit
open Zitat

module WebTests =
    let private query text =
        let context = DefaultHttpContext()
        context.Request.QueryString <- QueryString text
        Web.query context

    [<Theory>]
    [<InlineData("?facility=bad")>]
    [<InlineData("?severity=bad")>]
    [<InlineData("?before=bad")>]
    [<InlineData("?limit=0")>]
    [<InlineData("?q=severity:bad")>]
    [<InlineData("?q=facility:bad")>]
    [<InlineData("?q=since:bad")>]
    let ``invalid query values are rejected`` text =
        Assert.True(query text |> Result.isError)

    [<Theory>]
    [<InlineData("?facility=auth", 4)>]
    [<InlineData("?facility=AUTH", 4)>]
    [<InlineData("?facility=17", 17)>]
    let ``facility parameter accepts names and numbers`` text expected =
        match query text with
        | Ok parsed -> Assert.Equal(Some expected, parsed.Facility)
        | Error error -> failwith error
