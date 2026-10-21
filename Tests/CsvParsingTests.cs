using System.Collections.Generic;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the minimal RFC 4180 reader behind the importer; Google's export quotes any
// cell containing a comma and ends lines with \r\n
public class CsvParsingTests {
    [Fact]
    public void ParsesPlainRows() {
        List<string[]> rows = Csv.Parse("a,b,c\n1,2,3");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "2", "3"], rows[1]);
    }

    [Fact]
    public void KeepsCommasInsideQuotedFields() {
        List<string[]> rows = Csv.Parse("\"Rock Bottom, 6a\",2:17.615");

        Assert.Equal(["Rock Bottom, 6a", "2:17.615"], rows[0]);
    }

    [Fact]
    public void UnescapesDoubledQuotes() {
        List<string[]> rows = Csv.Parse("\"say \"\"hi\"\"\",x");

        Assert.Equal(["say \"hi\"", "x"], rows[0]);
    }

    [Fact]
    public void KeepsNewlinesInsideQuotedFields() {
        List<string[]> rows = Csv.Parse("\"two\nlines\",b");

        Assert.Single(rows);
        Assert.Equal(["two\nlines", "b"], rows[0]);
    }

    [Theory]
    [InlineData("a,b\r\nc,d")]
    [InlineData("a,b\nc,d")]
    [InlineData("a,b\rc,d")]
    public void HandlesEveryLineEnding(string text) {
        List<string[]> rows = Csv.Parse(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }

    [Fact]
    public void EmitsTheLastRowWithoutTrailingNewline() {
        List<string[]> rows = Csv.Parse("a,b\nc,d");

        Assert.Equal(["c", "d"], rows[^1]);
    }

    [Fact]
    public void KeepsEmptyTrailingFields() {
        // the sheet's rows end on the empty "Unranked" column
        List<string[]> rows = Csv.Parse("a,b,\n");

        Assert.Equal(["a", "b", ""], rows[0]);
    }

    [Fact]
    public void KeepsRaggedRowsAsTheyAre() {
        List<string[]> rows = Csv.Parse("a,b,c\nd,e");

        Assert.Equal(3, rows[0].Length);
        Assert.Equal(2, rows[1].Length);
    }

    [Fact]
    public void ReturnsNothingForEmptyInput() {
        Assert.Empty(Csv.Parse(""));
    }
}
