using System.Text.RegularExpressions;

namespace Celeste.Mod.SpeedrunSheet;

// Where the reference workbook lives, and how to move a player still pointed at
// the previous one. Game-free, so tested. [SettingIgnore] does not stop the tab
// URLs being serialized, and a stored value beats a new default: repointing the
// constants alone moves nobody who has ever saved settings.
public static class SheetUrls {
    // the reference workbook srs reads
    public const string ReferenceId = "1Gjr0t5Ncl30SnD34HYvdihVZToMMau3L2mw-b6XWSDY";

    // its frozen predecessor, which still answers: a player left on it would
    // miss every later retiming. To migrate away from, never a default
    private const string FrozenId = "18iSckSLnGQw13Ql_mpMLSVRbJKllp0lWZI6U0gP8x0Y";

    // StandardsTabs.DefaultUrl builds the settings' defaults from this, so a
    // future move cannot repoint the constants and forget the migration
    public const string EditUrlPrefix =
        "https://docs.google.com/spreadsheets/d/" + ReferenceId + "/edit?gid=";

    /// Substitutes the frozen spreadsheet id, and nothing else, in a stored
    /// URL; null when there is nothing to do. The reference is a Drive copy of
    /// the frozen workbook, which preserves sheetIds, so a gid a player changed
    /// still names the same tab. Any other workbook's URL is left untouched.
    public static string Migrate(string url) {
        if (string.IsNullOrEmpty(url)) {
            return null;
        }

        // anchored on the /d/ segment and stopped before any further id
        // character, so this matches the id as a whole token and not as a prefix
        string migrated = Regex.Replace(url, "(?<=/d/)" + FrozenId + "(?![-\\w])", ReferenceId);
        return migrated == url ? null : migrated;
    }

    /// The no-auth CSV export URL of the tab an edit URL names, its gid taken
    /// from ?gid=, #gid= or &gid= (tab 0 when none). Null for anything without
    /// a /d/<id> segment, a bare id included: settings hold full edit URLs.
    public static string CsvUrlOf(string sheetUrl) {
        if (string.IsNullOrWhiteSpace(sheetUrl)) {
            return null;
        }

        Match id = Regex.Match(sheetUrl, @"/d/([\w-]+)");
        if (!id.Success) {
            return null;
        }

        Match gid = Regex.Match(sheetUrl, @"[?#&]gid=(\d+)");
        return $"https://docs.google.com/spreadsheets/d/{id.Groups[1].Value}/export?format=csv&gid={(gid.Success ? gid.Groups[1].Value : "0")}";
    }
}
