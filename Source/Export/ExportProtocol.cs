using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Celeste.Mod.SpeedrunSheet;

public sealed class ExportUpdate {
    [JsonPropertyName("tab")] public string Tab { get; set; } = "";
    /// the band the row was read from: the script then searches it alone.
    /// Empty searches every band of the tab
    [JsonPropertyName("band")] public string Band { get; set; } = "";
    [JsonPropertyName("chapter")] public string Chapter { get; set; } = "";
    [JsonPropertyName("cp")] public string Cp { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    /// what srs believed the cell held, as the sheet displayed it. The script
    /// refuses the row rather than writing when it no longer matches.
    [JsonPropertyName("expect")] public string Expect { get; set; } = "";
    /// the value to set the row's Timing cell to; absent when srs sets none
    [JsonPropertyName("timing")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Timing { get; set; }
    /// what srs believed the Timing cell held
    [JsonPropertyName("expectTiming")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ExpectTiming { get; set; }
    /// the buffer, in seconds, the time was judged through
    [JsonPropertyName("expectBuffer")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ExpectBuffer { get; set; }
}

public sealed class ExportRequest {
    [JsonPropertyName("updates")] public List<ExportUpdate> Updates { get; set; } = [];
}

public sealed class ExportResult {
    [JsonPropertyName("tab")] public string Tab { get; set; } = "";
    [JsonPropertyName("band")] public string Band { get; set; } = "";
    [JsonPropertyName("chapter")] public string Chapter { get; set; } = "";
    [JsonPropertyName("cp")] public string Cp { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    /// set, kept or refused; empty when the update carried no Timing
    [JsonPropertyName("timing")] public string Timing { get; set; } = "";
}

/// The script answers with results, or with error and nothing else: there is
/// no "ok" on the wire.
public sealed class ExportResponse {
    [JsonPropertyName("results")] public List<ExportResult> Results { get; set; } = [];
    [JsonPropertyName("error")] public string Error { get; set; }
    [JsonPropertyName("ms")] public int? Ms { get; set; }
    [JsonPropertyName("version")] public int? Version { get; set; }
}

public sealed class RemoteRow {
    [JsonPropertyName("tab")] public string Tab { get; set; } = "";
    [JsonPropertyName("band")] public string Band { get; set; } = "";
    [JsonPropertyName("chapter")] public string Chapter { get; set; } = "";
    [JsonPropertyName("cp")] public string Cp { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    // the five below exist only on a row with a Timing dropdown (or a helper
    // column), and are left out of the saved copy when absent
    [JsonPropertyName("timing")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Timing { get; set; }
    [JsonPropertyName("timingOptions")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> TimingOptions { get; set; }
    [JsonPropertyName("timingGroup")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string TimingGroup { get; set; }
    [JsonPropertyName("fileTimeBuffer")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FileTimeBuffer { get; set; }
    [JsonPropertyName("rankOffset")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RankOffset { get; set; }
}

internal sealed class RowsResponse {
    [JsonPropertyName("rows")] public List<RemoteRow> Rows { get; set; }
    // how long the script itself took, so the wait can be split between its
    // work and Google's dispatch
    [JsonPropertyName("ms")] public int? Ms { get; set; }
    [JsonPropertyName("cached")] public bool Cached { get; set; }
    [JsonPropertyName("error")] public string Error { get; set; }
    [JsonPropertyName("version")] public int? Version { get; set; }
}

/// Why an answer holds no rows: it is not the script's, the script refused
/// with a reason of its own, or it speaks another protocol version.
public enum RowsFailure { None, NotTheScript, Refused, OutOfDate }

/// Wire format between the mod and the player's Apps Script Web App.
/// Never throws: every failure path returns false with a human-readable message.
public static class ExportProtocol {
    /// The only version of the script srs speaks. Absent means a version 1
    /// script, which the player replaces by copying the script again.
    public const int WireVersion = 3;

    /// Set to Dialog.Clean by ExportMenu.Load(). This file is compiled straight
    /// into the test project, which has no game reference, so the lookup has to
    /// come in from outside; unset, it hands back the key.
    public static Func<string, string> Localize = key => key;

    private static readonly JsonSerializerOptions Options = new() {
        // emoji stay literal, as the script sends them
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// A deployed Apps Script Web App endpoint. The /dev URL of the same script
    /// answers a signed-out client with a login page, so it is refused here.
    public static bool IsEndpointUrl(string url) {
        if (string.IsNullOrWhiteSpace(url)) {
            return false;
        }

        // a copy broken over two lines parses, escaped, and no request can use it
        string trimmed = url.Trim();
        return !trimmed.Any(char.IsWhiteSpace)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out Uri uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal);
    }

    public static string SerializeRequest(ExportRequest request) =>
        JsonSerializer.Serialize(request, Options);

    public static bool TryParseResponse(string body, out ExportResponse response, out string error) {
        response = null;
        if (!Guard(body, out error)) {
            return false;
        }
        try {
            response = JsonSerializer.Deserialize<ExportResponse>(body, Options);
        } catch (JsonException e) {
            error = Localize("SRS_EXPORT_ERR_UNREADABLE") + " " + e.Message;
            return false;
        }
        if (response == null) {
            error = Localize("SRS_EXPORT_ERR_EMPTY");
            return false;
        }
        // same shape as TryParseRows: the script answers with results or with
        // error, never both, so a body carrying error is a failed parse and not
        // a response the caller has to inspect for one
        if (!string.IsNullOrEmpty(response.Error)) {
            error = response.Error;
            response = null;
            return false;
        }
        if (response.Version != WireVersion) {
            error = VersionError(response.Version);
            response = null;
            return false;
        }
        // an explicit null overrides the property's default, for the list, an
        // entry or a field alike, and a throw in the caller's continuation
        // would leave the screen on "Writing to the sheet..."
        response.Results = response.Results?.FindAll(r => r != null) ?? [];
        foreach (ExportResult r in response.Results) {
            r.Tab ??= "";
            r.Band ??= "";
            r.Chapter ??= "";
            r.Cp ??= "";
            r.Status ??= "";
            r.Reason ??= "";
        }
        error = null;
        return true;
    }

    // a script ahead of the mod cannot be fixed by copying the script again
    private static string VersionError(int? version) =>
        Localize(version > WireVersion ? "SRS_EXPORT_ERR_MOD_OUT_OF_DATE" : "SRS_EXPORT_ERR_OUT_OF_DATE");

    public static bool TryParseRows(string body, out List<RemoteRow> rows, out string scriptTiming,
        out string error, out RowsFailure failure) {
        scriptTiming = null;
        rows = null;
        failure = RowsFailure.NotTheScript;
        if (!Guard(body, out error)) {
            return false;
        }
        RowsResponse wrapper;
        try {
            wrapper = JsonSerializer.Deserialize<RowsResponse>(body, Options);
        } catch (JsonException e) {
            error = Localize("SRS_EXPORT_ERR_UNREADABLE") + " " + e.Message;
            return false;
        }
        if (wrapper == null) {
            error = Localize("SRS_EXPORT_ERR_EMPTY");
            return false;
        }
        if (!string.IsNullOrEmpty(wrapper.Error)) {
            failure = RowsFailure.Refused;
            error = wrapper.Error;
            return false;
        }
        if (wrapper.Version != WireVersion) {
            failure = RowsFailure.OutOfDate;
            error = VersionError(wrapper.Version);
            return false;
        }
        if (wrapper.Rows == null) {
            error = Localize("SRS_EXPORT_ERR_NO_ROWS");
            return false;
        }
        // same reason as Results: the reader takes them in inside a continuation
        rows = wrapper.Rows.FindAll(r => r != null);
        foreach (RemoteRow r in rows) {
            r.Band ??= "";
        }
        scriptTiming = wrapper.Ms is { } ms
            ? $"{ms} ms in the script{(wrapper.Cached ? ", cached" : "")}"
            : "script timing unknown";
        failure = RowsFailure.None;
        error = null;
        return true;
    }

    /// An HTML body means Google served a login page: the Web App was deployed with
    /// "Only myself" instead of "Anyone", so a plain HTTP request gets rejected.
    private static bool Guard(string body, out string error) {
        if (string.IsNullOrWhiteSpace(body)) {
            error = Localize("SRS_EXPORT_ERR_EMPTY");
            return false;
        }
        string head = body.TrimStart();
        if (head.StartsWith("<", StringComparison.Ordinal)) {
            error = Localize("SRS_EXPORT_ERR_LOGIN_PAGE");
            return false;
        }
        error = null;
        return true;
    }
}
