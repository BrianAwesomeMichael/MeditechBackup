# MeditechBackup

Backs up every **In Progress**, **On Hold** and **Pending Zip** job from the file servers
to one or two local drives on the backup PC. It replaces adding bvckup2 jobs by hand.

| What | Where |
|---|---|
| Program to install | `dist\MeditechBackup.exe` (one file, no .NET install needed, Windows 10 or newer, 64-bit; not in git: build it) |
| Source code | https://github.com/BrianAwesomeMichael/MeditechBackup: `MeditechBackup\` (C#, .NET 9) |
| Status + settings on 201 | `http://192.168.1.201/Meditech/admin.php?view=backup_service` |
| On the backup PC | A window + tray icon (same page as the admin tab, plus **This PC** settings). Also `http://<backup-pc-ip>:8099/` from any browser |
| Server side (PHP) | `server\backupService\` here, deployed to `J:\htdocs\Meditech\backupService\` (`api.php`, `admin_tab.php`, `common.php`, `status.js`, `status.css`; `data\` only on the server) |

## How it works

- **Passes.** Every *N* hours (default 4) it gets the job list from 201 and backs up the jobs **one at a time**.
  The job that was backed up longest ago goes first. If a pass takes longer than *N* hours, the next one starts as soon as it ends.
- **Only copies what changed.** Each file's size and date are compared with the copy on the backup drive.
  Unchanged files are skipped without being opened. Copies go to a `.mbtmp` temp file that is renamed when finished,
  so a crash never leaves a half-written file.
- **Old versions.** When a file changes or is deleted on the server, the previous copy moves to
  `_versions\<Client>\<Job>\<date>\...` on the same drive and is deleted after the retention time:
  - In Progress / On Hold: 30 days (default)
  - Pending Zip and later: 0 days (just the current files)
  - Per-job overrides are set on the admin tab.
- **Status changes.**
  - **3. Zipping / 4. Zipped:** the copy is kept but not re-scanned.
  - **5. Complete / 6. Archived:** the copy is deleted (it's on tape).
  - **Not in the database any more:** the copy is kept and flagged.
- **Two drives.** Each new job goes to the drive with the most free space and stays there. If that drive fills up
  (it always leaves *reserve* GB free), the job continues on the other drive. At the end of a pass, if one drive is
  under 10% free and the other is over 25% free, one whole job moves across (*Rebalance*, can be turned off).
- **Safety.**
  - If a job's folder can't be found, or the share drops, nothing is deleted from the backup.
  - Folders that can't be read are left alone.
  - If 90% or more of a job's files suddenly disappear, removal stops and a warning is logged.
- **Existing (bvckup2) backups are reused in place.** For a job with no backup yet, it looks in the backup folder on
  each drive (up to 4 levels deep) for a folder with the same name as the job's server folder, or one starting with
  the job code (`MTH126`, `MTH126 - old name`). If it finds one, it backs up into that folder where it is, without
  moving it. bvckup2 keeps file dates, so unchanged files are skipped rather than recopied. Jobs with no existing folder go
  in `Jobs\<Client>\<Job>` (the sub-folder name can be changed under **This PC**). Turn off the bvckup2 jobs once this
  has taken over, so the two programs don't write the same folders.
- **Permanent folders.** Folders listed under *Permanent folders* on the admin tab (e.g. `\\192.168.1.14\Projects2\_Library`)
  are backed up every pass like a production job, whatever the database says. They show as `PERM-<folder name>`, and
  per-job overrides work on that code. Taking one off the list keeps its backup (flagged, like a job that left the database).
- **When a drive gets full.** When a backup drive has less free space than *Clean up below* (500 GB by default), the oldest
  things with nothing written in *Only delete things older than* (7 days) are deleted until it's back above it:
  - **Old versions** (on by default): `_versions` sets go sooner than *Keep old versions* says.
  - **Folders no job uses** (off until you tick it): anything in the backup folder that isn't a job's backup, e.g. bvckup2
    copies of jobs that went to tape before this program. A job's folder is never one of these, whatever its status.
    Job-like folders (`ABC123 - ...`) go as a whole; other folders are looked into (up to 4 levels), and loose files are left alone.
  - **Safe folders** are never deleted: a path in the backup folder (`Archive\Keep`, or `E:\Backups\Archive\Keep`) protects that
    folder and everything in it; a name (`_KEEP`, `*-keep`) protects any folder with that name, and anything containing one.
  - It runs at the start of a pass, before each job (at most every 30 minutes per drive), and straight away when a job runs out
    of space; that job is then tried again. It does nothing until job history has loaded.
  - The status page's **Cleanup** section lists the folders no job uses (worked out at the end of each pass, sizes remembered
    for a day), whether each can be deleted, and the last 50 things the cleanup deleted. Every deletion is also in the log.
- **Excludes.** Off by default. Add names like `_old`, `old`, `~old`, `*.tmp` on the admin tab.
- **Commands.** *Run now*, *Pause*, *Resume* and *Skip job* work from both the admin tab and the local page.

On each backup drive (with the backup folder set to `Backups`):

```
E:\Backups\<existing bvckup folder>\...                    adopted jobs carry on where they were
E:\Backups\Jobs\<Client>\<Job folder>\...                 new jobs (browse/restore from here)
E:\Backups\_versions\<Client>\<Job folder>\<date>\...     old versions
E:\Backups\_state\state.json                              job history (on every drive)
```
The job table shows each job's folder under its drive.

Restoring is a plain copy back out of these folders. Nothing is packed or encoded.

## Install on the backup PC

1. Make a folder, e.g. `C:\MeditechBackup\`, and copy `MeditechBackup.exe` into it. Run it.
2. A window opens on the **This PC** tab. Enter the drives (`E:` and `G:`) and the backup folder (`Backups`), press
   **Check** to see what's in those folders, then **Save**. Backups start straight away, with no restart.
   (These settings live in `MeditechBackup.json` next to the exe. Editing that file by hand also works and is picked up within a minute.)
3. Closing the window keeps it running in the tray (the icon's dot is blue while backing up, green while waiting,
   yellow when paused, red when there's a problem). Double-click the tray icon to reopen it. **Exit** is in the tray menu.
   Starting the exe again just brings the window back. Add `--minimized` to start it in the tray only.
4. Run it as a Windows user that can open `\\192.168.1.14\Projects2` (and any other share jobs live on).
   Your restart program can point at `MeditechBackup.exe` (`--minimized` optional). Only one copy runs at a time.
   - *Or* as a Windows service (no window; the second-start brings up the page in a browser instead):
     ```
     sc create MeditechBackup binPath= "C:\MeditechBackup\MeditechBackup.exe" start= delayed-auto obj= ".\backupuser" password= "..."
     sc failure MeditechBackup reset= 86400 actions= restart/60000/restart/60000/restart/60000
     sc start MeditechBackup
     ```
     Use an account that can reach the shares. The default LocalSystem account usually **can't** see network shares.
5. The window uses the Microsoft Edge WebView2 Runtime, which Windows 10/11 normally already has. If it's missing,
   the window offers a button to open the same page in the web browser instead.
6. To view the status page from other PCs, allow the port once (admin prompt):
   `netsh advfirewall firewall add rule name="MeditechBackup" dir=in action=allow protocol=TCP localport=8099`
   (Settings under *This PC* can only be changed on the backup PC itself.)

**Moving to a new PC:** stop the old one, move the drives (or copy the `MeditechBackup` folders), copy the exe and
`MeditechBackup.json` over, and fix the drive letters if they changed. History and versions live on the drives.

Logs are in `logs\backup-YYYY-MM-DD.log` next to the exe and are kept for 60 days.

## Building

```
cd MeditechBackup
dotnet publish -c Release -o ..\dist
```
The status page's `status.js` / `status.css` are built into the exe from `server\backupService\`.
After editing them, copy `server\backupService\*` (not `data\`) to `J:\htdocs\Meditech\backupService\` and rebuild the
exe, so the admin tab and the local page match.
