# Changelog

What changed in each release of OwlsEye - Orbit Access Matrix. From 1.0.0 on, versions follow semantic versioning: a
release that needs something done to config.json, the shared settings or the state folder gets a new major version.
The GitHub release of each version shows its section of this file.

## [1.0.0] - 2026-10-06

First stable release. owlseye shows and edits the folder rights of a Windows file share as a matrix of groups and
folders, and writes them as NTFS ACLs: with a preview of what changes for every user, a log with undo, a desired state
that reports changes made outside owlseye, findings and an access rights report for audits.

- The version is shown next to "owlseye" in the header.
- Release notes: this file, shown with each GitHub release.

## [0.9.29] - 2026-10-06

### Fixed
- "Keep users from moving it" works on a folder that has only W| entries. Before, the folder still counted as not kept,
  its W| showed as W|*, and a second click found nothing to apply.

## [0.9.28] - 2026-10-06

### Changed
- Preview: the reason field and Apply stand at the top, under the cards; the effect on users can be long. A second
  Apply at the end uses the same reason.

## [0.9.27] - 2026-10-06

### Added
- Findings page: "Convert all to W|…" turns every W- into W| in one go, with a preview and undo.

## [0.9.26] - 2026-10-06

### Added
- W- as a cell value: read and write without delete, passed down (key N). It is a low finding: nothing can be deleted,
  renamed or moved below, and saving in Word or Excel can fail.
- Folders users cannot move (📌), set per folder in the folder panel: every W there is written as write without delete
  on the folder and Modify below. The panel explains that only this one folder is fixed.

### Removed
- The setting "W means" (config `write`). An existing `write` key is ignored; write-without-delete entries show as W-.

## [0.9.25] - 2026-10-06

### Changed
- On the file server, a local folder such as `E:\Shares\Data` opens through the administrative share
  (`\\server\E$\Shares\Data`) instead of failing with a hint about running elevated.
- README: testing on a folders-only copy of a production share (robocopy), screenshots of the current version.

## [0.9.24] - 2026-10-06

### Added
- Findings page: what was checked (folders, moved folders), so that an empty list means something.
- Moved folders carry ↯ in the matrix.

## [0.9.23] - 2026-10-06

### Added
- Moved folders: a folder moved within the volume keeps the rights of its old place. owlseye finds it (finding), shows
  what really applies there, and "Re-apply inheritance" in the folder panel gives it the rights of its new place. Any
  write above a moved folder does the same, and the preview shows it.

## [0.9.22] - 2026-10-06

### Added
- Owner Rights in the folder panel: "no personal rights" or Modify, so that users cannot change the permissions of
  what they own.

### Changed
- The Creator Owner finding names the real risk: personal rights outside the groups.

## [0.9.21] - 2026-10-06

### Added
- The folder panel lists the hidden accounts on the folder.
- Creator Owner in the folder panel: nothing, Modify or full control; full control is a finding.

## [0.9.20] - 2026-10-06

### Added
- "Hidden accounts" above the matrix shows SYSTEM, Administrators, Domain Admins and the accounts hidden in the
  settings as columns that can be set. Findings and the desired state leave them out either way.

## [0.9.19] - 2026-10-05

### Changed
- Saving the settings reads the share again only for a new scan depth.
- The accounts of `full_control` get full control on the root too (inherited from the drive counts); an inheritable F
  stopped by broken inheritance shows as blocked; new folder names are limited to 200 characters.

## [0.9.18] - 2026-10-05

### Changed
- The log shows where shared settings were saved; the settings page shows the version.

## [0.9.17] - 2026-10-05

### Changed
- Scan depth, W, hidden and full-control accounts are shared settings in the state folder (`owlseye-settings.json`),
  the same for every admin who uses it.

## [0.9.16] - 2026-10-05

### Added
- Settings page: scan depth, hidden accounts, the accounts that must have full control, the state folder (with
  pickers). The preview names the full-control entries owlseye adds.

## [0.9.15] - 2026-10-05

### Added
- Groups page with a membership matrix.

### Changed
- The report leaves out users without access and accounts without a right.

## [0.9.14] - 2026-10-05

### Fixed
- The desired-state page is fast on large shares (tested with 45,000 folders) and shows no false differences after
  the rights changes of 0.9.12.

## [0.9.13] - 2026-10-05

### Added
- Full control (F) can be set in the matrix.

## [0.9.12] - 2026-10-05

### Changed
- W means Modify; full control shows as F; administrator groups are hidden; the preview warns when a change removes
  rights the matrix does not show.

## [0.9.11] - 2026-10-05

### Changed
- Build checks run for pull requests and by hand only.

## [0.9.10] - 2026-10-05

### Added
- A missing WebView2 Runtime is explained with clickable download links.

## [0.9.9] - 2026-10-05

First public version.
