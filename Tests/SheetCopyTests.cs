using System;
using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class SheetCopyTests {
    private const string Url = "https://script.google.com/macros/s/AKfycbxSECRET/exec";
    private static readonly DateTime Saved = new(2026, 10, 4, 18, 32, 5, DateTimeKind.Utc);

    private static List<RemoteRow> Rows() => [
        new() { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = "21.948" },
        new() { Tab = "A Sides", Band = "checkpoint", Chapter = "6a", Cp = "Hollows \U0001F4FC", Time = "8.704" },
    ];

    [Fact]
    public void RoundTrips() {
        string json = SheetCopy.Serialize(Url, Saved, Rows());

        Assert.True(SheetCopy.TryParse(json, Url, out List<RemoteRow> rows, out DateTime savedAt, out string why));
        Assert.Null(why);
        Assert.Equal(Saved, savedAt);
        Assert.Equal(DateTimeKind.Utc, savedAt.Kind);
        Assert.Equal(2, rows.Count);
        Assert.Equal("checkpoint", rows[0].Band);
        Assert.Equal("Hollows \U0001F4FC", rows[1].Cp);
    }

    // the URL is the credential: the file carries only what tells sheets apart
    [Fact]
    public void HoldsNoUrl() {
        string json = SheetCopy.Serialize(Url, Saved, Rows());
        Assert.DoesNotContain("AKfycbxSECRET", json);
        Assert.DoesNotContain("script.google.com", json);
        Assert.Contains(SheetCopy.Fingerprint(Url), json);
    }

    [Fact]
    public void TheFingerprintIsSixteenLowercaseHexDigitsOfTheTrimmedUrl() {
        string print = SheetCopy.Fingerprint(Url);
        Assert.Matches("^[0-9a-f]{16}$", print);
        Assert.Equal(print, SheetCopy.Fingerprint("  " + Url + " "));
        Assert.NotEqual(print, SheetCopy.Fingerprint(Url.Replace("SECRET", "OTHER")));
    }

    [Fact]
    public void AnotherSheetsCopyIsRefused() {
        string json = SheetCopy.Serialize(Url.Replace("SECRET", "OTHER"), Saved, Rows());
        Assert.False(SheetCopy.TryParse(json, Url, out _, out _, out string why));
        Assert.Equal(SheetCopy.AnotherSheet, why);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    [InlineData("""{"format":1,"sheet":"x","savedAt":"2026-10-04T18:32:05Z","rows":[{"tab":"A Si""")]
    [InlineData("""{"format":3,"sheet":"x","savedAt":"2026-10-04T18:32:05Z","rows":[]}""")]
    public void AnUnreadableCopyIsRefusedWithoutThrowing(string json) {
        Assert.False(SheetCopy.TryParse(json, Url, out List<RemoteRow> rows, out _, out string why));
        Assert.Null(rows);
        Assert.NotNull(why);
    }

    [Fact]
    public void NullRowsAndFieldsReadAsEmpty() {
        string print = SheetCopy.Fingerprint(Url);
        string json = $$"""{"format":2,"sheet":"{{print}}","savedAt":"2026-10-04T18:32:05Z","rows":[null,{"tab":"A Sides","band":null,"chapter":"1a","cp":"Crossing","time":null}]}""";

        Assert.True(SheetCopy.TryParse(json, Url, out List<RemoteRow> rows, out _, out _));
        RemoteRow row = Assert.Single(rows);
        Assert.Equal(("", ""), (row.Band, row.Time));
    }

    [Fact]
    public void TheFiveTimingFieldsRoundTrip() {
        RemoteRow full = new() {
            Tab = "A Sides", Band = "il", Chapter = "1a", Cp = "1a", Time = "1:05.000", Timing = "FileTime",
            TimingOptions = ["S&Q", "No S&Q", "FileTime"], TimingGroup = "1a", FileTimeBuffer = 5.5, RankOffset = 2.25,
        };
        RemoteRow plain = new() { Tab = "A Sides", Band = "il", Chapter = "1a", Cp = "Chasm", Time = "30.000" };

        string json = SheetCopy.Serialize(Url, Saved, [full, plain]);
        Assert.True(SheetCopy.TryParse(json, Url, out List<RemoteRow> rows, out _, out _));

        Assert.Equal(("FileTime", "1a", 5.5, 2.25), (rows[0].Timing, rows[0].TimingGroup, rows[0].FileTimeBuffer, rows[0].RankOffset));
        Assert.Equal(["S&Q", "No S&Q", "FileTime"], rows[0].TimingOptions);
        Assert.Null(rows[1].Timing);
        Assert.Null(rows[1].TimingOptions);
        Assert.Null(rows[1].FileTimeBuffer);
        // absent fields stay out of the file
        Assert.Equal(1, json.Split("fileTimeBuffer").Length - 1);
    }

    [Fact]
    public void AFormatOneCopyIsRefused() {
        string json = SheetCopy.Serialize(Url, Saved, Rows()).Replace("\"format\":2", "\"format\":1");

        Assert.False(SheetCopy.TryParse(json, Url, out _, out _, out string why));
        Assert.Equal("format 1", why);
    }
}
