# owlseye: requirements specification

This document lists the behaviour owlseye must keep: what it reads and shows, what it writes and how, how it protects
the file share, and the files it keeps. Every requirement has an ID. Code, tests and the
[user stories](user-stories.md) cite these IDs, so an ID is never renumbered or reused; a requirement that no longer
applies stays in its table, marked "(dropped)" with the reason. The [README](../README.md) describes owlseye for the
people who use it; [ADR 0001](adr/0001-native-windows-app-in-csharp.md) records why it is a native Windows app.

Quoted texts are what owlseye shows; tests check many of them. The UI is English.

Requirement IDs: `F` functional, `S` safety, `D` data/compatibility, `N` non-functional.

---

## 1. Glossary

| Term | Meaning |
| --- | --- |
| Share root | Folder the matrix starts at: a UNC path, a drive letter mapped to a UNC path, or (local mode) a local folder. Paths inside the share are relative and use `\`; `""` is the root. |
| Level | `""` = 0, `A` = 1, `A\B` = 2, … |
| Cell | Explicit Allow entry of one account on one folder: `R`, `W`, `F`, `R\|`, `W\|` or none (F-R1). |
| Standard entry | Exactly one ACE with the mask and flags of the cell value (F-R1). Anything else is a special entry and shows with `*` (non-standard). |
| Protected | Inheritance broken on a folder (`SE_DACL_PROTECTED`). |
| Hidden account | An account that is never a column and whose entries owlseye leaves alone (F-R12). |
| Full-control accounts | The accounts that must have full control on the root and on folders with broken inheritance (setting `full_control`, F-R11). |
| Pending | Changes collected in the UI but not yet written: cells, inheritance, new folders, clears, extra columns. |
| Extra column | A group added with "＋ Group" that has no entry in the share yet (F-G3). |
| Automatic R\| | `R\|` that the planner adds on parent folders so an account can reach a folder below, or removes again (F-P7, F-P8). |
| Deviation | A folder below the matrix depth that has something of its own (F-R9). |
| State folder | Folder for the desired state, the log and the shared settings (F-S13). |
| Desired state (baseline) | Cells and protected folders as owlseye last set or accepted them, per share (F-D1). |
| Drift | Differences between the desired state and the actual ACLs (F-D2). |
| Finding | Something that does not match the model (F-F1). |

## 2. Modes, start, configuration

| ID | Requirement |
| --- | --- |
| F-S1 | Four modes: **demo** (built-in demo share in memory; writes change memory only, the desired state stays in memory), **sim** (emulated AD and a real folder tree, D-6), **local** (this machine's local users and groups and the real ACLs of a local folder or UNC path), **windows** (AD and a file share). sim, local and windows run the same provider logic (section 15). |
| F-S2 | Command line: `--demo`, `--sim [DIR]`, `--local [PATH]`, `--config FILE`, `seed-local PATH [--force]` (F-X3), `--help` (also `-h`, `/?`). With several mode flags, demo wins over sim and sim over local, whatever the order. A mode flag overrides `provider` from config.json. Without a mode flag: `provider` from config.json (F-S12); without one, **windows** on a machine that is a member of an AD domain and **local** elsewhere. The demo starts only when asked for (`--demo`, `"provider": "demo"`, or the link on the start page, F-S8); a double-click on the exe never opens it. The mode is not remembered between starts. An unknown argument shows the usage text and exits with code 2. |
| F-S3 | local/windows: share = path from the command line > share opened last with this provider (F-S6) > `share` from config.json. Without any share the window opens anyway and asks for one: the start page says "No share yet: enter a local folder (or a UNC path) below, or start once with --local PATH." (local) or "No share yet: enter a share (UNC path or mapped drive) below, or set share in config.json." (windows) and offers to open a folder (F-S8). A share that cannot even be opened at start (missing drive, not a folder) shows the error and offers another folder. A share that can be opened is remembered right away (F-S6). |
| F-S4 | sim: if `<sim dir>\state.json` is missing, it is created from the demo data (F-X2) and the start shows "Sim created: <dir>" once. Sim dir: `--sim DIR`, else `sim_dir`, else `<data dir>\sim`. |
| F-S5 | `config.json` keys: `provider` (F-S2), `share` (""), `max_level` (3, default matrix depth), `scan_depth` (20; 0 = whole tree), `state` ("" = data dir, F-S13), `audit` ("" = `<state folder>\audit-<provider>.jsonl`), `baseline` (folder for the desired state; "" = state folder; demo: memory only), `sim_dir`, `write` (`"modify"` or `"no-delete"`, F-R1), `hidden` ([], F-R12), `full_control` (`["SYSTEM", "Administrators"]`; an empty list means the default, F-R11). Unknown keys are ignored. The shared settings of the state folder override `scan_depth`, `write`, `hidden` and `full_control` (F-S13). |
| F-S6 | Per-admin settings in `<data dir>\settings.json`: `depth` (matrix depth), `share_<provider>`, `recent_<provider>` (at most 8, newest first, deduplicated case-insensitively). Missing or broken file = defaults. Written atomically (D-1). |
| F-S7 | Data dir: `%LOCALAPPDATA%\owlseye` (fallback `~/.local/share/owlseye`). It holds `settings.json`, the admin's own `config.json` (F-S12), the WebView2 profile (S-14), `error.log` (F-B8), and by default the state folder and the sim dir. |
| F-S8 | The window opens at once. Until the first scan is done, every page shows the progress (F-B1). If the scan fails: "Cannot read the share: <error>", a "Try again" button, "Restart as administrator" (local mode, not elevated, N-5), and (local/windows) "Or open another folder" with a text field, "Browse…" and "Open", plus a link that restarts owlseye with the demo data. |
| F-S9 | Share page (click on the share in the header): current share, provider, scan time; text field (local folder or UNC, surrounding quotes stripped; empty: "Enter a folder."), native folder picker, list of recent shares (without the current one). In demo/sim: "In <mode> mode the share is fixed." A warning if there are pending changes, since they are dropped. |
| F-S10 | Switching shares scans first, with the current scan depth. On failure nothing changes ("Cannot open <path>: <error>"). On success, pending changes are dropped, the desired state follows the new share (one file per share, D-2), the share is remembered, and the matrix shows "Opened <share>.". |
| F-S11 | The actor (`DOMAIN\user` of the signed-in admin) is shown in the header and written as `actor` into the log and as `by` into the desired state. |
| F-S12 | The config.json read at start: the one given with `--config`, else `%LOCALAPPDATA%\owlseye\config.json` (the admin's own one, written by the settings page, F-S15), else `config.json` next to `owlseye.exe`, else none. |
| F-S13 | State folder (`state`, default the data dir): holds the desired-state files, the log and the shared settings `owlseye-settings.json` (D-8) with `scan_depth`, `write`, `hidden` and `full_control`. At start these win over the same keys in the local config.json, so every admin who uses the state folder works by the same rules; if the folder or the file cannot be read, the local values apply. `audit` and `baseline` can still name other places. |
| F-S14 | Settings page: the owlseye version; where the shared settings are (and whether they are written yet), which config.json was read and where saving goes, and the start order (F-S12). Fields: scan depth (0–100), what W means (Modify, or read and write without delete), the full-control accounts and further hidden accounts (one per line; a picker offers the keywords SYSTEM, Administrators and Domain Admins, the accounts in this share's ACLs including hidden ones, and from two typed characters on groups from the directory, at most 25 suggestions), the full-control accounts as found in this share, the state folder (text field and folder picker), and a reason for the log. Provider and share are not on the page. |
| F-S15 | Saving settings: refused while changes are pending ("Apply or discard the pending changes first: saving works the rights out anew.") and when a config.json given with `--config` cannot be written; the scan depth must be 0–100 and W "modify" or "no-delete". Lists are trimmed and deduplicated case-insensitively; an empty full-control list means the default. The shared settings go into the state folder (D-8). Where the state folder is (`state`, `audit`, `baseline`) goes into the config.json in use if it can be written, otherwise into the admin's own one (first copied from the one in use); the shared keys are removed from the local file (D-5). The settings apply at once, and the log gets a `settings` entry with the old and the new value of every changed key (D-4). If nothing changed: "Nothing changed.". |
| F-S16 | Changing the state folder: `audit` and `baseline` follow it. If the new folder holds no owlseye state yet, the desired-state files, the logs of all providers and the shared settings are moved there (all copied first, the originals deleted only afterwards; on failure nothing changes: "State folder not changed: <error>"). If it already holds state (a `desired-*.json`, an `audit*.jsonl` or an `owlseye-settings.json`), nothing is moved and that state is used from now on; if it has shared settings, they replace the ones entered ("Now using the state folder <dir> and its settings (those of the admins who use it)."). |
| F-S17 | After saving, only a new scan depth (local/windows: the share is scanned again with it; demo/sim: "The scan depth applies at the next start.") or changed hidden accounts read the share again. W, the full-control accounts and the state folder are worked out again from the last scan. |
| F-S18 | Without the Microsoft Edge WebView2 Runtime, owlseye shows a window that says what is missing and how to install it, with the online installer, the offline installer (x64) and Microsoft's download page as clickable links (opened in the browser, full address visible) and a "Copy links" button, then exits (code 3). |

## 3. Scan

| ID | Requirement |
| --- | --- |
| F-SC1 | Walk the tree **iteratively** (no recursion limit) from the root, children in case-insensitive name order, down to `scan_depth` (0 = unlimited). The result is in tree order. |
| F-SC2 | Per folder: read the DACL (protected flag and all ACEs, explicit and inherited) through a handle on the folder itself, opened without following a reparse point, together with the folder's identity (volume serial number and file id); where the admin may read the ACL but cannot open a handle, by path and without identity. Allow/Deny ACEs become `Ace(sid, name, kind, mask, allow, inherited, flags)`; any other ACE type (object, callback, …) sets `other_aces` (the folder becomes read-only for owlseye, S-4). |
| F-SC3 | NULL DACL is shown as one ACE: Everyone, `0x1F01FF`, flags `0x3`. |
| F-SC4 | One unreadable folder never aborts the scan. ACL unreadable → `error="ACL not readable: …"`, `other_aces=true`, no descent (also for a folder that was replaced by a junction or link after its parent was listed: owlseye does not read through it). Contents unreadable → `error="Contents not readable: …"`, no descent. |
| F-SC5 | Accounts: SID → (DOMAIN\name, kind) via LookupAccountSid, cached per scan (cache cleared at each scan). `SID_NAME_USE` 1 user, 2/4 group, 5 wellknown, 9 computer, else group; not resolvable → kind `unknown`, name = SID. The SIDs SYSTEM, Administrators, Creator Owner, Owner Rights, Everyone, Authenticated Users, BUILTIN\Users are always kind `wellknown`. |
| F-SC6 | Principals (the accounts owlseye knows) = all SIDs from explicit ACEs of all folders plus inherited ACEs of the root, except hidden accounts (F-R12). Domain SIDs (`S-1-5-21-*`) get their DN from the directory via `(objectSid=…)`, OR-batched 50 per query. |
| F-SC7 | Members: starting from the DNs of the principals, breadth-first: groups (`objectClass` contains `group`) with `member`, recursing into members not seen yet; users (`user` and not `computer`) with `sAMAccountName`, `displayName` (fallback sam), enabled = `userAccountControl & 2 == 0`, SID. Batches of 50 `(distinguishedName=…)`. |
| F-SC8 | Progress phases during the scan: "Reading folders" (path, number of folders read), "Looking up the accounts in the directory", "Reading group members", "Evaluating the rights". |
| F-SC9 | After the scan: compute cells, findings, desired state, drift (F-R, F-F, F-D). Extra columns that now appear in ACLs are dropped from `extra`. Every scan forgets cached SID lookups and directory data. |
| F-SC10 | Rescan button in the header (tooltip "Scanned <time>"), message "Rescanned."; the matrix then shows the placeholder panel and no column filter. |
| F-SC11 | windows and local read several folders at a time (8 and 4), level by level; the result is the same as that of the sequential walk, in the same order. sim reads one folder at a time. |
| F-SC12 | A user's primary group (typically Domain Users) counts: users whose `primaryGroupID` is the RID of a domain group read in the scan become its members (queried in batches of 50 RIDs, computer accounts skipped), so such a group does not look empty. |
| F-SC13 | A group with more members than the directory returns at once (`member;range=…`, more than 1,500 members) is read range by range until all members are known. |
| F-SC14 | Paths longer than 260 characters are read and written: all file system calls use `\\?\` paths, and the exe is long-path aware. |

## 4. Rights model (computation)

| ID | Requirement |
| --- | --- |
| F-R1 | Masks: `READ = 0x1200A9` (read, execute); the W mask is Modify `0x1301BF` (read, execute, write and delete; default) or, with the setting `write` = `"no-delete"`, `0x1201BF` (read, execute and write, no delete); `FULL = 0x1F01FF`. Another `write` value is refused at start ("config.json: "write" must be "modify" or "no-delete", not "<value>""). Standard entries: `R\|` = (READ, flags 0), `R` = (READ, OI\|CI = 0x3), `W\|` = (W mask, 0), `W` = (W mask, 0x3), `F` = (FULL, 0x3). Rank: none 0, R\| 0.5, W\| 0.75, R 1, W 2, F 3. |
| F-R2 | Classification of an account's explicit Allow ACEs on a folder: `F` if the masks of the entries that apply to this folder, its subfolders and files (OI and CI set, not inherit-only) add up to FULL. Otherwise: if any ACE has OI or CI, only those count (inheritable), else all; "write" = any of these masks has a bit of `0x2\|0x4\|0x40\|0x10000\|0x40000000\|0x10000000`; value = inheritable ? (W or R) : (W\| or R\|). Standard = exactly one ACE and (mask, flags without INHERITED) equals the standard entry of the value. |
| F-R3 | Explicit cells: only Allow ACEs, without hidden accounts (F-R12). |
| F-R4 | Inheritance, reproduced by owlseye: R, W and F pass down into subfolders until a folder is protected; R\|/W\| do not pass down. The root (if not protected) receives what it inherits from above the share (inherited, inheritable Allow ACEs of the root, classified per account, source "(above the share)"). Effective value = the stronger of own and incoming; source = the folder it comes from. |
| F-R5 | Users: transitive group membership (nested, cycle-safe: with A in B and B in A, members of either are in both). A user's SIDs = own SID + Everyone/Authenticated Users/BUILTIN\Users + SIDs of all transitive groups. Effective user right per folder = strongest effective cell over these SIDs. |
| F-R6 | "via": the accounts through which a user reaches a folder, each as `<name> (<right>, here \| from <source or root>)`, sorted by name. |
| F-R7 | Members of a column: Everyone etc. → all users; a user column → that user if it is among the users read (a computer or service account is not); a group with a known DN → transitive user members; otherwise "unknown" (shown as `?`). |
| F-R8 | Blocked: on a protected folder (level ≥ 1), every account that has an inheritable R or W effective on the parent and nothing here. |
| F-R9 | Deviation: protected, read error, other ACE types, or an explicit ACE of an account that is not hidden. |
| F-R10 | "Covered": an account gets into a folder if it has a cell there (own or inherited), or every member already has access there through another account (typically all users with R\| on the root). |
| F-R11 | Full-control accounts, from the setting `full_control` (default SYSTEM and Administrators): SIDs as given; SYSTEM, Administrators and Domain Admins (RID 512 of the domain the share's accounts belong to) by these keywords, whatever their name in the domain's language; other names (`DOMAIN\name` or `name`, case-insensitive) looked up among the accounts read and in the ACLs. Names that cannot be found are left out. |
| F-R12 | Hidden accounts: always SYSTEM (`S-1-5-18`), Administrators (`S-1-5-32-544`), Creator Owner (`S-1-3-0`), Owner Rights (`S-1-3-4`), Domain Admins (`S-1-5-21-…-512`) and Enterprise Admins (`S-1-5-21-…-519`) of any domain, plus the accounts in the setting `hidden` (by SID, `DOMAIN\name` or `name`, case-insensitive), e.g. a backup group `CORP\backup` with full control everywhere. A hidden account is no column, its members are not read, its entries give no cells and no per-entry findings, drift ignores it (F-D2), and the planner never sets it (F-P2). When owlseye writes a folder, the entries of hidden accounts stay, except where the folder is cleared (F-P11) or inheritance is broken or restored (F-P3, F-P4). |

## 5. Findings

| ID | Requirement |
| --- | --- |
| F-F1 | Rules, per folder (texts verbatim): **high** "owlseye cannot read this folder as administrator. <error>"; **medium** suspicious name (S-6) "Name has invisible, combining or mixed-script characters (lookalike?)"; **medium** "Inheritance broken below level <depth>"; **medium** on protected folders and on the root "<accounts> without full control here" for the full-control accounts (F-R11) without an explicit Allow entry with the FULL mask and OI\|CI (short names joined with " and "); per explicit ACE of an account that is not hidden, first matching: Deny → **medium** "Deny entry for <name>"; unknown → **high** "Unresolved SID <sid>"; Everyone/Authenticated Users/BUILTIN\Users with OI or CI below the root → **high** "Broad permission for <name>"; user → **high** "Direct user entry for <name>"; below depth → **medium** "Explicit entry below level <depth>: <name>"; inherit-only without OI/CI → **low** "Entry for <name> applies to nothing". Then, for cells within the depth: **low** "Non-standard entry for <name> (shown as <value>)" for non-standard cells, otherwise **low** "Full control for <name>: its members can change permissions and take ownership" for `F`; **medium** "<name> cannot open this folder to reach <first folder below> (R\| missing)" for unreachable cells (F-F2). |
| F-F2 | Unreachable: for each direct cell within the depth (not for unknown accounts), every ancestor where the account is not covered (F-R10). |
| F-F3 | Sorted by severity (high, medium, low), then path (case-insensitive), then text. Findings depend on the matrix depth and are recomputed when it changes. |
| F-F4 | Findings page: table severity / folder (link to the folder page) / text; "No findings." when empty. Badge with the count in the navigation. |

## 6. Matrix

| ID | Requirement |
| --- | --- |
| F-M1 | Rows: the root, all folders up to the matrix depth, below it only deviations (F-R9, plus folders with a pending clear) and the folders on the way to them. Tree order (case-insensitive by path component). Planned new folders are included. |
| F-M2 | Depth selector 1–10, saved per admin (`settings.depth`). Changing it drops pending new folders deeper than the new depth (with everything pending on them), recomputes findings, and resets the panel and the column filter. |
| F-M3 | Columns: accounts with a cell anywhere in the share (also deeper than the matrix), extra columns, accounts with pending changes. Sorted by short name (part after `\`), case-insensitive. Header: short name, rotated; tooltip "<name> · <kind> · <n> users" (`?` if unknown); styles for extra, user, unknown columns. |
| F-M4 | Column filter: substring of the name, case-insensitive, updates while typing (250 ms debounce); `/matrix?q=…` presets it. |
| F-M5 | Cell display (after applying pending changes to a copy of the snapshot): **pending** (value or `–`, marked), **auto** (automatic R\|, marked), **direct** (value; `F` darker; non-standard: `*` suffix and marked), **inherited** (value, muted), **blocked** (`⊘`), **none** (empty). Tooltips (after "<account> · <folder>: "): "Pending: <before> → <after>", "Added automatically: …", "Removed automatically: …", "Entry here: <label>[ (non-standard entry)]", "Inherited from <source>: <label>", "Blocked: <label> on <parent> does not reach here (inheritance broken)", "No access". |
| F-M6 | Folder row header: indentation by level; collapse/expand for level-1 folders with children (UI state only, not saved, survives updates; the rows of a collapsed folder are left out); inheritance icon `⛔` (protected, also pending) or `↳` (tooltip "Inheritance broken" / "Inherits from parent", with "(pending)"); folder name (`＋ ` prefix for new folders, tooltip = full path); icon and name open the folder panel; `⚠` link to the folder page if the folder has findings. Rows below the depth are marked and have a tooltip explaining why they are shown (deviation / way to one). Pending inheritance or clear is marked. |
| F-M7 | Legend with all cell styles (W labelled "Modify" or "Write (no delete)" by the setting `write`, R, F, R\|, inherited, blocked, pending, automatic, ⛔) and the keyboard help. |
| F-M8 | Pending bar (when anything is pending): "<n> pending change(s)", list of new folders, plan error if the plan fails, **Preview** and **Discard**. |
| F-M9 | Banner if there is drift: "<n> change(s) to ACL entries or folder inheritance made outside owlseye since the last desired state." with the link "Review: restore or keep" to the desired-state page. |
| F-M10 | Empty share: "No group has an entry in this share yet. Add one with ＋ Group." |

## 7. Editing

| ID | Requirement |
| --- | --- |
| F-E1 | Clicking a cell opens the cell panel (F-E5). Setting a value: explicit (`R`, `W`, `F`, `R\|`, `W\|`, none) or cycle when no value is given: from (pending value, else current explicit value) none→R, R\|→R, R→W, W\|→W, W→none, F→none. |
| F-E2 | If the new value equals the current explicit value and that entry is standard, the pending change is removed; otherwise it is set. If the resulting pending set cannot be planned (PlanError), the change is reverted and the reason shown in the panel. |
| F-E3 | Keyboard on a focused cell (ignored with Ctrl/Meta/Alt and while busy): arrows move (clamped to the grid, skipping collapsed rows); `r` → R; `w`/`m` → W; `f` → F; `l` or `\|` → R\|; `Shift+W` → W\|; `Delete`/`Backspace`/`0`/`-` → none; `Enter`/`Space` open the panel. Focus stays on the same cell (sid + path) after the update, also after a click on a button in the panel. |
| F-E4 | Every change updates the matrix **and** the open panel together. |
| F-E5 | Cell panel: account short name, full name, kind, folder; state text by kind (direct; for a special entry what it is in Windows terms, e.g. "Modify + change permissions", and that choosing a right replaces it with the standard entry / inherited with source and own entry / blocked / pending / auto / no access); buttons for none, R\|, R, W\|, W, F (current one highlighted) unless the folder has `other_aces` (then "This folder's ACL cannot be read completely or has entries of other types; owlseye does not rewrite it."); a note below the depth; for blocked cells "Restore inheritance on <folder>"; for inherited cells an explanation and "Break inheritance on <folder>" where possible; members (first 30, "… and <n> more"; "Unresolved account." / "Members are not known to owlseye (built-in or not found in the directory)." / "No members."); link "Who has access to <folder>?". |
| F-I1 | Inheritance can be toggled on every existing folder except the root (also below the depth, for cleanup), not on a pending new folder. Toggling back to the current state removes the pending change. The planner refuses folders with other ACE types, and breaking inheritance where an unresolved SID is inherited (F-P2). |
| F-I2 | Clear (reset to default): allowed on existing folders below the root without other ACE types that are protected or have explicit entries ("Only folders below the root with entries of their own can be cleared"). Toggling again takes it back. |
| F-I3 | Folder panel: name, path, level; state (new folder pending with "Cancel this folder" / clear pending / protected or inherits, pending note); buttons Break/Restore inheritance and Clear/Undo clear with explanations; "New subfolder" form (only if level < depth and the folder is not new itself), list of pending new subfolders with cancel; "Groups with access" (name, effective right, here / from <source>), sorted by right descending then name; findings of this folder; link "Who has access to <folder>?". |
| F-N1 | New folder: name trimmed, at most 200 characters (input field), parent must exist or be pending, level ≤ depth, valid name (S-5), not existing, not already pending ("<path> is already pending"). Creation order: parents first. A new folder inherits; rights can be set on it before applying. |
| F-N2 | Cancelling a new folder also cancels pending new folders below it and everything pending on them (cells, inheritance). |
| F-G1 | "＋ Group" panel: search 300 ms after typing; prefix search on `sAMAccountName` of groups in the directory (max 50, sorted, without hidden accounts) plus built-in groups by localized name or alias (Users/Benutzer `S-1-5-32-545`, Power Users/Hauptbenutzer `-547`, Remote Desktop Users/Remotedesktopbenutzer `-555`, Authenticated Users/Authentifizierte Benutzer `S-1-5-11`, Everyone/Jeder `S-1-1-0`). Already shown accounts are marked "column", others get "Add"; no hit: "No group found.". |
| F-G2 | A search text that is the beginning of administrators, administratoren, system, creator owner or ersteller-besitzer shows why these accounts are no columns: owlseye gives SYSTEM and Administrators full control itself, and Creator Owner stays as it is. |
| F-G3 | An added column stays (as extra) until it appears in an ACL or pending changes are discarded or applied. |
| F-E6 | Discard drops all pending changes and extra columns, and resets the panel and the column filter. |

## 8. Planner

| ID | Requirement |
| --- | --- |
| F-P1 | Input: cell changes, inheritance changes, new folders, clears, extra columns, depth (limits only new folders). Output: ACL operations per folder (path, protected before/after, explicit ACEs before/after, cell changes with auto flag, new_folder, cleared), create operations, the automatic R\| changes, user impact, snapshot after. |
| F-P2 | Validation (PlanError, shown in the panel, the pending bar or the preview): invalid new folders (F-N1); clear only on existing folders below the root without other ACE types; inheritance only on existing folders below the root (or folders created in the same plan), not with other ACE types, not when breaking a folder that inherits an unresolved SID; no hidden accounts ("SYSTEM, Administrators, Creator Owner, Domain Admins and the accounts hidden in config.json are not set in the matrix"); unknown account; unknown folder; value not one of the cell values; no rights for an unresolved account ("<name> cannot be resolved (deleted or foreign account?); owlseye does not give it rights"); folder with other ACE types; a folder path with a bad component (S-5) is refused in the preview already. |
| F-P3 | Breaking inheritance: explicit = previous explicit + all inherited ACEs converted to explicit (INHERITED flag removed), deduplicated by (sid, allow, mask, flags without INHERITED). New folders: only the flag changes. |
| F-P4 | Restoring inheritance: remove explicit ACEs whose (sid, allow, mask) the parent passes down (parent ACE with CI). "This folder only" entries stay. |
| F-P5 | Setting a cell replaces all Allow ACEs of that account on the folder with the one standard entry (or removes them). Deny entries and other accounts stay. |
| F-P6 | A change to the same value is a no-op, except on a non-standard entry, which gets normalized to the standard entry. |
| F-P7 | Automatic R\| add: for every account that gets a right on a folder, add `R\|` on each ancestor where it is not covered (F-R10) and has no change of its own. |
| F-P8 | Automatic R\| remove: when an account's entry is removed, walk up the ancestors (deepest changes first): remove an ancestor's `R\|` only if it is a standard `R\|`, has no change of its own, and no other right of that account remains below; stop at the first ancestor that does not qualify. An `R\|` set by hand on a folder without rights below stays. |
| F-P9 | On every written folder with broken inheritance, ensure the full-control accounts (F-R11) have FULL with OI\|CI; accounts that already have it are left as they are. |
| F-P10 | ACE order: Deny before Allow, otherwise stable. |
| F-P11 | Clear: inheritance on, all cells of the folder removed (so automatic R\| above goes too), and then **all** remaining explicit ACEs dropped (Deny, special entries, entries of hidden accounts). A clear overrides cells and inheritance changes on the same folder. |
| F-P12 | Operations sorted by (level, path case-insensitive). Folders whose protected flag and ACE set do not change are skipped (new folders without own entries are only created). |
| F-P13 | Impact: per user and folder, effective right before → after where it differs; sorted by path, user. "Gained" = rank after > rank before. |
| F-P14 | Hidden losses: when a change replaces an account's entry by a standard entry that still grants something (typically a special entry such as full control made of several entries, or Modify while W means "no delete"), and that removes delete, delete subfolders and files, change permissions or take ownership beyond what the visible step (e.g. W → R) explains, the plan lists it: folder, account, the entries before and after in Windows terms, and what is lost. GENERIC_ALL and GENERIC_WRITE count as the rights they stand for. Not for new folders or removed entries. |
| F-P15 | The full-control entries F-P9 adds (accounts of F-R11 with full control after, not before, and not set as a cell in the same change) are listed per folder. |

## 9. Preview, apply, undo

| ID | Requirement |
| --- | --- |
| F-A1 | Preview page: cards (folders, entries changed, new folders, rights gained, rights lost, and "entries lose hidden rights" when F-P14 finds any); new folders ("They inherit from their parent folder. Creating a folder cannot be undone automatically."); the hidden losses (F-P14) as a warning table ("These changes remove rights the matrix does not show."); ACL changes per folder (clear with the number of deny entries removed, break/restore lines, cell changes before → after, "automatic R\|" tag, "full control required" rows for F-P15); a note when a change sets F (its members may change permissions and take ownership); explanatory notes naming the full-control accounts; user impact table; reason/ticket field; Apply; Back. At most 500 folders and 200 impact rows are listed, with the totals; Apply writes everything. A plan error or an empty plan ("No changes.") shows its message. |
| F-A2 | Apply applies exactly the plan that was shown: the preview carries a hash of the plan (SHA-256 over its JSON form); if the plan has changed since, go back to the preview with "The plan has changed, please review it again." |
| F-A3 | Conflict check against the live file system before writing: a planned new folder already exists → conflict ("<path> already exists."); for each ACL op on an existing folder, re-read the ACL of the folder (and of its parent when the protected flag changes) and compare the protected flag and the ACE set (sorted by sid, allow, mask, flags without INHERITED) with the snapshot ("The ACL of <folder or the share root> was changed outside owlseye."). Conflict → rescan, back to the preview with "<reason> Rescanned, please review again." |
| F-A4 | Writing: log `change_start` (actor, provider, share, reason, total, planned_create, planned_acl) **before** the first write. Create folders first, then ACLs in plan order. After each step log `change_step` (`create` or `acl_op`). The written ACL operations are carried into the desired state (F-D5) in batches: after at most 25 written folders or 2 seconds, at the end, and on an error. If saving the desired state fails once, record the error ("desired state not saved: …") and stop trying for further operations. Stop at the first failed write ("create <path>: …" / "ACL of <folder>: …"). Finally log `change` (status ok/error, error, acl_ops done, create_ops done, impact), clear pending, rescan. |
| F-A5 | Messages: "<n> changes applied (log <id>). Users get them at their next access." / "Error after <n> of <total> operations: <error>". |
| F-A6 | Writing a DACL sets the explicit ACEs and the protected flag; Windows computes inherited ACEs and propagates into the subtree. |
| F-U1 | Undo from a log entry (only entries whose ACL operations are in the current format, including unfinished runs): refuse if it belongs to another share ("This log entry belongs to another share"), or if it cannot be parsed, also when an account in it is not a SID ("Log entry cannot be read (older format?): …"). Otherwise drop pending changes; for each operation on a folder that still exists, set each changed cell back to its `before` value where the current value differs, add extra columns for accounts no longer in the scan (under the name the system resolves now, S-16), set inheritance back where it differs; then open the preview. Folder creation is not undone. |

## 10. Desired state and drift

| ID | Requirement |
| --- | --- |
| F-D1 | Desired state = cells (sid, path, value), names (sid → name, for display), protected folders (level ≥ 1). On a scan without a desired-state file, the current state becomes the desired state and `baseline_init` is logged, under the file lock (a second instance must not overwrite the first one's). |
| F-D2 | Drift on all scanned levels: `folder_gone` (a folder in the desired state no longer exists; its cell differences are not listed separately), entry `added`/`removed`/`changed`, inheritance `broken`/`restored`. Desired cells of hidden accounts are no drift. A folder renamed only in case is the same folder. Sorted by path, name, change. Key = `change\|sid\|path`. |
| F-D3 | Desired-state page: file location (or "in memory only (demo)"), state (not saved yet / from the earlier group model / last written <time> by <who> / cannot be read), counts, log path; "Delete desired state…" with reason and confirmation; drift table with checkboxes (new items checked, select-all); "Restore desired state…"; reason field + "Keep current state"; user impact desired → current. At most 500 differences and 200 impact rows are shown; the buttons act on everything selected. A broken file shows the error, and comparison stays off until it is fixed or deleted; the same when the desired-state folder cannot be reached or the file stays locked (the scan itself stays valid). Badge in the navigation: count or `!` on error. |
| F-D4 | Keep (accept): the selected items become the desired state; log `drift_accept` with the reason, at most 500 of the items and their total `accepted_count`; rescan. Message "<n> outside change(s) kept as the new desired state (log <id>).". |
| F-D5 | Own applies carry the written operations into the desired state (in batches, F-A4), so they are not reported as drift, also for other admins sharing the state folder; unrelated drift stays. |
| F-D6 | Restore (revert): turn the selected items into pending changes and open the preview. Missing folders are recreated (with missing parents) together with their desired cells (except those of hidden accounts) and protected flag; accounts no longer present get extra columns (under the name the system resolves now, S-16). |
| F-D7 | Delete desired state: delete the file (also a broken one), log `baseline_reset` (reason, path), rescan; the current state becomes the desired state. |
| F-D8 | Drift impact: what the outside changes did for users (desired → actual), missing folders excluded. |
| F-D9 | A desired state written before format 2 (D-2) saved full control as `W`: a desired `W` where the entry now is `F` is no drift, and the next save of that desired state writes `F` there and format 2. |

## 11. Other pages

| ID | Requirement |
| --- | --- |
| F-V1 | Users page: search (sam + display name), list sorted by display name; the selected user shows every folder in the matrix with access: right and "via" (F-R6); "No access to managed folders." when there is none. |
| F-V2 | Folder page: full path, level, protected, number of users with access; findings; "Who has access?" (user with link to the users page, right; sorted by right descending then name); explicit ACL table (account, kind, Deny or label, mask `0x%06X`, flags `0x%02X`). A folder not in the scan: "This folder is not in the scan (any more)." |
| F-L1 | Log page: log path; newest first, at most 200 entries; columns time (UTC), who, reason (+ error), changes, "Undo…" button (F-U1). Rendering per kind: `baseline_init`, `baseline_reset`, `settings` (each changed key with before → after, where the shared settings and the state-folder pointer were saved, whether the state folder's settings were taken over), unfinished `change_start` ("Not finished: <n> of <total> operations written." + not-written paths + hint), accepted drift items (the first 20, then "… and <n> more kept"), created folders, ACL ops (cleared / break / restore / cell changes with "(auto)"), earlier-model entries (`ops`, ACL ops without `protected_before`) as muted lines. "No changes yet." when empty. |
| F-L2 | Folding: an apply logs start, steps, end; the end is the entry. A start without an end stays as the entry, with its steps as its operations and the planned but unwritten paths as `missing`. Broken or foreign lines are skipped. |
| F-V3 | Groups page: the groups with rights on the share (the matrix columns that are groups or well-known groups); "All groups" adds every group read from the directory (the groups nested in them); hidden accounts are left out; search by name. Each group shows its number of users and of folders with an entry of its own. The selected group shows its number of users ("every user" where that applies), the groups it is a member of and the groups in it (as links), its rights on the folders of the matrix (entry here or inherited), and its members with "direct" or "via <group>" (at most 500 rows each). |
| F-V4 | Membership matrix (on the groups page): users × groups, ● direct member, ○ member through a nested group. Groups every user is in (Everyone, Authenticated Users, BUILTIN\Users, Domain Users, or any group that holds all users) are named above it instead of shown as columns; only users in at least one column are rows; search by user. |
| F-V5 | Access rights report (menu "Report"): built from the scan; pending changes are not part of it, and the page says how many there are. Header with share, creator and time, scan time and mode, folder counts, and cards (folders, accounts with rights, users with access, findings, changes). Sections: 1 rights matrix with the folders of the matrix and only the accounts with an effective right on one of them (own entry as value, `F`, or value with `*` if non-standard; inherited in brackets; ⛔ for broken inheritance; blocks of 28 accounts); 2 groups and members; 3 access per user (only users with access); 4 findings; 5 changes of the last 90 days on this share from the log (applies with their changes, unfinished runs, kept outside changes, desired state deleted or first taken). Hidden accounts are not listed. |
| F-V6 | Report export: "Save as PDF…" prints the page through WebView2 (A4 landscape, backgrounds, title in the header, page numbers, each section on a new page); "Save as Excel…" writes a workbook with the sheets Summary, Matrix, Access (one row per user and folder with the right and the accounts it comes through), Groups, Findings and Changes (90 days); characters XML cannot hold become U+FFFD. Default file name `owlseye-report-<share>-<date>`; after saving, "Open" and "Show in folder". |

## 12. Progress, busy state, closing, window

| ID | Requirement |
| --- | --- |
| F-B1 | Progress status: task (scan / apply / idle), phase text, current path, done, total. Scan: indeterminate bar + "<n> folders read"; apply: bar "<done> of <total> done" + "Keep owlseye open until this is finished." The display repaints at most five times a second. |
| F-B2 | During rescan, apply, share switch, drift accept/reset and saving settings: overlay with the live progress; the page behind it is inert, so nothing can be triggered twice. The UI stays responsive (work runs off the UI thread). |
| F-B3 | Closing the window while ACLs are being written asks first, in a Yes/No message box: "owlseye is writing ACLs right now." with the explanation that the folder being written may be left with its subfolders only partly updated, and "Close anyway?" (default No). Closing ends the process, background threads included. |
| F-B4 | Phases during apply: "Checking the ACLs for outside changes", "Creating folders", "Writing ACLs", then the rescan phases. |
| F-B5 | Flash messages after actions at the top of the page (errors red). A message from an action stays for the page it leads to; one set on the page goes away with the next navigation. |
| F-B6 | Header on every page: brand, navigation (Matrix, Users, Groups, Findings + badge, Desired state + badge, Log, Report, Settings), share (link to the share page), actor, provider tag, the unsafe-folder warning (S-15), "Restart as administrator" (local mode, not elevated, N-5), Rescan. |
| F-B7 | Window title: "<page> · owlseye · <share>" (page: Matrix, Preview, Users, Groups, Findings, Desired state, Log, Report, Share, Settings, or the folder name on a folder page); "owlseye · <share>" until the first scan is done. |
| F-B8 | Unexpected errors are written with their stack to `<data dir>\error.log`. On the UI thread a message box names the error and the log; a page that fails shows "Something went wrong on this page: <error>" with the log path and a link back to the matrix, and gets a fresh start on the next navigation; a failed action shows its error as a flash message. |

## 13. Safety

| ID | Requirement |
| --- | --- |
| S-1 | **Handle chain on every write and mkdir:** open every component from the share root to the target with `FILE_FLAG_OPEN_REPARSE_POINT \| FILE_FLAG_BACKUP_SEMANTICS`, no `FILE_SHARE_DELETE`, keep all handles open until done; refuse if a component is a reparse point or not a directory. Second layer: the final path of the leaf must equal the root's final path + rel (case-insensitive). Write the DACL via `SetSecurityInfo` on the leaf handle (`WRITE_DAC`). |
| S-2 | mkdir: hold the parent chain open, create, then verify the new folder through the chain; if that fails, remove it again and fail. |
| S-3 | Junctions/symlinks: never descended during the scan; recorded per scan. A write on a folder that has a recorded link anywhere below is refused ("<folder> has a junction or link below it (<link>); inheritance would propagate through it. Remove the link first."), as long as it is not verified that propagation over SMB does not follow links (on local NTFS it does not). |
| S-4 | Before every write, re-read the DACL; if it now has other ACE types, refuse ("owlseye does not rewrite it"). Folders with `other_aces` or a read error are never written. |
| S-5 | Bad path components (`""`, `.`, `..`, trailing dot or space, `\ / : * ? " < > \|`, control characters < 32): such folders are flagged ("Name Windows cannot address as written"), never read, never descended, never written; reading/writing such paths is refused ("Invalid folder path: …"). New folder names additionally may not be reserved device names (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM1–9, COM¹²³, LPT1–9, LPT¹²³), also with an extension or trailing spaces before the dot. |
| S-6 | Suspicious names (finding, see F-F1): Unicode category Cf, Co, Cn, or a Zs other than the normal space; not NFC; Latin mixed with Greek or Cyrillic letters. A name that cannot be normalized at all (unpaired surrogate) is suspicious too and does not break the scan. |
| S-7 | Paths deeper than `scan_depth` are refused for reads and writes ("<path> is below the scanned depth (<n>)"). |
| S-8 | Drive letters are converted to UNC (`WNetGetUniversalName`); on failure: "<drive> is not a network drive in this session. Is owlseye running elevated? Then it cannot see normally mapped drives; start it without 'Run as administrator' or use the UNC path." Anything else that is not UNC: "Neither UNC nor a drive letter: <path>". Local mode: a UNC path, or an absolute local path that must exist ("Share root does not exist: <path>"). |
| S-9 | LDAP filter values are escaped (RFC 4515): everything outside `[A-Za-z0-9 =,.-_]` as `\xx` per UTF-8 byte. |
| S-10 | A desired-state file that is broken, has an unexpected format (including an account that is not a SID), or belongs to another share (same sanitized name) is **never overwritten**; the error is shown. |
| S-11 | Only ACLs are written and folders created. owlseye never changes groups or members, never deletes folders (except taking back a folder it just created that did not land where it should, S-2), never touches deny entries or other accounts' entries except through an explicit clear, break or restore. |
| S-12 | The window loads only owlseye's own content, embedded in the exe. Only web links (`http`, `https`) leave it, opened in the default browser; other schemes are blocked; files dropped onto the window are not opened; developer tools, browser accelerator keys and the status bar are off in release builds. |
| S-13 | Writes go only to the folder that was scanned: right before `SetSecurityInfo`, on the same pinned handle, owlseye compares the folder's identity (F-SC2), explicit entries and protection with the scan. A folder swapped in by a rename ("<folder> is no longer the folder that was scanned (renamed or replaced); owlseye did not write it") or an ACL changed in the meantime ("The ACL of <folder> was changed outside owlseye; owlseye did not write it") is not written. |
| S-14 | Elevated, owlseye ignores `WEBVIEW2_*` environment variables (remote debugging, another browser) and uses its own WebView2 profile `<data dir>\WebView2-elevated`, created fresh at each start and without an integrity label (WebView2 runs its browser process de-elevated). Not elevated, the profile is `<data dir>\WebView2`. |
| S-15 | Elevated, owlseye checks its program folder and the `.exe`/`.dll` files in it: if an account other than SYSTEM, Administrators, TrustedInstaller or Creator Owner owns them or may change them (write, append, delete, change permissions, take ownership, generic write or all), the header shows "⚠ elevated from an unsafe folder", with the account and folder in the tooltip. |
| S-16 | Accounts that come from files (desired state, log) and not from the scan are resolved through the system and shown and planned under the name it returns, never under the name in the file; an unresolvable SID shows as "<sid> (unresolved; the file calls it <name>)" and gets no rights (F-P2). |
| S-17 | Windows libraries called by owlseye are loaded from `System32` only; Explorer is started by its full path; directory queries use integrated sign-in with signing and sealing. |

## 14. Data formats and compatibility

All files are UTF-8 JSON without BOM, with non-ASCII characters unescaped. Files that owlseye rewrites are replaced
atomically (temp file in the same folder, then replace), so readers never see half a file. Several admins, on several
machines, may use one state folder on an admin share at the same time (D-3).

| ID | File | Format |
| --- | --- | --- |
| D-1 | `settings.json` | Object; keys see F-S6; written with indent 1, sorted keys, atomically. |
| D-2 | `desired-<name>.json` | Name: share lowercased, every run of characters outside `[\w.-]` (Unicode `\w`, so umlauts stay) → `_`, trimmed of `._`, empty → `share` (`\\fileserver\Data` → `desired-fileserver_data.json`, `E:\Share` → `desired-e_share.json`). Content: `{"share", "format": 2, "updated" (ISO UTC seconds, e.g. 2026-10-04T12:00:00+00:00), "by", "cells": [[sid, path, value], …] sorted, "names": {sid: name} (only SIDs with cells), "protected": [paths] sorted case-insensitively}`, indent 1, sorted keys. Values `R\|`, `R`, `W\|`, `W`, `F`. Valid only if `share` is a string, every cell is three strings with a SID and a known value, `names` maps SIDs to strings and `protected` holds strings. Without `format` (or below 2) the file is from before format 2 (F-D9). A file with `groups` and without `cells` (earlier model) counts as absent. Atomic replace. |
| D-3 | Locks | Read-modify-write of the desired state under a lock on byte 0 of `desired-<name>.lock` next to it; audit appends under a lock on byte 0 of the audit file itself. Both retry for about 10 s, so two owlseye instances (also on different machines using one state folder) exclude each other. Readers retry briefly while a file is locked. |
| D-4 | `audit*.jsonl` | One JSON object per line, appended. Every entry: `id` (12 hex characters), `ts` (ISO UTC seconds). Kinds and fields: `change_start` (actor, provider, share, reason, total, planned_create, planned_acl), `change_step` (run, create \| acl_op), `change` (run, actor, provider, share, reason, status, error, acl_ops, create_ops, impact), `baseline_init` (actor, provider, share, ops: []), `baseline_reset` (…, reason, status, path), `drift_accept` (…, reason, status, accepted: [drift] (at most 500), accepted_count), `settings` (…, reason, status, file, shared, adopted, changes: {key: {before, after}}, moved: [paths] or null). `acl_op` = `{path, protected_before, protected_after, before: [ace], after: [ace], changes: [change], new_folder, cleared}`; `ace` = `{sid, name, kind, mask, allow, inherited, flags}`; `change` = `{sid, name, path, before, after, auto}`; `drift` = `{change, sid, name, path, before, after}`; `impact` = `{user, display, path, before, after}`. Values use `null` for "none". Lines that are not JSON objects with an `id` are skipped when reading. |
| D-5 | `config.json` | Keys see F-S5. The settings page writes `state`, `audit` and `baseline` and removes `scan_depth`, `write`, `hidden` and `full_control` (they live in the state folder, D-8); all other keys stay. Indent 1, atomically. |
| D-6 | Sim directory | `state.json`: `whoami`, `root` (base DN), `objects` {DN: attributes with real AD names; `objectSid` as string}, `sids` {sid: [name, domain, SID_NAME_USE]}, `acls` {rel path: {protected, aces: [[type, flags, mask, sid]]}}, `config.share`, `drives` {letter: UNC}; re-read when the file changes, written atomically. `share/` = real folder tree (dot folders and names containing `\` are skipped; symlinks count as links). Inherited ACEs are emulated: the parent's CI ACEs, with INHERIT_ONLY removed and INHERITED set. Directory queries go through the LDAP filter subset (D-7). |
| D-7 | LDAP filter subset | `&`, `\|`, `!`, equality (case-insensitive), presence `=*`, prefix `=value*`, bit rules `1.2.840.113556.1.4.803`/`804`; anything else raises an error. Rows as the Windows directory adapter returns them: multi-valued `member`/`objectClass` as lists, empty values as null, `objectSid` binary. Used by the sim and by the local mode. |
| D-8 | `owlseye-settings.json` (state folder) | Object with `scan_depth`, `write`, `hidden` (list), `full_control` (list); other keys stay when it is saved. Indent 1, atomically. |

## 15. Providers and platform adapters

`AdProvider` contains the provider logic (F-SC, S-3–S-7, S-13) and works over two ports:
`IDirectory { WhoAmI, RootDn, Query(base, filter, attrs, scope) }` and
`IFilesystem { Links, ToUnc, ReadDacl, ReadFolder, LookupSid, WriteDacl, Exists, Mkdir, Subdirs }`. Every mode
implements the provider interface `Name, Share, WhoAmI, Scan(progress), FolderAcl, FolderExists, CreateFolder,
SetFolderAcl (with the scanned folder for S-13), FindGroups, ResolveAccount`.

| Mode | File system | Directory |
| --- | --- | --- |
| windows | Win32 on the share | AD via ADSI |
| local | Win32 on a local folder or UNC path | this machine's user database, presented as LDAP rows |
| sim | emulation: real folder tree, ACLs in `state.json` (D-6) | emulation from `state.json` |
| demo | in memory, no ports (F-X1) | in memory |

| Function | Windows implementation |
| --- | --- |
| Read DACL | `GetSecurityInfo` on a handle (scan, S-13) or `GetNamedSecurityInfo` by path → `RawSecurityDescriptor`; ACEs other than Allow/Deny → `other_aces` |
| Write DACL | `RawAcl` with `CommonAce`s in the given order → `SetSecurityInfo(handle, SE_FILE_OBJECT, DACL \| (UN)PROTECTED_DACL)` through the handle chain (S-1) |
| Handles, identity, final path | `CreateFile`, `GetFileInformationByHandle`, `GetFinalPathNameByHandle` |
| SID → name and kind | `LookupAccountSidW` (`SID_NAME_USE`) |
| Drive letter → UNC | `WNetGetUniversalNameW` |
| Subfolders | `DirectoryInfo.EnumerateDirectories`; reparse points go to `Links` (S-3) |
| Directory query | `DirectorySearcher` (filter, attributes, `PageSize = 1000`, subtree or base), signing and sealing; ranged `member` (F-SC13) |
| Root DN | `LDAP://RootDSE`, `defaultNamingContext` |
| Actor | `WindowsIdentity.GetCurrent().Name` |
| Local users and groups | `NetUserEnum`, `NetLocalGroupEnum`, `NetLocalGroupGetMembers`, presented as LDAP rows (`OU=Groups`/`OU=Users,DC=<PC>`), skipping RIDs 500/501/503/504 and BUILTIN groups; read once per scan and at most every 15 s otherwise |
| Domain membership (default mode, F-S2) | `NetGetJoinInformation` |
| Locks | `FileStream.Lock(0, 1)` with retry (D-3) |

| ID | Requirement |
| --- | --- |
| F-X1 | Demo provider: the built-in demo share (a made-up company: users, nested groups, folders, ACLs including typical legacy issues), in memory; writes change memory only. |
| F-X2 | Sim seed: create a sim directory (`state.json` with raw AD attributes, and the `share/` folder tree) from the demo data; an existing `state.json` is never overwritten. |
| F-X3 | Local seed (`owlseye seed-local E:\Share [--force]`, in an elevated prompt): users `owl.<name>` (disabled, random password), groups G-\*/P-\* as in the demo (nested demo groups flattened, since local groups cannot nest), folders and explicit ACLs as in the demo, other folders under the root get an empty explicit ACL; clean up leftovers of the earlier AGDLP model (`DL_FS_*` groups, earlier owl.\* users, `local-nesting.json`). Refuses a folder that is not empty and not an earlier demo share (marker file `.owlseye-demo`) unless `--force` is given. |

## 16. Non-functional requirements

| ID | Requirement |
| --- | --- |
| N-1 | Runs in the context of the signed-in admin: no service account, no stored passwords, integrated sign-in for AD. |
| N-2 | Performance budget, measured on a generated share with 2,000 folders in the matrix × 60 columns: a cell change (plan + re-render) under 300 ms; first paint of the matrix under 1 s after the scan. The scan itself is I/O-bound and must report progress at least every 500 ms. |
| N-3 | No network listener of any kind; nothing but the window talks to the logic. |
| N-4 | Distribution: one self-contained `win-x64` folder: `owlseye.exe` (a single file with the .NET runtime, all managed code and the web assets) plus the native libraries of WebView2 and WPF next to it, loaded from there; nothing is extracted to `%TEMP%` at run time. No installer: a release is a zip of that folder with `SHA256SUMS.txt`. Runs on Windows 10/11 and Windows Server with the Microsoft Edge WebView2 Runtime (F-S18). The exe is to be signed (not done yet). |
| N-5 | owlseye starts as the invoking user (`asInvoker`), so the admin's mapped drives stay visible. In local mode, where reading and writing other people's folders needs administrator rights, the header (and the start page after a failed scan) offers "Restart as administrator", which starts owlseye elevated with the same arguments; a cancelled UAC prompt shows "Restart as administrator was cancelled.". |
| N-6 | Light and dark theme, following the Windows colour scheme; the report prints in light colours. |
| N-7 | HTML-special characters, umlauts, spaces and commas in folder, group and share names are handled correctly everywhere (display, LDAP, file names). |
| N-8 | Large shares (tens of thousands of folders and differences): the matrix draws only the rows in view and redraws a row only when its content changes; long lists (preview, desired-state page, groups page) show a capped number of rows with the totals, while the actions act on everything; comparing, keeping and restoring differences takes time linear in their number. |
