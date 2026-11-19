# Connecting srs to your practice sheet

srs writes your times through a small script that comes with your own copy of the practice sheet template (`srsExport.gs`). There's nothing to paste in: you only deploy it, which gives srs a private URL to write to. Until you deploy it, the script does nothing.

## Deploy

1. Open your copy of the sheet, then **Extensions > Apps Script**. If `srsExport.gs` isn't in the file list, your copy is too old: make a new one from the current template.
2. **Deploy > New deployment**, click the gear next to *Select type* and pick **Web app**. Set *Execute as* to **Me** and *Who has access* to **Anyone**.
3. Authorise the script when Google asks (the two screens it shows are explained below).
4. Copy the URL ending in `/exec`. The `/dev` one is the editor's test address and won't work.
5. In Celeste: **Mod Options > Speedrun Sheet > Your Sheet > Set Sheet Web App URL from clipboard**. It works from the title screen or a paused level, and checks that the URL answers before keeping it.

If the script is updated later, publish a new version or the URL keeps running the old code: **Deploy > Manage deployments >** pencil icon **> Version: New version > Deploy**. The URL stays the same.

## What Google asks you

**"Google hasn't verified this app."** Your copy is its own script project, so it can never be verified. Click **Advanced**, then **Go to ... (unsafe)**: it's your own script in your own document.

**The permission list** asks for access to all your Google Sheets. That's for the sheet's own scripts, which read the shared reference workbook and your old sheet. Leave every box ticked: Apps Script grants permissions to the whole project, so unticking one breaks the export with an unclear error. The srs script itself only touches the document it lives in and makes no outside requests.

**Why "Anyone"?** With "Only myself", Google wants a login on every call, and every sync fails. "Execute as: Me" means the script writes with your permissions, so you never have to share the sheet.

## Keeping it safe

**Your `/exec` URL is a password.** Anyone who has it can read and overwrite your times.

- Never put it in the sheet, not in a cell, a comment or the description.
- Copy something else afterwards, so it doesn't stay on your clipboard.
- Don't paste it when asking for help. srs keeps it in `Saves/srs/export-url.txt`, not in its settings file, so don't share that file.
- If it leaks, archive the deployment in **Deploy > Manage deployments**, make a new one, and set the new URL.

**Only give Edit access to people you'd trust with your Google account.** Someone who can edit the sheet can change its script, and the script runs with your permissions. View access is safe: viewers can't see the script, and a copy they make has no deployment and no URL.

## What the script writes

It only writes the entry tabs named in your **Config** tab (C6, C7 and C9): `A Sides`, `B+C Sides`, `Farewell` and `ARB/Full Clear` on the template. It never writes the category tabs.

Rows are found by their labels, so inserting rows is fine. Renaming a tab, a checkpoint, or a table's `Time`, `Standard` or `Date` header isn't: srs reports those rows as not found and leaves them alone.

On a matched row, it writes the **Time**, the **Date** if Config's *Auto-fill dates* is on, and the **Timing** where srs knows it. Cells tied to that Timing follow it if *Sync selectors* is on. It never touches **Standard**, and it refuses a Time cell that holds a formula.

A few more things to know:

- **It checks before writing.** If you changed a time in the browser since srs last read it, that row comes back as *sheet changed* and is skipped. The rest of the export goes through.
- **Frame check.** With *Check time validity* on, a time that isn't a whole number of frames gets marked as if you'd typed it: struck through, bold, with a note giving the nearest valid times.
- **A five-minute cache.** An edit you make in the browser can take up to five minutes to show up in srs. An export clears the cache.
- **Ticked rows overwrite without asking.** Check the export screen before confirming. **Auto Export on PB** (off by default) skips that screen entirely.
