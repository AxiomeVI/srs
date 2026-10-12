using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

[Collection(RemoteBestsCollection.Name)]
public sealed class SheetReaderTests : IDisposable {
    private const string UrlA = "https://script.google.com/macros/s/A/exec";
    private const string UrlB = "https://script.google.com/macros/s/B/exec";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "srs-reader-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Url, TaskCompletionSource<(string, string)> Answer)> fetches = [];
    private string url = UrlA;
    private bool enabled = true;
    private int urlReads;
    private int urlLimit = int.MaxValue;

    private string CopyPath => Path.Combine(dir, "sheet-times.json");
    private static readonly SheetRowRef Crossing = new("A Sides", "1a", "Crossing");

    public SheetReaderTests() {
        Directory.CreateDirectory(dir);
        SheetReader.Install(new ReaderHost {
            Fetch = asked => {
                TaskCompletionSource<(string, string)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (fetches) {
                    fetches.Add((asked, answer));
                }

                return answer.Task;
            },
            Info = _ => { },
            Warn = _ => { },
            Enabled = () => enabled,
            Url = () => urlReads++ < urlLimit ? url : UrlB,
            CopyPath = CopyPath,
        });
        SheetReader.Reset();
    }

    public void Dispose() {
        SheetReader.Reset();
        Directory.Delete(dir, recursive: true);
    }

    private static string Answer(string time) =>
        $$"""{"rows":[{"tab":"A Sides","band":"checkpoint","chapter":"1a","cp":"Crossing","time":"{{time}}"}],"version":3}""";

    private int Asked {
        get {
            lock (fetches) {
                return fetches.Count;
            }
        }
    }

    private (string Url, TaskCompletionSource<(string, string)> Answer) Fetch(int index) {
        lock (fetches) {
            return fetches[index];
        }
    }

    private static async Task Until(Func<bool> condition) {
        for (int i = 0; i < 500 && !condition(); i++) {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task<ReadOutcome> Within(Task<ReadOutcome> task) {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(5000)));
        return await task;
    }

    private static string HeldTime() => RemoteBests.TryGet(Crossing, out RemoteRow row) ? row.Time : null;

    private async Task HoldAnswer(string time) {
        Task<ReadOutcome> read = SheetReader.Refresh("setup");
        Fetch(Asked - 1).Answer.SetResult((Answer(time), null));
        Assert.Equal(ReadKind.Accepted, (await Within(read)).Kind);
    }

    private static List<ExportUpdate> Sent(string time) => [
        new() { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Time = time, Expect = "25.000" },
    ];

    private static List<ExportResult> WrittenAnswer() =>
        [new ExportResult { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Crossing", Status = "written" }];

    [Fact]
    public async Task JoinsAReadAskedForTheSameSheetSinceTheLastWrite() {
        Task<ReadOutcome> first = SheetReader.Refresh("launch");
        Task<ReadOutcome> second = SheetReader.Refresh("a screen opened");
        Assert.Same(first, second);
        Assert.Equal(1, Asked);

        Fetch(0).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Accepted, (await Within(first)).Kind);
        Assert.Equal("21.948", HeldTime());
        Assert.False(SheetReader.IsReading);
    }

    [Fact]
    public async Task AReadAskedAfterAWriteWaitsForTheNextOne() {
        Task<ReadOutcome> before = SheetReader.Refresh("launch");
        SheetReader.BeginWrite();
        Task<ReadOutcome> after = SheetReader.Refresh("after a write");
        Assert.NotSame(before, after);
        Assert.Equal(1, Asked);

        Fetch(0).Answer.SetResult((Answer("25.000"), null));
        await Until(() => Asked == 2);
        Assert.Null(HeldTime());

        Fetch(1).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Accepted, (await Within(after)).Kind);
        // a dropped read's callers follow the next one
        Assert.Equal(ReadKind.Accepted, (await Within(before)).Kind);
        Assert.Equal("21.948", HeldTime());
    }

    // the script serves the old cells until its POST ends
    [Fact]
    public async Task AReadStartedDuringAPostIsDropped() {
        await HoldAnswer("25.000");
        WriteToken token = SheetReader.BeginWrite();
        Task<ReadOutcome> during = SheetReader.Refresh("a screen opened during the export");
        Assert.Equal(2, Asked);

        SheetReader.EndWrite(token, Sent("21.948"), WrittenAnswer());
        Assert.Equal("21.948", HeldTime());

        Fetch(1).Answer.SetResult((Answer("25.000"), null));
        await Until(() => Asked == 3);
        Assert.Equal("21.948", HeldTime());

        Fetch(2).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Accepted, (await Within(during)).Kind);
        Assert.Equal("21.948", HeldTime());
    }

    [Fact]
    public async Task AnAnswerForAnotherUrlIsDropped() {
        Task<ReadOutcome> old = SheetReader.Refresh("launch");
        url = UrlB;
        SheetReader.Forget();
        Task<ReadOutcome> check = SheetReader.Refresh("a sheet URL was just set");

        Fetch(0).Answer.SetResult((Answer("99.000"), null));
        await Until(() => Asked == 2);
        Assert.Equal(UrlB, Fetch(1).Url);
        Assert.Null(HeldTime());

        Fetch(1).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Accepted, (await Within(check)).Kind);
        Assert.Equal(ReadKind.Accepted, (await Within(old)).Kind);
        Assert.Equal("21.948", HeldTime());
    }

    // the URL is set on the game thread without the gate
    [Fact]
    public async Task ACopyCarriesTheFingerprintOfTheSheetItCameFrom() {
        // reads: one in Refresh, one in Land's drop check; any later one sees UrlB
        urlLimit = 2;
        Task<ReadOutcome> read = SheetReader.Refresh("launch");
        Fetch(0).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Accepted, (await Within(read)).Kind);

        Assert.True(SheetCopy.TryParse(File.ReadAllText(CopyPath), UrlA, out _, out _, out _));
    }

    [Fact]
    public async Task AFailureForAnotherUrlMarksNothing() {
        Task<ReadOutcome> old = SheetReader.Refresh("launch");
        url = UrlB;
        SheetReader.Forget();

        Fetch(0).Answer.SetResult((null, "timeout"));
        Assert.Equal(ReadKind.Cancelled, (await Within(old)).Kind);
        Assert.Null(RemoteBests.Error);
    }

    [Fact]
    public async Task FollowersAreCancelledWhenNoReadCanFollow() {
        Task<ReadOutcome> read = SheetReader.Refresh("launch");
        url = "";
        SheetReader.Forget();

        Fetch(0).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Cancelled, (await Within(read)).Kind);
        Assert.False(RemoteBests.IsResolved);
    }

    [Fact]
    public async Task AnAnswerWithTheModOffIsDropped() {
        Task<ReadOutcome> read = SheetReader.Refresh("launch");
        enabled = false;

        Fetch(0).Answer.SetResult((Answer("21.948"), null));
        Assert.Equal(ReadKind.Cancelled, (await Within(read)).Kind);
        Assert.False(RemoteBests.IsResolved);
        Assert.Equal(ReadKind.Cancelled, (await Within(SheetReader.Refresh("off"))).Kind);
    }

    [Fact]
    public async Task AFailureKeepsWhatIsHeld() {
        await HoldAnswer("21.948");
        Task<ReadOutcome> read = SheetReader.Refresh("again");
        Fetch(1).Answer.SetResult((null, "timeout"));

        ReadOutcome outcome = await Within(read);
        Assert.Equal((ReadKind.Unreachable, "timeout"), (outcome.Kind, outcome.Error));
        Assert.Equal("21.948", HeldTime());
        Assert.Equal("timeout", RemoteBests.Error);
    }

    [Theory]
    // the kind as a name: ReadKind is internal, and a public test method cannot take it
    [InlineData("""{"rows":[{"tab":"A Sides","chapter":"1a","cp":"Crossing","time":"21.948"}]}""", "OutOfDate")]
    [InlineData("<!DOCTYPE html>", "NotTheScript")]
    [InlineData("""{"error":"Config names no entry tab","version":3}""", "Refused")]
    [InlineData("""{"rows":[],"version":3}""", "NoRows")]
    [InlineData("""{"rows":[{"tab":"A Sides","chapter":"9z","cp":"Nowhere","time":"1.000"}],"version":3}""", "NoRows")]
    public async Task AnAnswerSrsCannotUseIsNeitherAcceptedNorSaved(string body, string kind) {
        Task<ReadOutcome> read = SheetReader.Refresh("launch");
        Fetch(0).Answer.SetResult((body, null));

        Assert.Equal(Enum.Parse<ReadKind>(kind), (await Within(read)).Kind);
        Assert.False(RemoteBests.IsResolved);
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task AnAcceptedAnswerIsSavedAndLoadsBack() {
        await HoldAnswer("21.948");
        Assert.True(File.Exists(CopyPath));

        RemoteBests.Reset();
        SheetReader.LoadCopy();
        Assert.Equal(HeldSource.Saved, RemoteBests.Source);
        Assert.Equal("21.948", HeldTime());
    }

    [Fact]
    public async Task ASavedCopyNeverReplacesAnAnswer() {
        File.WriteAllText(CopyPath, SheetCopy.Serialize(UrlA, DateTime.UtcNow.AddHours(-2),
            [new RemoteRow { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = "30.000" }]));
        await HoldAnswer("21.948");

        SheetReader.LoadCopy();
        Assert.Equal(HeldSource.Fresh, RemoteBests.Source);
        Assert.Equal("21.948", HeldTime());
    }

    [Fact]
    public void ACopyOfAnotherSheetIsIgnoredAndDeleted() {
        File.WriteAllText(CopyPath, SheetCopy.Serialize(UrlB, DateTime.UtcNow,
            [new RemoteRow { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = "30.000" }]));

        SheetReader.LoadCopy();
        Assert.False(RemoteBests.IsResolved);
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task ForgetDropsEverythingHeld() {
        await HoldAnswer("21.948");
        Assert.True(File.Exists(CopyPath));

        SheetReader.Forget();
        Assert.False(RemoteBests.IsResolved);
        Assert.False(File.Exists(CopyPath));
    }

    [Fact]
    public async Task AWrittenTimeIsSavedWithTheCopy() {
        await HoldAnswer("25.000");
        WriteToken token = SheetReader.BeginWrite();
        SheetReader.EndWrite(token, Sent("21.948"), WrittenAnswer());

        RemoteBests.Reset();
        SheetReader.LoadCopy();
        Assert.Equal("21.948", HeldTime());
    }

    [Fact]
    public async Task EndWriteForAnotherUrlAppliesNothing() {
        await HoldAnswer("25.000");
        WriteToken token = SheetReader.BeginWrite();
        url = UrlB;
        SheetReader.Forget();
        await HoldAnswer("40.000");

        SheetReader.EndWrite(token, Sent("21.948"), WrittenAnswer());
        Assert.Equal("40.000", HeldTime());
        Assert.False(SheetReader.UnansweredWrite);
    }

    [Fact]
    public async Task AnUnansweredPostKeepsExportOffUntilAReadAfterItIsTakenIn() {
        await HoldAnswer("25.000");
        WriteToken token = SheetReader.BeginWrite();
        SheetReader.EndWrite(token, Sent("21.948"), []);
        Assert.True(SheetReader.UnansweredWrite);
        Assert.Equal("25.000", HeldTime());

        // EndWrite asked the sheet again; offline, that read fails
        await Until(() => Asked == 2);
        Fetch(1).Answer.SetResult((null, "offline"));
        await Until(() => !SheetReader.IsReading);
        Assert.True(SheetReader.UnansweredWrite);

        Task<ReadOutcome> read = SheetReader.Refresh("back online");
        Fetch(2).Answer.SetResult((Answer("21.948"), null));
        await Within(read);
        Assert.False(SheetReader.UnansweredWrite);
    }

    [Fact]
    public async Task AnExportCutShortAppliesWhatWasAnsweredAndKeepsExportOff() {
        await HoldAnswer("25.000");
        WriteToken token = SheetReader.BeginWrite();
        List<ExportUpdate> sent = [.. Sent("21.948"),
            new() { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = "Chasm", Time = "30.000" }];
        SheetReader.EndWrite(token, sent, WrittenAnswer());

        Assert.Equal("21.948", HeldTime());
        Assert.True(SheetReader.UnansweredWrite);
    }

    [Fact]
    public async Task IsWritingFromBeginWriteToEndWrite() {
        await HoldAnswer("25.000");
        Assert.False(SheetReader.IsWriting);

        WriteToken answered = SheetReader.BeginWrite();
        Assert.True(SheetReader.IsWriting);
        SheetReader.EndWrite(answered, Sent("21.948"), WrittenAnswer());
        Assert.False(SheetReader.IsWriting);

        WriteToken unanswered = SheetReader.BeginWrite();
        Assert.True(SheetReader.IsWriting);
        SheetReader.EndWrite(unanswered, Sent("21.948"), []);
        Assert.False(SheetReader.IsWriting);
    }

    [Fact]
    public void WrittenPairsResultsWithTheUpdatesTheyAnswer() {
        List<ExportUpdate> sent = [
            new() { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = " 21.948 " },
            new() { Tab = "A Sides", Chapter = "1a", Cp = "Chasm", Time = "30.000" },
        ];
        List<ExportResult> results = [new() { Status = "written" }, new() { Status = "changed" }];

        (SheetRowRef row, string time, string timing) = Assert.Single(SheetReader.Written(sent, results));
        Assert.Equal((Crossing, "21.948", (string) null), (row, time, timing));
        // an answer that does not pair up says nothing srs can trust
        Assert.Empty(SheetReader.Written(sent, [new ExportResult { Status = "written" }]));
    }

    [Theory]
    [InlineData("written", "set", "21.948", "S&Q")]
    [InlineData("unchanged", "set", null, "S&Q")]
    [InlineData("unchanged", "kept", null, "S&Q")]
    [InlineData("written", "", "21.948", null)]
    public void WrittenListsTheTimeAndTheTimingTheSheetNowHolds(string status, string timing, string time, string value) {
        List<ExportUpdate> sent = [new() { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = "21.948", Timing = "S&Q" }];

        (SheetRowRef row, string heldTime, string heldTiming) =
            Assert.Single(SheetReader.Written(sent, [new ExportResult { Status = status, Timing = timing }]));
        Assert.Equal((Crossing, time, value), (row, heldTime, heldTiming));
    }

    [Fact]
    public void ARefusedOrChangedRowIsNotListed() {
        List<ExportUpdate> sent = [new() { Tab = "A Sides", Chapter = "1a", Cp = "Crossing", Time = "21.948", Timing = "S&Q" }];

        Assert.Empty(SheetReader.Written(sent, [new ExportResult { Status = "changed", Timing = "refused" }]));
        Assert.Empty(SheetReader.Written(sent, [new ExportResult { Status = "unchanged", Timing = "" }]));
    }
}
