using System;
using System.Linq;
using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class ExportProtocolTests {
    // expect is the compare-and-swap: the script writes nothing unless the cell
    // still reads what it carries
    [Fact]
    public void SerializesARequestWithLowerCamelCaseFields() {
        var request = new ExportRequest {
            Updates = {
                new ExportUpdate { Tab = "B+C Sides", Band = "checkpoint", Chapter = "6b", Cp = "Falling", Time = "1:07.915", Expect = "1:09.4" },
            },
        };

        string json = ExportProtocol.SerializeRequest(request);

        Assert.Equal(
            """{"updates":[{"tab":"B+C Sides","band":"checkpoint","chapter":"6b","cp":"Falling","time":"1:07.915","expect":"1:09.4"}]}""",
            json);
    }

    // the payloads below are the deployed script's own answers
    [Fact]
    public void ParsesAResponse() {
        const string json = """
            {"results":[{"tab":"A Sides","chapter":"7a","cp":"7a Start \uD83D\uDC8E","status":"written","reason":""}],"version":3}
            """;

        Assert.True(ExportProtocol.TryParseResponse(json, out var response, out string error));
        Assert.Null(error);
        Assert.Single(response.Results);
        Assert.Equal("written", response.Results[0].Status);
        Assert.Equal("7a Start \U0001F48E", response.Results[0].Cp);
    }

    [Fact]
    public void ParsesARefusalWithItsReason() {
        const string json = """
            {"results":[{"tab":"A Sides","chapter":"1a","cp":"No Such Row","status":"notFound","reason":"no row matching 1a / No Such Row in tab \"A Sides\""}],"version":3}
            """;

        Assert.True(ExportProtocol.TryParseResponse(json, out var response, out _));
        Assert.Equal("notFound", response.Results[0].Status);
        Assert.StartsWith("no row matching", response.Results[0].Reason);
    }

    [Fact]
    public void ANullResultsListReadsAsNoResults() {
        Assert.True(ExportProtocol.TryParseResponse("""{"results":null,"version":3}""", out var response, out _));
        Assert.Empty(response.Results);
    }

    [Fact]
    public void NullFieldsOfAResultReadAsEmpty() {
        Assert.True(ExportProtocol.TryParseResponse(
            """{"results":[{"tab":null,"band":null,"chapter":null,"cp":null,"status":null,"reason":null}],"version":3}""",
            out var response, out _));

        ExportResult r = Assert.Single(response.Results);
        Assert.Equal(("", "", "", "", "", ""), (r.Tab, r.Band, r.Chapter, r.Cp, r.Status, r.Reason));
    }

    [Fact]
    public void NullEntriesAreDroppedFromBothAnswers() {
        Assert.True(ExportProtocol.TryParseResponse(
            """{"results":[null,{"tab":"A Sides","chapter":"1a","cp":"Crossing","status":"written"}],"version":3}""",
            out var response, out _));
        Assert.Equal("written", Assert.Single(response.Results).Status);

        Assert.True(ExportProtocol.TryParseRows(
            """{"rows":[null,{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.948"}],"version":3}""",
            out var rows, out _, out _, out _));
        Assert.Equal("21.948", Assert.Single(rows).Time);
    }

    // the write path answers with results or with error, never both, and both
    // parse entry points treat error the same way: a failed parse carrying the
    // script's message, not a response the caller has to inspect for one
    [Fact]
    public void AResponseErrorSurfacesTheScriptsDiagnosticMessage() {
        const string json = """
            {"error":"TypeError: Cannot read properties of null"}
            """;

        Assert.False(ExportProtocol.TryParseResponse(json, out var response, out string error));
        Assert.Null(response);
        Assert.Equal("TypeError: Cannot read properties of null", error);
    }

    [Fact]
    public void RejectsAnHtmlBodyWithAnExplicitMessage() {
        Assert.False(ExportProtocol.TryParseResponse("<!DOCTYPE html><html>", out _, out string error));
        // unlocalised in the test project: ExportProtocol.Localize is left as identity
        Assert.Equal("SRS_EXPORT_ERR_LOGIN_PAGE", error);
    }

    [Fact]
    public void RejectsMalformedJsonWithoutThrowing() {
        Assert.False(ExportProtocol.TryParseResponse("{not json", out _, out string error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ParsesRemoteRows() {
        const string json = """
            {"rows":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.947","standard":"Pink"}],"version":3}
            """;

        Assert.True(ExportProtocol.TryParseRows(json, out List<RemoteRow> rows, out _, out _, out _));
        Assert.Single(rows);
        Assert.Equal("A Sides", rows[0].Tab);
        Assert.Equal("Crossing", rows[0].Cp);
    }

    [Fact]
    public void RemoteRowsErrorSurfacesTheScriptsDiagnosticMessage() {
        const string json = """
            {"error":"Error: Tab \"Any%\" not found"}
            """;

        Assert.False(ExportProtocol.TryParseRows(json, out _, out _, out string error, out _));
        Assert.Equal("Error: Tab \"Any%\" not found", error);
    }

    [Fact]
    public void ReadsTheBandOfEachRow() {
        const string json = """
            {"rows":[{"tab":"A Sides","band":"checkpoint","chapter":"1a","cp":"Crossing","time":"21.947"}],"ms":812,"cached":false,"version":3}
            """;

        Assert.True(ExportProtocol.TryParseRows(json, out List<RemoteRow> rows, out _, out _, out _));
        Assert.Equal("checkpoint", Assert.Single(rows).Band);
    }

    // a v1 script sends no version: srs speaks v3 only, and says what to do
    [Fact]
    public void RowsWithoutTheCurrentVersionAreRefusedAsOutOfDate() {
        const string json = """
            {"rows":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.947"}]}
            """;

        Assert.False(ExportProtocol.TryParseRows(json, out _, out _, out string error, out RowsFailure failure));
        Assert.Equal(RowsFailure.OutOfDate, failure);
        Assert.Equal("SRS_EXPORT_ERR_OUT_OF_DATE", error);
    }

    [Fact]
    public void AResponseWithoutTheCurrentVersionIsRefused() {
        const string json = """
            {"results":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","status":"written","reason":""}]}
            """;

        Assert.False(ExportProtocol.TryParseResponse(json, out var response, out string error));
        Assert.Null(response);
        Assert.Equal("SRS_EXPORT_ERR_OUT_OF_DATE", error);
    }

    // a version 2 script takes a version 3 export without its Timing values
    [Theory]
    [InlineData(2, "SRS_EXPORT_ERR_OUT_OF_DATE")]
    [InlineData(4, "SRS_EXPORT_ERR_MOD_OUT_OF_DATE")]
    public void AnotherVersionIsRefusedAndSaysWhichSideToUpdate(int version, string key) {
        string rows = $$"""
            {"version":{{version}},"rows":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.947"}]}
            """;
        string results = $$"""
            {"version":{{version}},"results":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","status":"written"}]}
            """;

        Assert.False(ExportProtocol.TryParseRows(rows, out _, out _, out string error, out RowsFailure failure));
        Assert.Equal((RowsFailure.OutOfDate, key), (failure, error));
        Assert.False(ExportProtocol.TryParseResponse(results, out _, out error));
        Assert.Equal(key, error);
    }

    // the script's own error wins over the version check: it says more
    [Fact]
    public void AnErrorAnswerIsAnErrorWhateverItsVersion() {
        Assert.False(ExportProtocol.TryParseRows("""{"error":"boom","ms":3,"version":3}""",
            out _, out _, out string error, out RowsFailure failure));
        Assert.Equal(RowsFailure.Refused, failure);
        Assert.Equal("boom", error);
    }

    [Fact]
    public void ReadsTheMatchedBandAndTheScriptTimeOfAnExport() {
        const string json = """
            {"results":[{"tab":"A Sides","band":"il","chapter":"1a","cp":"1a","status":"unchanged","reason":"1a already holds \"2:01.3\""}],"ms":4210,"version":3}
            """;

        Assert.True(ExportProtocol.TryParseResponse(json, out var response, out _));
        Assert.Equal("il", response.Results[0].Band);
        Assert.Equal(4210, response.Ms);
    }
}

public class EndpointUrlTests {
    [Theory]
    [InlineData("https://script.google.com/macros/s/AKfycbx123/exec")]
    [InlineData("  https://script.google.com/macros/s/AKfycbx123/exec  ")]
    public void AcceptsADeployedWebApp(string url) {
        Assert.True(ExportProtocol.IsEndpointUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // the /dev URL needs a Google login and would answer the mod a sign-in page
    [InlineData("https://script.google.com/macros/s/AKfycbx123/dev")]
    // the sheet itself, which is the paste a player is most likely to make
    [InlineData("https://docs.google.com/spreadsheets/d/1Gjr0t5N/edit#gid=0")]
    [InlineData("http://script.google.com/macros/s/AKfycbx123/exec")]
    [InlineData("script.google.com/macros/s/AKfycbx123/exec")]
    [InlineData("not a url at all")]
    // a copy broken over two lines, or with a space in it
    [InlineData("https://script.google.com/macros/s/AKfy\ncbx123/exec")]
    [InlineData("https://script.google.com/macros/s/AKfy cbx123/exec")]
    public void RefusesEverythingElse(string url) {
        Assert.False(ExportProtocol.IsEndpointUrl(url));
    }

    [Fact]
    public void ReadsTheFiveTimingFieldsOfARow() {
        const string json = """
            {"rows":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.947","timing":"FileTime","timingOptions":["S&Q","No S&Q","FileTime"],"timingGroup":"1a","fileTimeBuffer":5.25,"rankOffset":2.5}],"version":3}
            """;

        Assert.True(ExportProtocol.TryParseRows(json, out List<RemoteRow> rows, out _, out _, out _));
        RemoteRow row = Assert.Single(rows);
        Assert.Equal("FileTime", row.Timing);
        Assert.Equal(["S&Q", "No S&Q", "FileTime"], row.TimingOptions);
        Assert.Equal("1a", row.TimingGroup);
        Assert.Equal(5.25, row.FileTimeBuffer);
        Assert.Equal(2.5, row.RankOffset);
    }

    [Fact]
    public void AnUpdateWithoutTimingOmitsTheTimingFields() {
        string json = ExportProtocol.SerializeRequest(new ExportRequest {
            Updates = [new ExportUpdate { Tab = "A Sides", Band = "il", Chapter = "1a", Cp = "1a", Time = "1.000", Expect = "2.000" }],
        });

        Assert.Equal("""{"updates":[{"tab":"A Sides","band":"il","chapter":"1a","cp":"1a","time":"1.000","expect":"2.000"}]}""", json);
    }

    [Fact]
    public void AnUpdateWithTimingCarriesTheThreeFields() {
        string json = ExportProtocol.SerializeRequest(new ExportRequest {
            Updates = [new ExportUpdate { Timing = "S&Q", ExpectTiming = "FileTime", ExpectBuffer = 5.123 }],
        });

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement update = doc.RootElement.GetProperty("updates")[0];
        Assert.Equal("S&Q", update.GetProperty("timing").GetString());
        Assert.Equal("FileTime", update.GetProperty("expectTiming").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Number, update.GetProperty("expectBuffer").ValueKind);
        Assert.Equal(5.123, update.GetProperty("expectBuffer").GetDouble());
    }

    [Fact]
    public void AResultsTimingIsParsedAndAbsentIsEmpty() {
        const string json = """
            {"results":[{"tab":"A Sides","chapter":"1a","cp":"A","status":"written","timing":"set"},
                        {"tab":"A Sides","chapter":"1a","cp":"B","status":"written"}],"version":3}
            """;

        Assert.True(ExportProtocol.TryParseResponse(json, out var response, out _));
        Assert.Equal(["set", ""], response.Results.Select(r => r.Timing));
    }
}
