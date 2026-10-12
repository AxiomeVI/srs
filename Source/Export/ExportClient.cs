using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.SpeedrunSheet;

/// Talks to the player's Apps Script Web App. Never runs on the game thread and
/// never throws: a failure comes back as a message in the second tuple slot,
/// through ExportProtocol.Localize, which is safe before Dialog has loaded.
internal static class ExportClient {
    private const string LogTag = "srs";

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    // a request of ExportBatches.Size rows takes the script about 12 s (about
    // 75 s if each sets a synced selector), plus up to 30 s waiting on its
    // lock: the rest is margin for a slow day
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(180);

    // each request carries its own deadline
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public static Task<(string body, string error)> FetchAsync(string url) =>
        SendAsync(url, null);

    public static Task<(string body, string error)> PostAsync(string url, string json) =>
        SendAsync(url, json);

    private static Task<(string body, string error)> SendAsync(string url, string json) =>
        Task.Run(async () => {
            // timed: an Apps Script cold start and an oversized payload look the
            // same from the game
            Stopwatch clock = Stopwatch.StartNew();
            using CancellationTokenSource deadline = new(json == null ? ReadTimeout : WriteTimeout);
            try {
                using HttpResponseMessage response = json == null
                    ? await Http.GetAsync(url, deadline.Token)
                    : await Http.PostAsync(url,
                        new StringContent(json, Encoding.UTF8, "application/json"), deadline.Token);

                string body = await response.Content.ReadAsStringAsync(deadline.Token);
                Logger.Log(LogLevel.Info, LogTag,
                    $"{(json == null ? "read" : "write")} took {clock.ElapsedMilliseconds} ms,"
                    + $" {body?.Length ?? 0} chars back");
                if (!response.IsSuccessStatusCode) {
                    // never log the URL: it is a secret
                    Logger.Log(LogLevel.Warn, LogTag, $"export request failed: {(int) response.StatusCode}");
                    return (null, $"{ExportProtocol.Localize("SRS_EXPORT_ERR_STATUS")} {(int) response.StatusCode}.");
                }
                return (body, (string) null);
            } catch (OperationCanceledException) {
                Logger.Log(LogLevel.Warn, LogTag,
                    $"export request timed out after {clock.ElapsedMilliseconds} ms");
                // a write may have run to completion server-side: its timeout
                // says nothing about whether the sheet was written. A read's does
                return (null, ExportProtocol.Localize(json == null
                    ? "SRS_EXPORT_ERR_READ_TIMEOUT"
                    : "SRS_EXPORT_ERR_TIMEOUT"));
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, LogTag,
                    $"export request failed after {clock.ElapsedMilliseconds} ms: " + e.Message);
                // the message is the runtime's, in English: the log keeps it
                return (null, ExportProtocol.Localize("SRS_EXPORT_ERR_UNREACHABLE"));
            }
        });
}
