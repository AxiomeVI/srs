using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Celeste.Mod.SpeedrunSheet;

/// The saved copy of the player's sheet times, Saves/srs/sheet-times.json:
/// its format only. SheetReader owns the file, its lock and its log.
internal static class SheetCopy {
    public const int Format = 2;
    public const string AnotherSheet = "another sheet's";

    private sealed class CopyFile {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("sheet")] public string Sheet { get; set; } = "";
        [JsonPropertyName("savedAt")] public string SavedAt { get; set; } = "";
        [JsonPropertyName("rows")] public List<RemoteRow> Rows { get; set; } = [];
    }

    // emoji stay literal, as in the script's own answers
    private static readonly JsonSerializerOptions Options = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// Tells one sheet's copy from another's without holding the URL, which is
    /// the credential: 64 bits of its SHA-256 can neither be turned back into
    /// it nor used to call it.
    public static string Fingerprint(string url) {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string Serialize(string url, DateTime savedAtUtc, IReadOnlyList<RemoteRow> rows) =>
        JsonSerializer.Serialize(new CopyFile {
            Format = Format,
            Sheet = Fingerprint(url),
            SavedAt = savedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            Rows = [.. rows],
        }, Options);

    /// Never throws: why says what was wrong, for the log.
    public static bool TryParse(string json, string url, out List<RemoteRow> rows, out DateTime savedAtUtc,
        out string why) {
        rows = null;
        savedAtUtc = default;
        CopyFile file;
        try {
            file = JsonSerializer.Deserialize<CopyFile>(json, Options);
        } catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) {
            why = "unreadable: " + e.GetType().Name;
            return false;
        }

        if (file == null) {
            why = "empty";
            return false;
        }

        if (file.Format != Format) {
            why = $"format {file.Format}";
            return false;
        }

        if (file.Sheet != Fingerprint(url)) {
            why = AnotherSheet;
            return false;
        }

        if (!DateTime.TryParse(file.SavedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTime savedAt)) {
            why = "no date";
            return false;
        }

        savedAtUtc = savedAt.ToUniversalTime();
        rows = file.Rows?.FindAll(row => row != null) ?? [];
        foreach (RemoteRow row in rows) {
            row.Tab ??= "";
            row.Band ??= "";
            row.Chapter ??= "";
            row.Cp ??= "";
            row.Time ??= "";
        }

        why = null;
        return true;
    }
}
