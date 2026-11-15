using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

// The test project compiles the game-free sources by an explicit list. A new file under
// Source/ must be placed on one side: in that list, or in GameCoupled below. The namespace
// is Celeste.Mod.SpeedrunSheet, so a file reaches game types with no using at all: the list
// is a decision, never inferred from the usings.
public class SourceListTests {
    private static readonly string[] GameCoupled = [
        "RunWatcher.cs", "InteropReport.cs", "TierComparison.cs", "SheetImporter.cs", "RoomMap.cs", "SrsModule.cs",
        "SrsSettings.cs", "SegmentAutoDetect.cs", "UI/ModMenu.cs", "UI/Hotkeys.cs",
        "Export/ExportMenu.cs", "Export/ExportUrlMenu.cs", "Export/ExportTarget.cs",
        "Export/ExportClient.cs", "Export/SessionBests.cs",
    ];

    private static string Root() {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "srs.csproj"))) {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("srs.csproj not found above the test binaries");
    }

    private static List<string> Compiled(string root) =>
        Regex.Matches(File.ReadAllText(Path.Combine(root, "Tests", "srs.Tests.csproj")), "Include=\"\\.\\./Source/([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToList();

    private static List<string> OnDisk(string root) {
        string source = Path.Combine(root, "Source");
        return Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("MenuTools/"))
            .ToList();
    }

    [Fact]
    public void EverySourceFileIsCompiledByTheTestsOrNamedGameCoupled() {
        string root = Root();
        HashSet<string> known = [.. Compiled(root), .. GameCoupled];

        Assert.Equal([], OnDisk(root).Where(f => !known.Contains(f)).Order());
    }

    [Fact]
    public void NoFileIsOnBothSides() =>
        Assert.Equal([], Compiled(Root()).Intersect(GameCoupled).Order());

    [Fact]
    public void EveryListedFileExists() {
        string root = Root();
        HashSet<string> disk = [.. OnDisk(root)];

        Assert.Equal([], Compiled(root).Concat(GameCoupled).Where(f => !disk.Contains(f)).Order());
    }
}
