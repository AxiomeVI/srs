namespace Celeste.Mod.SpeedrunSheet;

// its own key: the released mod shares every other one, and would show this title
[SettingName("MODOPTIONS_SRS_BETA_TITLE")]
public class SrsSettings : EverestModuleSettings {
    // master switch: off, the mod is inert and Mod Options shows only this
    // toggle. Built by hand in ModMenu, whose Change handler folds the rest
    [SettingIgnore]
    public bool Enabled { get; set; } = true;

    // the Standards tabs srs reads (StandardsTabs), as full edit URLs, editable
    // only in the settings file. The property names stay: they are the stored
    // keys
    [SettingIgnore]
    public string ASidesUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.ASides);

    [SettingIgnore]
    public string BSidesUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.BSides);

    [SettingIgnore]
    public string FarewellUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.Farewell);

    [SettingIgnore]
    public string CSidesUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.CSides);

    [SettingIgnore]
    public string ArbUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.Arb);

    [SettingIgnore]
    public string FcUrl { get; set; } = StandardsTabs.DefaultUrl(StandardsTab.Fc);

    internal string UrlOf(StandardsTab tab) => tab switch {
        StandardsTab.ASides => ASidesUrl,
        StandardsTab.BSides => BSidesUrl,
        StandardsTab.Farewell => FarewellUrl,
        StandardsTab.CSides => CSidesUrl,
        StandardsTab.Arb => ArbUrl,
        StandardsTab.Fc => FcUrl,
        _ => throw new System.ArgumentOutOfRangeException(nameof(tab)),
    };

    internal void SetUrl(StandardsTab tab, string url) {
        switch (tab) {
            case StandardsTab.ASides: ASidesUrl = url; break;
            case StandardsTab.BSides: BSidesUrl = url; break;
            case StandardsTab.Farewell: FarewellUrl = url; break;
            case StandardsTab.CSides: CSidesUrl = url; break;
            case StandardsTab.Arb: ArbUrl = url; break;
            case StandardsTab.Fc: FcUrl = url; break;
            default: throw new System.ArgumentOutOfRangeException(nameof(tab));
        }
    }

    // the tier's name in the rows drawn above the room timer
    [SettingIgnore]
    public bool ShowTier { get; set; } = true;

    // the rows' other parts ("1a Crossing", "14.875", "PB -0.214",
    // "+0.140 to Purple 1"); menu toggles only
    [SettingIgnore]
    public bool ShowCheckpointName { get; set; } = true;

    [SettingIgnore]
    public bool ShowTime { get; set; } = true;

    [SettingIgnore]
    public bool ShowPbImprovement { get; set; } = true;

    [SettingIgnore]
    public bool ShowDelta { get; set; } = true;

    // the outline of the darkest tier colors, for testers to compare
    [SettingIgnore]
    public DarkOutline DarkOutline { get; set; } = DarkOutline.Tint50;

    // sends a session best that beats the sheet as soon as its row ends; off by default
    [SettingIgnore]
    public bool AutoExport { get; set; }

    // a popup when an automatic export wrote every row it sent; a failure always shows
    [SettingIgnore]
    public bool AutoExportPopup { get; set; } = true;

    // local date the URL was last set, "yyyy-MM-dd". Display only: it lets the
    // status line say "set <date>" without showing the URL
    [SettingIgnore]
    public string ExportUrlSetOn { get; set; } = "";

    // opens the export screen; unbound by default
    [SettingIgnore]
    public ButtonBinding OpenExportMenu { get; set; } = new();

    // steps the rows above the timer back through the attempt's segments;
    // unbound by default
    [SettingIgnore]
    public ButtonBinding PreviousSegment { get; set; } = new();
}
