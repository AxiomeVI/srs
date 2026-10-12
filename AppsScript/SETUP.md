# Connecting srs to your practice sheet

You need **your own copy** of the practice sheet template, and srs's endpoint inside it.
Deploying that endpoint is what gives srs a private URL it can write your times to.

**The template already carries that endpoint**, as `srsExport.gs`, so there is nothing to
paste in: you only deploy it.

Nothing in it runs until you deploy. It adds no trigger, and its two entry points are
reachable only through a deployment URL, so an undeployed copy behaves exactly as before.

## Deploy

1. Open your copy of the sheet, then **Extensions > Apps Script**.
2. Check that `srsExport.gs` is in the file list on the left. If it is not, your copy
   predates it: make a new copy from the current template.
3. **Deploy > New deployment**. Click the gear next to *Select type* and pick **Web app**,
   then set the two fields that decide everything:
   - *Execute as*: **Me**
   - *Who has access*: **Anyone**
4. Authorise the script when Google asks. This is where the two warning screens appear;
   the section below says what they are asking for and why.
5. Copy the generated `/exec` URL. It ends in `/exec`, never `/dev`: the `/dev` URL is the
   editor's own test address, it requires you to be logged in, and srs cannot use it.
6. In Celeste, with the URL still in your clipboard: **Mod Options > Speedrun Sheet >
   Set Sheet URL from clipboard**. It works from a paused level as well as from the title
   screen, and it refuses anything that is not https ending in `/exec`, then asks the
   endpoint whether it answers as srs's own. Once one is set, the button reads *Replace
   Sheet URL from clipboard*.

If the script is ever updated, putting the new code in your sheet is not enough on its own:
the deployed URL keeps serving the old code until you publish a new version. **Deploy >
Manage deployments >** pencil icon **> Version: New version > Deploy**. The URL does not
change.

## The two screens Google shows you, and what they mean

**"Google hasn't verified this app."** Correct, and it cannot be otherwise. Verification
applies to one OAuth client, and your copy of the sheet is its own script project with its
own client. Even a verified template would not carry that over to a single copy: the app
you are authorising is yours, not somebody else's. Click **Advanced**, then **Go to ...
(unsafe)**. It is your own script, in your own document.

**The permission list.** It names Google Sheets, and it is worth reading precisely, because
it asks for more than this endpoint uses:

- *See, edit, create, and delete all your Google Sheets spreadsheets.* This one is for the
  **sheet's own** scripts, not srs. They read two other documents: the shared reference
  workbook, to refresh your standards, and your old sheet, for *Import from old sheet*. They
  also write formatting through an interface that cannot be narrowed to one file.
- *Display and run third-party web content in prompts and sidebars.* The sheet's own menus
  and dialogs.

If the screen shows a checkbox next to each line, **leave them all ticked**. Apps Script
grants permissions per project, never per function, so this endpoint runs on that same
Sheets line: untick it and the export fails with a permission error that names nothing you
would recognise. What the endpoint itself touches is the document it lives in, and nothing
else. It opens no other spreadsheet, and it makes no outbound request of any kind.

## "Who has access" must be "Anyone"

"Only myself" sounds like the private, safer choice. It is not compatible with this mod:
Google then demands an OAuth token on every call and answers a plain HTTP request with a
login page, so every sync fails. Only "Anyone" makes the URL itself sufficient.

"Execute as: Me" means the script writes with *your* permissions: the sheet itself never has
to be shared with anyone.

## Keeping it safe

**Your `/exec` URL is a password.** Anyone who has it can read the times in your sheet and
write over them. Keep it to yourself:

- Never write it inside the sheet: not in a cell, a comment or the description.
- Don't paste it when asking for help. srs keeps it in `Saves/srs/export-url.txt`, not in its
  settings file, so a settings file you share does not carry it; don't share that one.
- If it leaks, revoke it: **Deploy > Manage deployments >** archive the deployment, create a
  new one, and set the new URL in Mod Options.

**Edit access to your sheet is access to its script.** Anyone with Edit access can change the
script, and changed code can run with your Google permissions, which cover all your Google
Sheets, the next time you use the sheet's menu or update the deployment. Give Edit access only
to people you would trust with your Google account.

**View access is safe to share.** It lets people read the sheet, but not open its script or its
deployment from it. A viewer who makes their own copy gets a copy of the script, but no
deployment and no URL: deployments are never copied.

## What the script reads and writes

**Which tabs.** It works only on the entry tabs your sheet's **Config** tab names, in cells C6,
C7 and C9: on the template, `A Sides`, `B+C Sides`, `Farewell` and `ARB/Full Clear`, the tabs you
fill in. srs itself knows rows of all four. srs writes entry tabs only: the category tabs read
from them, and writing into one would turn its cells manual and break its auto-fill. Setting a
Timing cell makes the sheet's own selector sync update the cells tied to it, which can sit on
other tabs, when Config's *Sync selectors* allows it. The tab list is read from those three cells on every call: a tab they stop naming
is no longer written, and if they name none, for example because a copy moved them, every call
fails with an error.

**Which rows.** Rows are found by their labels, never by position, in each table of a tab,
starting from its `Time` header. Inserting rows above or inside a table is fine. Renaming a
tab, a checkpoint, or a table's `Time`, `Standard` or `Date` header is not: srs then reports the
rows as not found and leaves them alone.

**What it writes.** On a matched row, the **Time** cell, the **Date** cell when Config's
*Auto-fill dates* is Yes, and the row's **Timing** cell where it has one and srs can vouch for
its value (cells tied to it follow when Config's *Sync selectors* is on). Nothing else: the
**Standard** column is a formula and is never touched, and a Time cell holding a formula is
refused.

**What it checks first.** Before writing, it compares the Time cell with what srs last read from
your sheet, or from its saved copy of it. If you changed it in the browser meanwhile, the row
comes back as *sheet changed* and is left alone, and the rest of the export goes through. A row
that already holds the time being exported is left untouched, date included.

**Frame check.** If Config's *Check time validity* is Yes and the time is not a whole number of
frames, the script marks the cell exactly as typing it would: struck through, bold, with a note
giving the two nearest valid times, which replaces any note there. When the time is valid it
removes only its own mark, and a note you wrote stays.

**Its cache.** The script keeps its last answer for five minutes rather than reading your sheet
again on every open, and throws it away after every export. An export is never read
back stale, but an edit you make by hand in the browser can take up to five minutes to appear.

**Review before confirming.** A time already in the sheet is overwritten by the ticked row
without asking.
