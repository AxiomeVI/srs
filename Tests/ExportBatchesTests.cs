using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Celeste.Mod.SpeedrunSheet.Tests;

public class ExportBatchesTests {
    private static List<ExportUpdate> Updates(int count) =>
        Enumerable.Range(0, count).Select(i => new ExportUpdate { Tab = "A Sides", Chapter = "1a", Cp = $"row {i}" }).ToList();

    // answers every update of a request as written, echoing its cp
    private static string Answer(string json, int ms = 10, int drop = 0) {
        ExportRequest request = JsonSerializer.Deserialize<ExportRequest>(json);
        var results = request.Updates.Skip(drop).Select(u => new { tab = u.Tab, chapter = u.Chapter, cp = u.Cp, status = "written" });
        return JsonSerializer.Serialize(new { results, ms, version = 3 });
    }

    [Fact]
    public async Task EveryRowIsSentOnceInOrderAndNoRequestIsOverTheSize() {
        List<int> sizes = [];
        List<int> progress = [];
        (List<ExportResult> results, int? ms, string error) = await ExportBatches.Send(
            Updates(2 * ExportBatches.Size + 3),
            json => {
                sizes.Add(JsonSerializer.Deserialize<ExportRequest>(json).Updates.Count);
                return Task.FromResult((Answer(json), (string) null));
            },
            () => true, progress.Add);

        Assert.Null(error);
        Assert.Equal([ExportBatches.Size, ExportBatches.Size, 3], sizes);
        Assert.Equal([ExportBatches.Size, 2 * ExportBatches.Size, 2 * ExportBatches.Size + 3], progress);
        Assert.Equal(Enumerable.Range(0, 2 * ExportBatches.Size + 3).Select(i => $"row {i}"), results.Select(r => r.Cp));
        Assert.Equal(30, ms);
    }

    [Fact]
    public async Task ARequestIsSentOnlyAfterThePreviousAnswer() {
        List<TaskCompletionSource<(string, string)>> pending = [];
        List<string> bodies = [];
        Task<(List<ExportResult> Results, int? Ms, string Error)> send = ExportBatches.Send(
            Updates(ExportBatches.Size + 1),
            json => {
                bodies.Add(json);
                pending.Add(new TaskCompletionSource<(string, string)>());
                return pending[^1].Task;
            },
            () => true, _ => { });

        Assert.Single(pending);
        pending[0].SetResult((Answer(bodies[0]), null));
        Assert.Equal(2, pending.Count);
        pending[1].SetResult((Answer(bodies[1]), null));
        Assert.Equal(ExportBatches.Size + 1, (await send).Results.Count);
    }

    [Theory]
    // the request does not answer, answers with the script's error, or with fewer results than updates
    [InlineData("timeout")]
    [InlineData("script error")]
    [InlineData("short")]
    public async Task AFailedRequestStopsTheExportAndKeepsWhatWasAnswered(string failure) {
        int requests = 0;
        (List<ExportResult> results, _, string error) = await ExportBatches.Send(
            Updates(3 * ExportBatches.Size),
            json => {
                requests++;
                if (requests < 2) {
                    return Task.FromResult((Answer(json), (string) null));
                }

                return Task.FromResult(failure switch {
                    "timeout" => ((string) null, "no answer"),
                    "script error" => ("""{"error":"lock timeout","version":3}""", (string) null),
                    _ => (Answer(json, drop: 1), (string) null),
                });
            },
            () => true, _ => { });

        Assert.NotNull(error);
        Assert.Equal(2, requests);
        Assert.Equal(ExportBatches.Size, results.Count);
    }

    [Fact]
    public async Task NothingMoreIsSentOnceTheTargetIsGone() {
        int requests = 0;
        (List<ExportResult> results, _, string error) = await ExportBatches.Send(
            Updates(3 * ExportBatches.Size),
            json => {
                requests++;
                return Task.FromResult((Answer(json), (string) null));
            },
            () => requests < 1, _ => { });

        Assert.NotNull(error);
        Assert.Equal(1, requests);
        Assert.Equal(ExportBatches.Size, results.Count);
    }
}
