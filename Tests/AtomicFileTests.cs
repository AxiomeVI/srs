using System;
using System.IO;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// the export URL file is written from a menu press on the game thread: a
// failure must come back as a value, and the temporary file, which holds the
// credential, must not stay behind
public sealed class AtomicFileTests : IDisposable {
    private readonly string dir = Path.Combine(Path.GetTempPath(), "srs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(dir)) {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WritesTheFileAndCreatesItsFolder() {
        string path = Path.Combine(dir, "srs", "export-url.txt");

        Assert.True(AtomicFile.TryWrite(path, "first", out Exception error, out _));
        Assert.True(AtomicFile.TryWrite(path, "second", out _, out _));

        Assert.Null(error);
        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // a folder where the file should be makes the move throw, as a file held
    // open by another program does on Windows
    [Fact]
    public void AFailedMoveThrowsNothingAndLeavesNoTemporaryFile() {
        string path = Path.Combine(dir, "export-url.txt");
        Directory.CreateDirectory(path);

        Assert.False(AtomicFile.TryWrite(path, "secret", out Exception error, out Exception leftover));

        Assert.NotNull(error);
        Assert.Null(leftover);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
