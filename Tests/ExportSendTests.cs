using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

[Collection(RemoteBestsCollection.Name)]
public sealed class ExportSendTests : IDisposable {
    private const string Url = "https://script.google.com/macros/s/A/exec";

    private readonly List<TaskCompletionSource<(string, string)>> posts = [];
    private readonly List<string> bodies = [];
    private readonly List<string> infos = [];
    private readonly List<string> warns = [];
    private bool goOn = true;
    // what the host says of a request that failed
    private bool timesOut;
    private int delays;

    public ExportSendTests() {
        SheetReader.Install(new ReaderHost {
            // the first read answers, so the rows held are this URL's; later ones never do
            Fetch = _ => Interlocked.Increment(ref fetches) == 1
                ? Task.FromResult((Sheet, (string) null))
                : new TaskCompletionSource<(string, string)>().Task,
            Info = _ => { },
            Warn = _ => { },
            Enabled = () => true,
            Url = () => Url,
            CopyPath = null,
            Delay = _ => {
                Interlocked.Increment(ref delays);
                return new TaskCompletionSource().Task;
            },
        });
        ExportSend.Install(new SendHost {
            Post = (_, json) => {
                TaskCompletionSource<(string, string)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (posts) {
                    bodies.Add(json);
                    posts.Add(answer);
                }

                return answer.Task.ContinueWith(task => (task.Result.Item1, task.Result.Item2, timesOut),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            },
            Info = message => { lock (infos) { infos.Add(message); } },
            Warn = message => { lock (warns) { warns.Add(message); } },
        });
        SheetReader.Reset();
        SendLedger.Shared.Reset();
    }

    public void Dispose() {
        SheetReader.Reset();
        SendLedger.Shared.Reset();
    }

    private const string Sheet =
        """{"rows":[{"tab":"A Sides","band":"checkpoint","chapter":"1a","cp":"Crossing","time":"25.000"}],"version":3}""";
    private int fetches;

    // the sheet answered once, through the reader: a write can be taken
    private static int Hold() {
        Assert.True(SheetReader.Refresh("held").Wait(TimeSpan.FromSeconds(5)));
        return RemoteBests.Accepts;
    }

    private static List<ExportUpdate> Updates(int count) =>
        Enumerable.Range(0, count).Select(i => new ExportUpdate {
            Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = $"row {i}", Time = "21.948", Expect = "25.000",
        }).ToList();

    private static string Answer(string json, string status = "written") {
        ExportRequest request = JsonSerializer.Deserialize<ExportRequest>(json);
        var results = request.Updates.Select(u => new { tab = u.Tab, band = u.Band, chapter = u.Chapter, cp = u.Cp, status });
        return JsonSerializer.Serialize(new { results, ms = 5, version = 3 });
    }

    private TaskCompletionSource<(string, string)> Post(int index) {
        lock (posts) {
            return posts[index];
        }
    }

    private int Posts {
        get {
            lock (posts) {
                return posts.Count;
            }
        }
    }

    private static async Task Until(Func<bool> condition) {
        for (int i = 0; i < 500 && !condition(); i++) {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void ARefusedWriteStartsNothing() {
        // nothing held: the sheet has not answered
        WriteRefusal refusal = ExportSend.TryStart(Updates(1), RemoteBests.Accepts, "manual",
            () => true, _ => { }, _ => Assert.Fail("done called"));

        Assert.Equal(WriteRefusal.NothingHeld, refusal);
        Assert.Equal(0, Posts);
        Assert.False(SheetReader.IsWriting);
        Assert.Empty(infos);
    }

    [Fact]
    public async Task PostsAndEndsTheWrite() {
        int accepts = Hold();
        int calls = 0;
        SendOutcome seen = default;
        List<ExportUpdate> sent = Updates(2);

        Assert.Equal(WriteRefusal.None, ExportSend.TryStart(sent, accepts, "manual", () => true, _ => { },
            outcome => { seen = outcome; Interlocked.Increment(ref calls); }));
        Assert.True(SheetReader.IsWriting);
        Assert.Equal("exporting 2 row(s) (manual)", infos[0]);

        await Until(() => Posts == 1);
        Post(0).SetResult((Answer(bodies[0]), null));
        await Until(() => Volatile.Read(ref calls) == 1);

        Assert.False(SheetReader.IsWriting);
        Assert.Equal(2, seen.Results.Count);
        Assert.Null(seen.Error);
        Assert.False(seen.Stopped);
        Assert.Equal(0, seen.NotWritten);
        Assert.Contains(infos, line => line.StartsWith("the script took 5 ms for 2 of 2 update(s)"));
        await Task.Delay(50);
        Assert.Equal(1, calls);
    }

    private static List<PendingUpdate> Rows(int count) => Enumerable.Range(0, count).Select(i =>
        PendingUpdate.Create(new SheetRowRef("A Sides", "1a", $"row {i}"), $"row {i}", TimeSpan.FromSeconds(60).Ticks,
            new RemoteRow { Tab = "A Sides", Band = "checkpoint", Chapter = "1a", Cp = $"row {i}", Time = "70.000" },
            SaveAndQuitNote.Unknown, false, 0)).ToList();

    [Fact]
    public async Task MarksTheLedgerPerRequestPosted() {
        int accepts = Hold();
        List<PendingUpdate> rows = Rows(ExportBatches.Size + 2);
        List<ExportUpdate> sent = SendLedger.Shared.Pick(rows, retry: false);
        Assert.Equal(rows.Count, sent.Count);

        int calls = 0;
        ExportSend.TryStart(sent, accepts, "manual", () => true, _ => { }, _ => Interlocked.Increment(ref calls));
        await Until(() => Posts == 1);
        Post(0).SetResult((Answer(bodies[0]), null));
        await Until(() => Posts == 2);
        // posted, so unanswered already, before the send ends
        Assert.Empty(SendLedger.Shared.Pick(rows.Skip(ExportBatches.Size).ToList(), retry: false));
        Post(1).SetResult((null, "no answer"));
        await Until(() => Volatile.Read(ref calls) == 1);

        // the second request never answered; the first is written
        List<PendingUpdate> first = rows.Take(ExportBatches.Size).ToList();
        List<PendingUpdate> second = rows.Skip(ExportBatches.Size).ToList();
        Assert.Empty(SendLedger.Shared.Pick(first, retry: true));
        Assert.Empty(SendLedger.Shared.Pick(second, retry: false));
        Assert.Equal(second.Count, SendLedger.Shared.Pick(second, retry: true).Count);
    }

    [Fact]
    public async Task AStoppedSendReportsStopped() {
        int accepts = Hold();
        SendOutcome seen = default;
        int calls = 0;

        ExportSend.TryStart(Updates(ExportBatches.Size + 1), accepts, "manual", () => goOn, _ => { },
            outcome => { seen = outcome; Interlocked.Increment(ref calls); });
        await Until(() => Posts == 1);
        goOn = false;
        Post(0).SetResult((Answer(bodies[0]), null));
        await Until(() => Volatile.Read(ref calls) == 1);

        Assert.True(seen.Stopped);
        Assert.NotNull(seen.Error);
        Assert.Equal(ExportBatches.Size, seen.Results.Count);
        Assert.Equal(0, seen.NotWritten);
        Assert.False(SheetReader.IsWriting);
        // the request never posted left nothing unanswered
        Assert.False(SheetReader.UnansweredWrite);
    }

    // only a request given up on leaves a script that may still be writing
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task TellsTheReaderWhetherTheUnansweredRequestTimedOut(bool timedOut, int scheduled) {
        int accepts = Hold();
        int calls = 0;
        timesOut = timedOut;

        ExportSend.TryStart(Updates(1), accepts, "manual", () => true, _ => { }, _ => Interlocked.Increment(ref calls));
        await Until(() => Posts == 1);
        Post(0).SetResult((null, "no answer"));
        await Until(() => Volatile.Read(ref calls) == 1);

        Assert.True(SheetReader.UnansweredWrite);
        Assert.Equal(scheduled, Volatile.Read(ref delays));
    }

    [Fact]
    public void NotWrittenCountsRefusalsAndUnansweredRowsButNotChanged() {
        ExportResult R(string status) => new() { Status = status };
        List<ExportUpdate> sent = Updates(5);

        // written, unchanged, changed count as done; refused and notFound do not; one row has no answer
        SendOutcome outcome = new(sent, [R("written"), R("unchanged"), R("changed"), R("refused")], null, false);
        Assert.Equal(2, outcome.NotWritten);
        Assert.Equal(0, (outcome with { Stopped = true }).NotWritten);
        Assert.Equal(1, new SendOutcome(sent, [R("written"), R("written"), R("written"), R("written")], null, false).NotWritten);
    }

    [Fact]
    public async Task AThrowingDoneStillEndsTheWrite() {
        int accepts = Hold();

        ExportSend.TryStart(Updates(1), accepts, "manual", () => true, _ => { },
            _ => throw new InvalidOperationException("boom"));
        await Until(() => Posts == 1);
        Post(0).SetResult((Answer(bodies[0]), null));

        await Until(() => !SheetReader.IsWriting);
        await Until(() => { lock (warns) { return warns.Any(w => w.Contains("could not be handled")); } });
    }

    [Fact]
    public async Task AFaultedRequestStillEndsTheWriteAndCallsDone() {
        int accepts = Hold();
        SendOutcome? seen = null;

        ExportSend.TryStart(Updates(1), accepts, "manual", () => true, _ => { }, outcome => seen = outcome);
        await Until(() => Posts == 1);
        Post(0).SetException(new InvalidOperationException("boom"));

        await Until(() => seen != null);
        Assert.False(SheetReader.IsWriting);
        Assert.Empty(seen.Value.Results);
        Assert.NotNull(seen.Value.Error);
        Assert.Equal(1, seen.Value.NotWritten);
    }
}
