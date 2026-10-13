using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class DialogFilesTests {
    // read raw by Dialog.Get, the only keys a placeholder survives in
    private static readonly string[] RawKeys = ["SRS_KEYBIND_TIMEOUT", "SRS_KEYBIND_PAGE_COMBO_HINT"];

    private static Dictionary<string, string> Read(string language) {
        Dictionary<string, string> entries = [];
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Dialog", language + ".txt"))) {
            int equals = line.IndexOf('=');
            if (equals > 0 && !line.StartsWith('#')) {
                // Add throws on a key written twice
                entries.Add(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
        }

        return entries;
    }

    [Fact]
    public void BothLanguagesHoldTheSameKeys() {
        Assert.Equal(Read("English").Keys.Order(), Read("French").Keys.Order());
    }

    [Theory]
    [InlineData("English")]
    [InlineData("French")]
    public void NoTextIsEmptyCarriesAPlaceholderOrAnEmDash(string language) {
        foreach ((string key, string text) in Read(language)) {
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            // Dialog.Clean erases braces and what they hold
            Assert.True(RawKeys.Contains(key) || !text.Contains('{'), key);
            Assert.False(text.Contains('—'), key);
        }
    }
}
