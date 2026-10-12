using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Celeste.Mod.SpeedrunSheet;

/// One export as several requests. The script takes about half a second per
/// row that sets no Timing value (224 rows: 100 to 145 s, measured), about 3 s
/// for a row that sets a synced selector, and a read made during a request
/// never shows what it wrote: no request may near the client's timeout.
internal static class ExportBatches {
    // about 12 s in the script when every row is written and sets no Timing,
    // about 75 s when every one sets a synced selector
    public const int Size = 25;

    /// Sends the updates in order, each request after the previous answer: the
    /// script refuses a second one while the first holds its lock. Stops at the
    /// first request that fails, with its error. Results pairs with the first
    /// updates, whole requests only; posting is told each batch just before its
    /// request, answered how many after each. Stopped is true only when goOn said no.
    public static async Task<(List<ExportResult> Results, int? Ms, string Error, bool Stopped)> Send(
        IReadOnlyList<ExportUpdate> updates, Func<string, Task<(string body, string error)>> post,
        Func<bool> goOn, Action<int> answered, Action<IReadOnlyList<ExportUpdate>> posting) {
        List<ExportResult> results = [];
        int? ms = null;
        for (int from = 0; from < updates.Count; from += Size) {
            if (!goOn()) {
                // the log only: the screen that would show it is gone
                return (results, ms, "stopped: the sheet URL changed or the mod was switched off", true);
            }

            List<ExportUpdate> batch = updates.Skip(from).Take(Size).ToList();
            posting(batch);
            (string body, string error) = await post(
                ExportProtocol.SerializeRequest(new ExportRequest { Updates = batch }));
            ExportResponse response = null;
            if (error == null) {
                ExportProtocol.TryParseResponse(body, out response, out error);
            }

            // the script answers its updates in order, one result each: an
            // answer that does not pair up is trusted for nothing
            if (error == null && response.Results.Count != batch.Count) {
                error = $"{ExportProtocol.Localize("SRS_EXPORT_ERR_UNREADABLE")} {response.Results.Count}/{batch.Count}";
            }

            if (error != null) {
                return (results, ms, error, false);
            }

            results.AddRange(response.Results);
            if (response.Ms is { } took) {
                ms = (ms ?? 0) + took;
            }

            answered(results.Count);
        }

        return (results, ms, null, false);
    }
}
