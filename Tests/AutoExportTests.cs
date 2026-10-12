using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

[Collection(RemoteBestsCollection.Name)]
public sealed class AutoExportTests : IDisposable {
    private const string Url = "https://script.google.com/macros/s/A/exec";
    private Action onSettingRead;

    private readonly string dir = Path.Combine(Path.GetTempPath(), "srs-auto-" + Guid.NewGuid().ToString("N"));
    private readonly List<TaskCompletionSource<(string, string)>> fetches = [];
    private readonly List<TaskCompletionSource<(string, string)>> posts = [];
    private readonly List<string> bodies = [];
    private readonly List<string> infos = [];
    private readonly List<string> sendInfos = [];

    private volatile bool enabled = true;
    private volatile bool setting = true;
    private volatile string url = Url;
    private volatile bool screenOpen;
    private volatile Action<Action> run;
    // the host's four readers, all the worker's: Offer may call none
    private int hostReads;
    // evaluations scheduled and not finished
    private int running;
    // raises of Changed whose handlers have all returned, and how many the test caused
    private int changed;
    private int expected;

    private static readonly SheetRowRef Start = Target("1a", "Start");
    private static readonly SheetRowRef Crossing = Target("1a", "Crossing");
    private static readonly SheetRowRef Chasm = Target("1a", "Chasm");

    public AutoExportTests() {
        Directory.CreateDirectory(dir);
        run = action => action();
        SheetReader.Install(new ReaderHost {
            Fetch = _ => {
                TaskCompletionSource<(string, string)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (fetches) {
                    fetches.Add(answer);
                }

                return answer.Task;
            },
            Info = _ => { },
            Warn = _ => { },
            Enabled = () => enabled,
            Url = () => url,
            CopyPath = Path.Combine(dir, "sheet-times.json"),
        });
        ExportSend.Install(new SendHost {
            Post = (_, json) => {
                TaskCompletionSource<(string, string)> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (posts) {
                    bodies.Add(json);
                    posts.Add(answer);
                }

                return answer.Task;
            },
            Info = message => { lock (sendInfos) { sendInfos.Add(message); } },
            Warn = _ => { },
        });
        // Reset clears Changed's subscribers: Install comes after it
        SheetReader.Reset();
        SendLedger.Shared.Reset();
        AutoExport.Reset();
        AutoExport.Install(Host());
        // subscribed after Wake, so a raise is counted once Wake has returned
        SheetReader.Changed += () => Interlocked.Increment(ref changed);
    }

    public void Dispose() {
        SheetReader.Reset();
        SendLedger.Shared.Reset();
        AutoExport.Reset();
        Directory.Delete(dir, recursive: true);
    }

    private AutoHost Host() => new() {
        Enabled = () => { Interlocked.Increment(ref hostReads); return enabled; },
        Setting = () => { Interlocked.Increment(ref hostReads); onSettingRead?.Invoke(); return setting; },
        Url = () => { Interlocked.Increment(ref hostReads); return url; },
        ScreenOpen = () => { Interlocked.Increment(ref hostReads); return screenOpen; },
        Info = message => { lock (infos) { infos.Add(message); } },
        Run = action => {
            Interlocked.Increment(ref running);
            run(() => {
                try {
                    action();
                } finally {
                    Interlocked.Decrement(ref running);
                }
            });
        },
    };

    private static SheetRowRef Target(string scope, string name) {
        Assert.True(SheetRows.TryFind(scope, name, out SheetRow row));
        return SheetRows.TargetOf(row);
    }

    private static RunBook.Run Run(string scope, string name, double seconds) =>
        new(scope, name, TimeSpan.FromSeconds(seconds).Ticks, SaveAndQuitNote.Without);

    private static string Sheet(params (SheetRowRef Row, string Time)[] cells) => JsonSerializer.Serialize(new {
        rows = cells.Select(c => new { tab = c.Row.Tab, band = "checkpoint", chapter = c.Row.Chapter, cp = c.Row.Cp, time = c.Time }),
        version = 3,
    });

    private static string Answer(string json, string status = "written") {
        ExportRequest request = JsonSerializer.Deserialize<ExportRequest>(json);
        var results = request.Updates.Select(u => new { tab = u.Tab, band = u.Band, chapter = u.Chapter, cp = u.Cp, status });
        return JsonSerializer.Serialize(new { results, ms = 5, version = 3 });
    }

    private int Asked {
        get {
            lock (fetches) {
                return fetches.Count;
            }
        }
    }

    private int Posts {
        get {
            lock (posts) {
                return posts.Count;
            }
        }
    }

    private List<string> Infos {
        get {
            lock (infos) {
                return [.. infos];
            }
        }
    }

    private TaskCompletionSource<(string, string)> LastFetch {
        get {
            lock (fetches) {
                return fetches[^1];
            }
        }
    }

    private TaskCompletionSource<(string, string)> Post(int index) {
        lock (posts) {
            return posts[index];
        }
    }

    private List<ExportUpdate> Sent(int index) {
        lock (posts) {
            return JsonSerializer.Deserialize<ExportRequest>(bodies[index]).Updates;
        }
    }

    private static async Task Until(Func<bool> condition) {
        for (int i = 0; i < 500 && !condition(); i++) {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // every raise the test caused has been handled, and no evaluation is left
    // running: what is asserted next cannot be overtaken by a worker
    private Task Quiet() =>
        Until(() => Volatile.Read(ref changed) == expected && Volatile.Read(ref running) == 0);

    // an accepted read raises Changed
    private async Task AnswerRead(string body) {
        expected++;
        LastFetch.SetResult((body, null));
        await Quiet();
    }

    // a failed read raises nothing
    private async Task FailRead() {
        LastFetch.SetResult((null, "offline"));
        await Until(() => !SheetReader.IsReading);
    }

    private async Task Hold(string body) {
        _ = SheetReader.Refresh("setup");
        await AnswerRead(body);
    }

    // the end of a write raises Changed, whatever the answer
    private async Task AnswerPost(int index, string status = "written") {
        expected++;
        Post(index).SetResult((Answer(bodies[index], status), null));
        await Quiet();
    }

    private async Task FailPost(int index) {
        expected++;
        Post(index).SetResult((null, "no answer"));
        await Quiet();
    }

    // TakeFailed empties what it returns: the count is summed until it shows
    private static async Task<int> Failed(int atLeast) {
        int taken = 0;
        await Until(() => (taken += AutoExport.TakeFailed()) >= atLeast);
        return taken;
    }

    [Fact]
    public async Task NothingRunsWithTheSettingOff() {
        setting = false;
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        // nothing held: with the setting on this asks for a read
        Assert.Equal(0, Asked);

        await Hold(Sheet((Crossing, "25.000")));
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Asked);
        Assert.Equal(0, Posts);
        Assert.Empty(Infos);

        // the setting was the one thing in the way
        setting = true;
        AutoExport.Wake();
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task NothingRunsWithTheModOffOrNoUrl() {
        await Hold(Sheet((Crossing, "25.000")));
        enabled = false;
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        enabled = true;
        url = " ";
        AutoExport.Wake();

        Assert.Equal(1, Asked);
        Assert.Equal(0, Posts);

        // the same offer goes once both are back: it was the stops that held it
        url = Url;
        AutoExport.Wake();
        await Until(() => Posts == 1);
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task AnOfferDuringAnEvaluationGivesAnotherRound() {
        await Hold(Sheet((Crossing, "25.000")));
        bool offered = false;
        onSettingRead = () => {
            if (!offered) {
                offered = true;
                AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
            }
        };

        // the evaluation has read the slower run when the faster one is offered
        AutoExport.Offer([Run("1a", "Crossing", 30)], "1a");

        await Until(() => Posts == 1);
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task OfferItselfCallsNoFetchPostOrLog() {
        await Hold(Sheet((Crossing, "25.000")));
        List<Action> stored = [];
        run = stored.Add;
        int readsBefore = Volatile.Read(ref hostReads);

        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(0, AutoExport.TakeFailed());

        // one worker for both offers, and nothing else happened yet
        Assert.Single(stored);
        Assert.Equal(readsBefore, Volatile.Read(ref hostReads));
        Assert.Equal(1, Asked);
        Assert.Equal(0, Posts);
        Assert.Empty(Infos);
        lock (sendInfos) {
            Assert.Empty(sendInfos);
        }

        stored[0]();
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task APbIsSent() {
        await Hold(Sheet((Crossing, "25.000")));
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");

        Assert.Equal(1, Posts);
        ExportUpdate update = Assert.Single(Sent(0));
        Assert.Equal(Crossing, new SheetRowRef(update.Tab, update.Chapter, update.Cp));
        Assert.Equal("25.000", update.Expect);
        lock (sendInfos) {
            Assert.Equal("exporting 1 row(s) (automatic)", sendInfos[0]);
        }

        await AnswerPost(0);
        Assert.Equal(1, Posts);
        Assert.True(RemoteBests.TryGet(Crossing, out RemoteRow held));
        Assert.Equal(update.Time, held.Time);
        await Task.Delay(50);
        Assert.Equal(0, AutoExport.TakeFailed());
        Assert.DoesNotContain(Infos, line => line.Contains(Url));
    }

    [Fact]
    public async Task ASlowerRunIsNot() {
        await Hold(Sheet((Crossing, "25.000")));
        AutoExport.Offer([Run("1a", "Crossing", 30)], "1a");

        Assert.Equal(0, Posts);
        Assert.Equal(1, Asked);

        // and a faster one is, so the silence above was the rule's
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        await Until(() => Posts == 1);
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task WaitsForTheScreenToCloseThenSends() {
        await Hold(Sheet((Crossing, "25.000")));
        screenOpen = true;
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        AutoExport.Wake();
        Assert.Equal(0, Posts);

        screenOpen = false;
        AutoExport.Wake();
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task WithNothingHeldAnOfferAsksForOneReadAndItsAnswerSends() {
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Asked);
        Assert.Equal(0, Posts);
        Assert.Equal(["waiting for a read of the sheet"], Infos);

        await AnswerRead(Sheet((Crossing, "25.000")));
        Assert.Equal(1, Posts);
        Assert.Equal(1, Asked);
        Assert.Equal(Crossing.Cp, Assert.Single(Sent(0)).Cp);
    }

    [Fact]
    public async Task ChangedNeverAsksForARead() {
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Asked);
        await FailRead();

        AutoExport.Wake();
        AutoExport.Wake();
        // a real raise, with nothing held after it
        expected++;
        SheetReader.Forget();
        await Quiet();

        Assert.Equal(1, Asked);
        Assert.Equal(0, Posts);
        // the cause did not change: one line
        Assert.Equal(["waiting for a read of the sheet"], Infos);
    }

    // the sheet unreachable, then back
    [Fact]
    public async Task AfterAFailedRequestTheNextOfferRetriesIt() {
        string sheet = Sheet((Crossing, "25.000"), (Chasm, "35.000"));
        await Hold(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Posts);

        await FailPost(0);
        Assert.Equal(1, await Failed(1));
        // the read every write ends on fails too
        Assert.Equal(2, Asked);
        await FailRead();
        AutoExport.Wake();
        Assert.Equal(2, Asked);
        Assert.Equal(1, Posts);

        AutoExport.Offer([Run("1a", "Crossing", 20), Run("1a", "Chasm", 30)], "1a");
        Assert.Equal(3, Asked);
        Assert.Equal(1, Posts);

        await AnswerRead(sheet);
        Assert.Equal(2, Posts);
        Assert.Equal(new[] { Crossing.Cp, Chasm.Cp }, Sent(1).Select(u => u.Cp));
        Assert.DoesNotContain(Infos, line => line.Contains(Url));
    }

    [Fact]
    public async Task AFailedRequestIsNotRetriedByChangedAlone() {
        string sheet = Sheet((Crossing, "25.000"));
        await Hold(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        await FailPost(0);

        // the sheet answers again and its cell did not move: the same request
        await AnswerRead(sheet);
        AutoExport.Wake();
        Assert.Equal(1, Posts);
        Assert.Equal(2, Asked);

        // only an improved row retries it
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(2, Posts);
    }

    [Fact]
    public async Task TwoOffersDuringAWriteGiveOneSendAfterIt() {
        await Hold(Sheet((Start, "45.000"), (Crossing, "25.000"), (Chasm, "35.000")));
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Posts);

        AutoExport.Offer([Run("1a", "Crossing", 20), Run("1a", "Chasm", 30)], "1a");
        AutoExport.Offer([Run("1a", "Crossing", 20), Run("1a", "Chasm", 30), Run("1a", "Start", 40)], "1a");
        Assert.Equal(1, Posts);
        Assert.Single(Infos, line => line == "not sent: WriteOut");

        await AnswerPost(0);
        Assert.Equal(2, Posts);
        Assert.Equal(new[] { Start.Cp, Chasm.Cp }, Sent(1).Select(u => u.Cp));

        await AnswerPost(1);
        Assert.Equal(2, Posts);
    }

    [Fact]
    public async Task ARefusalIsCountedOnceAndNotResent() {
        string sheet = Sheet((Crossing, "25.000"));
        await Hold(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");

        await AnswerPost(0, "notFound");
        Assert.Equal(1, await Failed(1));

        // the read after the write shows the same cell: the same request
        await AnswerRead(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        AutoExport.Wake();
        Assert.Equal(1, Posts);
        Assert.Equal(0, AutoExport.TakeFailed());
    }

    [Fact]
    public async Task AChangedAnswerIsNotCountedAndTheNewCellIsSent() {
        await Hold(Sheet((Crossing, "25.000")));
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");

        await AnswerPost(0, "changed");
        Assert.Equal(1, Posts);

        // the player typed a better time, still behind the session's
        await AnswerRead(Sheet((Crossing, "24.000")));
        Assert.Equal(2, Posts);
        Assert.Equal("24.000", Assert.Single(Sent(1)).Expect);

        await AnswerPost(1);
        await Task.Delay(50);
        Assert.Equal(0, AutoExport.TakeFailed());
    }

    [Fact]
    public async Task AStoppedSendCountsNothing() {
        // one row more than a request holds
        List<SheetRow> table = SheetRows.All.DistinctBy(SheetRows.TargetOf).Take(ExportBatches.Size + 1).ToList();
        string sheet = Sheet(table.Select(row => (SheetRows.TargetOf(row), "9:00.000")).ToArray());
        await Hold(sheet);
        AutoExport.Offer(table.Select(row => Run(row.Scope, row.Name, 20)).ToArray(), null);
        Assert.Equal(1, Posts);
        Assert.Equal(ExportBatches.Size, Sent(0).Count);

        setting = false;
        await AnswerPost(0);
        Assert.Equal(1, Posts);
        await Task.Delay(50);
        Assert.Equal(0, AutoExport.TakeFailed());

        // back on, the row never posted goes once the sheet has been read again
        setting = true;
        await AnswerRead(sheet);
        Assert.Equal(2, Posts);
        Assert.Single(Sent(1));
    }

    [Fact]
    public async Task DropForgetsTheOffer() {
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Asked);
        AutoExport.Drop();

        await AnswerRead(Sheet((Crossing, "25.000")));
        AutoExport.Wake();
        Assert.Equal(0, Posts);
    }

    [Fact]
    public async Task ARowTheSheetLacksIsNeverSent() {
        await Hold(Sheet((Crossing, "25.000")));
        AutoExport.Offer([Run("1a", "Chasm", 30)], "1a");
        AutoExport.Offer([Run("1a", "Chasm", 30)], "1a");
        Assert.Equal(0, Posts);
        Assert.Single(Infos, line => line.StartsWith("not on the sheet, never sent by itself: "));

        // beside a row the sheet holds, it stays behind
        AutoExport.Offer([Run("1a", "Crossing", 20), Run("1a", "Chasm", 30)], "1a");
        Assert.Equal(1, Posts);
        Assert.Equal(Crossing.Cp, Assert.Single(Sent(0)).Cp);
        Assert.Single(Infos, line => line.StartsWith("not on the sheet, never sent by itself: "));
    }

    [Fact]
    public async Task AThrowingEvaluationIsLoggedByTypeAndTheLoopGoesOn() {
        await Hold(Sheet((Crossing, "25.000")));
        bool thrown = false;
        AutoExport.Install(new AutoHost {
            Enabled = () => true,
            Setting = () => true,
            Url = () => Url,
            ScreenOpen = () => {
                if (!thrown) {
                    thrown = true;
                    throw new InvalidOperationException(Url);
                }

                return false;
            },
            Info = message => { lock (infos) { infos.Add(message); } },
            Run = action => action(),
        });

        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(0, Posts);
        Assert.Contains(nameof(InvalidOperationException), Assert.Single(Infos));
        Assert.DoesNotContain(Infos, line => line.Contains(Url));

        AutoExport.Wake();
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task InstallingAgainDoesNotStackWakes() {
        AutoExport.Install(Host());
        AutoExport.Install(Host());
        int runs = 0;
        run = action => { Interlocked.Increment(ref runs); action(); };
        setting = false;
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, runs);

        // one raise, one evaluation
        // Wake now sits after the counter Quiet reads: wait on the run itself
        await Hold(Sheet((Crossing, "25.000")));
        await Until(() => Volatile.Read(ref runs) == 2);
        await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    // a trigger that lands as the loop leaves must start another, never write a
    // 2 nothing reads: every later trigger of the session would be lost
    [Fact]
    public async Task AnOfferLandingAsAnEvaluationEndsIsNotLost() {
        await Hold(Sheet((Crossing, "25.000"), (Chasm, "35.000")));
        run = action => Task.Run(action);
        RunBook.Run[] slow = [Run("1a", "Crossing", 30)];
        RunBook.Run[] fast = [Run("1a", "Crossing", 20)];

        // each thread ends on the row that beats the sheet: the last array offered holds it.
        // Paced, and this many: back to back, the loop never leaves between two offers, and
        // a plain write of 2 in Schedule passed 200 unpaced offers every time
        void Offers() {
            Random pace = new();
            for (int i = 0; i < 49999; i++) {
                AutoExport.Offer(slow, "1a");
                Thread.SpinWait(pace.Next(300));
            }

            AutoExport.Offer(fast, "1a");
        }

        Thread one = new(Offers);
        Thread two = new(Offers);
        one.Start();
        two.Start();
        one.Join();
        two.Join();

        await Until(() => Posts == 1);
        await Quiet();
        Assert.Equal(1, Posts);
        Assert.Equal(Crossing.Cp, Assert.Single(Sent(0)).Cp);

        // and the worker still answers afterwards
        await AnswerPost(0);
        AutoExport.Offer([Run("1a", "Crossing", 20), Run("1a", "Chasm", 30)], "1a");
        await Until(() => Posts == 2);
        Assert.Equal(Chasm.Cp, Assert.Single(Sent(1)).Cp);
        await Quiet();
    }

    private static string OutOfDate(string sheet) => sheet.Replace("\"version\":3", "\"version\":2");

    // a read that finds a script of another version: nothing is taken in, nothing raised
    private async Task FindTheScriptOutOfDate(string sheet) {
        _ = SheetReader.Refresh("again");
        LastFetch.SetResult((OutOfDate(sheet), null));
        await Until(() => !SheetReader.IsReading);
        Assert.True(SheetReader.ScriptOutOfDate);
    }

    [Fact]
    public async Task AnOfferAgainstAnOutOfDateScriptAsksForOneReadAndItsAnswerSends() {
        string sheet = Sheet((Crossing, "25.000"));
        await Hold(sheet);
        await FindTheScriptOutOfDate(sheet);
        int asked = Asked;

        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(asked + 1, Asked);
        Assert.Equal(0, Posts);

        // the script was replaced: the read says so, and the offer goes
        await AnswerRead(sheet);
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task ARetryTheReaderRefusedIsKeptForTheNextEvaluation() {
        string sheet = Sheet((Crossing, "25.000"));
        await Hold(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        await FailPost(0);
        // the sheet answers, its cell unmoved: the unanswered request waits for an offer
        await AnswerRead(sheet);
        Assert.Equal(1, Posts);

        await FindTheScriptOutOfDate(sheet);
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Posts);

        // the retry that offer carried was refused, not spent
        await AnswerRead(sheet);
        Assert.Equal(2, Posts);
    }

    [Fact]
    public async Task AfterAFailedReadAnOfferAsksAgainAndStillSendsOnWhatIsHeld() {
        string sheet = Sheet((Crossing, "25.000"));
        await Hold(sheet);
        _ = SheetReader.Refresh("again");
        await FailRead();
        int asked = Asked;

        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");

        Assert.Equal(asked + 1, Asked);
        Assert.Equal(1, Posts);
    }

    [Fact]
    public async Task AnAutomaticSendStopsWhenTheUrlChanges() {
        List<SheetRow> table = SheetRows.All.DistinctBy(SheetRows.TargetOf).Take(ExportBatches.Size + 1).ToList();
        await Hold(Sheet(table.Select(row => (SheetRows.TargetOf(row), "9:00.000")).ToArray()));
        AutoExport.Offer(table.Select(row => Run(row.Scope, row.Name, 20)).ToArray(), null);
        Assert.Equal(1, Posts);

        url = "https://script.google.com/macros/s/B/exec";
        await AnswerPost(0);

        Assert.Equal(1, Posts);
        Assert.Equal(0, AutoExport.TakeFailed());
    }

    [Fact]
    public async Task ASchedulerThatThrowsDoesNotStopLaterOffers() {
        await Hold(Sheet((Crossing, "25.000")));
        run = _ => throw new InvalidOperationException();
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(0, Posts);
        // the fixture counted an evaluation that never ran
        Interlocked.Decrement(ref running);

        run = action => action();
        AutoExport.Offer([Run("1a", "Crossing", 20)], "1a");
        Assert.Equal(1, Posts);
    }
}
