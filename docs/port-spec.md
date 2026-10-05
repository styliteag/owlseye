# owlseye C# port: requirements specification

> **Status (2026-10-04):** the port is done and on `main`. The Python reference implementation (`backend/`) this
> spec refers to was removed afterwards and is not part of this repository.

Everything the C# app ([ADR 0001](adr/0001-native-windows-app-in-csharp.md)) must do so that no feature of the
Python PoC is lost. **The Python implementation is the reference:** where this document and the Python code
disagree, the code wins and this document gets fixed. Each requirement names its Python source.

Scope: **feature parity, no new features.** The UI stays English. Error and status texts are copied from
the Python code verbatim, since tests check them.

Requirement IDs: `F` functional, `S` safety, `D` data/compatibility, `N` non-functional, `X` removed.

---

## 1. Glossary

| Term | Meaning |
| --- | --- |
| Share root | Folder the matrix starts at: UNC path, a drive letter mapped to UNC, or a local path (local mode). Relative paths use `\`; `""` is the root. |
| Level | `""` = 0, `A` = 1, `A\B` = 2, … |
| Cell | Explicit Allow entry of one account on one folder: `R`, `W`, `R\|`, `W\|` or none. |
| Standard entry | Exactly one ACE with the mask/flags of the cell value (see F-R1). Anything else shows as `*` (non-standard). |
| Protected | Inheritance broken on a folder (`SE_DACL_PROTECTED`). |
| Pending | Changes collected in the UI but not yet written: cells, inheritance, new folders, clears, extra columns. |
| Automatic R\| | `R\|` that the planner adds on parent folders so an account can reach a folder below, or removes again. |
| Deviation | A folder below the matrix depth that has something of its own (see F-R9). |
| Desired state (baseline) | Cells and protected folders as owlseye last set them, per share. |
| Drift | Differences between the desired state and the actual ACLs. |
| Finding | Something that does not match the model (F-F1). |

## 2. Modes, start, configuration

| ID | Requirement | Source |
| --- | --- | --- |
| F-S1 | Four modes: **demo** (in-memory data, writes to memory), **sim** (emulated AD + real folder tree, see D-6), **local** (this machine's local users/groups + real ACLs on a local folder), **windows** (AD + file share). | `__main__.py`, `config.py` |
| F-S2 | Command line: `--demo`, `--sim [DIR]`, `--local [PATH]`, `--config FILE`. A mode flag overrides `provider` from config. `--port`, `--token`, `--open` go away (X-1). | `__main__.py` |
| F-S3 | local/windows: share = explicit path > last share for this provider (settings) > `share` from config. Without any share, exit with "No share yet: start once with --local PATH (or set share in config.json)". The opened share is remembered (F-S6). | `__main__.py` |
| F-S4 | sim: if `<sim dir>/state.json` is missing, seed it from the demo data (`sim_seed`) and report "Sim created: <dir>". Default sim dir: `<data dir>\sim`. | `__main__.py`, `sim_seed.py` |
| F-S5 | `config.json` keys: `provider` (demo), `share` (""), `max_level` (3), `scan_depth` (20; 0 = whole tree), `audit` ("" = `<data dir>\audit-<provider>.jsonl`), `sim_dir`, `baseline` ("" = data dir; demo: memory only). Unknown keys are ignored. | `config.py` |
| F-S6 | Per-admin settings in `<data dir>\settings.json`: `depth`, `share_<provider>`, `recent_<provider>` (at most 8, newest first, deduplicated case-insensitively). Missing or broken file = defaults. Written atomically. | `settings.py` |
| F-S7 | Data dir: `%LOCALAPPDATA%\owlseye` (fallback `~/.local/share/owlseye`). | `config.py` |
| F-S8 | The window opens at once. Until the first scan is done, every page shows the progress (F-B1). If the scan fails: show the error, a "Try again" button, and (local/windows) a field to open another folder instead. | `state.py`, `loading.html` |
| F-S9 | Share page (click on the share in the header): current share, provider, scan time; text field (local folder or UNC, quotes stripped), native folder picker, list of recent shares (without the current one). In demo/sim: "In <mode> mode the share is fixed." A warning if there are pending changes, since they are dropped. | `share.html`, `routes_pages.py` |
| F-S10 | Switching shares scans first. On failure nothing changes ("Cannot open <path>: <error>"). On success, pending changes are dropped, desired state and log follow the new share, and the share is remembered. | `state.switch`, `routes_pages.open_share` |
| F-S11 | `whoami` (DOMAIN\user) is shown in the header and written as `actor` into the log and the desired state. | `state.py` |

## 3. Scan

| ID | Requirement | Source |
| --- | --- | --- |
| F-SC1 | Walk the tree **iteratively** (no recursion limit) from the root, children in case-insensitive name order, down to `scan_depth` (0 = unlimited). | `ad._walk` |
| F-SC2 | Per folder: read the DACL (protected flag + all ACEs, explicit and inherited). Allow/Deny ACEs become `Ace(sid, name, kind, mask, allow, inherited, flags)`; any other ACE type sets `other_aces` (folder becomes read-only for owlseye). | `ad._read_acl_full` |
| F-SC3 | NULL DACL is shown as one ACE: Everyone, `0x1F01FF`, flags `0x3`. | `windows.read_dacl` |
| F-SC4 | One unreadable folder never aborts the scan. ACL unreadable → `error="ACL not readable: …"`, `other_aces=true`, no descent. Contents unreadable → `error="Contents not readable: …"`, no descent. | `ad._walk` |
| F-SC5 | Accounts: SID → (DOMAIN\name, kind) via LookupAccountSid, cached per scan (cache cleared at each scan). `SID_NAME_USE` 1 user, 2/4 group, 5 wellknown, 9 computer, else group; not resolvable → kind `unknown`, name = SID. The SIDs SYSTEM, Administrators, Creator Owner, Owner Rights, Everyone, Authenticated Users, BUILTIN\Users are always kind `wellknown`. | `ad._lookup` |
| F-SC6 | Principals (columns) = all SIDs from explicit ACEs of all folders plus inherited ACEs of the root, except HIDDEN (SYSTEM `S-1-5-18`, Administrators `S-1-5-32-544`, Creator Owner `S-1-3-0`, Owner Rights `S-1-3-4`). Domain SIDs (`S-1-5-21-*`) get their DN from the directory via `(objectSid=…)`, OR-batched 50 per query. | `ad._principals` |
| F-SC7 | Members: starting from the DNs of the principals, breadth-first: groups (`objectClass` contains `group`) with `member`, recursing into members not seen yet; users (`user` and not `computer`) with `sAMAccountName`, `displayName` (fallback sam), enabled = `userAccountControl & 2 == 0`, SID. Batches of 50 `(distinguishedName=…)`. | `ad._members` |
| F-SC8 | Progress phases during the scan: "Reading folders" (path, number of folders read), "Looking up the accounts in the directory", "Reading group members", "Evaluating the rights". | `ad.scan`, `state.rescan` |
| F-SC9 | After the scan: compute cells, findings, desired state, drift (F-R, F-F, F-D). Extra columns that now appear in ACLs are dropped from `extra`. | `state.rescan` |
| F-SC10 | Rescan button in the header (tooltip "Scanned <time>"), message "Rescanned." | `routes_matrix.rescan` |

## 4. Rights model (computation)

| ID | Requirement | Source |
| --- | --- | --- |
| F-R1 | Masks: `READ = 0x1200A9`, `WRITE = READ \| 0x116`, `FULL = 0x1F01FF`. Standard entries: `R\|` = (READ, flags 0), `R` = (READ, OI\|CI = 0x3), `W\|` = (WRITE, 0), `W` = (WRITE, 0x3). Rank: none 0, R\| 0.5, W\| 0.75, R 1, W 2. | `model.py` |
| F-R2 | Classification of an account's explicit Allow ACEs on a folder: if any has OI or CI, use only those (inheritable), else all; "write" = any mask has a bit of `0x2\|0x4\|0x40\|0x10000\|0x40000000\|0x10000000`. Value = inheritable ? (W or R) : (W\| or R\|). Standard = exactly one ACE and (mask, flags without INHERITED) equals the standard entry. | `rights.classify` |
| F-R3 | Explicit cells: only Allow ACEs, without HIDDEN. | `rights.explicit_cells` |
| F-R4 | Inheritance, reproduced by owlseye: R/W pass down into subfolders until a folder is protected; R\|/W\| do not pass down. The root (if not protected) receives what it inherits from above the share (inherited, inheritable Allow ACEs of the root, source "(above the share)"). Effective value = the stronger of own and incoming; source = the folder it comes from. | `rights.matrix`, `rights.outer` |
| F-R5 | Users: transitive group membership (nested, cycle-safe). A user's SIDs = own SID + Everyone/Authenticated Users/BUILTIN\Users + SIDs of all transitive groups. Effective user right per folder = strongest effective cell over these SIDs. | `rights.transitive`, `user_sids`, `user_rights` |
| F-R6 | "via": the accounts through which a user reaches a folder, each as `<name> (<right>, here \| from <source or root>)`, sorted by name. | `rights.via` |
| F-R7 | Members of a column: Everyone etc. → all users; a user column → that user; a group with a known DN → transitive user members; otherwise "unknown" (shown as `?`). | `rights.members_of` |
| F-R8 | Blocked: on a protected folder (level ≥ 1), every account that has an inheritable R/W effective on the parent and nothing here. | `rights.blocked_cells` |
| F-R9 | Deviation: protected, read error, other ACE types, or an explicit ACE of a non-HIDDEN account. | `rights.deviates` |
| F-R10 | "Covered": an account gets into a folder if it has a cell there (own or inherited), or every member already has access there through another account (typically all users with R\| on the root). | `rights.covered` |

## 5. Findings

| ID | Requirement | Source |
| --- | --- | --- |
| F-F1 | Rules, per folder in path order (texts verbatim): **high** "owlseye cannot read this folder as administrator. <error>"; **medium** suspicious name (S-6) "Name has invisible, combining or mixed-script characters (lookalike?)"; **medium** "Inheritance broken below level <depth>"; **medium** on protected folders and the root "<SYSTEM and/or Administrators> without full control here" (requires Allow, FULL mask, OI\|CI); per explicit non-HIDDEN ACE, first matching: Deny → **medium** "Deny entry for <name>"; unknown → **high** "Unresolved SID <sid>"; Everyone/Authenticated Users/BUILTIN\Users with OI or CI below the root → **high** "Broad permission for <name>"; user → **high** "Direct user entry for <name>"; below depth → **medium** "Explicit entry below level <depth>: <name>"; inherit-only without OI/CI → **low** "Entry for <name> applies to nothing". Then **low** "Non-standard entry for <name> (shown as <value>)" for non-standard cells within the depth; **medium** "<name> cannot open this folder to reach <first folder below> (R\| missing)" for unreachable cells (F-F2). | `rights.findings` |
| F-F2 | Unreachable: for each direct cell within the depth (not for unknown accounts), every ancestor where the account is not covered (F-R10). | `rights.unreachable` |
| F-F3 | Sorted by severity (high, medium, low), then path (case-insensitive), then text. Findings depend on the matrix depth and are recomputed when it changes. | `rights.findings`, `state.set_depth` |
| F-F4 | Findings page: table severity / folder (link to the folder page) / text; "No findings." when empty. Badge with the count in the navigation. | `findings.html`, `base.html` |

## 6. Matrix

| ID | Requirement | Source |
| --- | --- | --- |
| F-M1 | Rows: the root, all folders up to the matrix depth, below it only deviations (F-R9, plus folders with a pending clear) and the folders on the way to them. Tree order (case-insensitive by path component). Planned new folders are included. | `state.folders`, `state.deep` |
| F-M2 | Depth selector 1–10, saved per admin (`settings.depth`). Changing it drops pending new folders deeper than the new depth and recomputes findings. | `state.set_depth` |
| F-M3 | Columns: accounts with a cell anywhere in the share (also deeper than the matrix), extra columns, accounts with pending changes. Sorted by short name (part after `\`), case-insensitive. Header: short name, rotated; tooltip "<name> · <kind> · <n> users" (`?` if unknown); styles for extra, user, unknown columns. | `state.columns`, `_matrix.html` |
| F-M4 | Column filter: substring of the name, case-insensitive, updates while typing (≈250 ms debounce). | `matrix.html` |
| F-M5 | Cell display (after applying pending changes to a copy of the snapshot): **pending** (value or `–`, marked), **auto** (automatic R\|, marked), **direct** (value; non-standard: `*` suffix and marked), **inherited** (value, muted), **blocked** (`⊘`), **none** (empty). Tooltips: "Pending: <before> → <after>", "Added automatically: …", "Removed automatically: …", "Entry here: <label>[ (non-standard entry)]", "Inherited from <source>: <label>", "Blocked: <label> on <parent> does not reach here (inheritance broken)", "No access". | `routes_matrix.cell_info`, `_matrix.html` |
| F-M6 | Folder row header: indentation by level; collapse/expand for level-1 folders with children (UI state only, not saved, survives updates); inheritance icon `⛔` (protected, also pending) or `↳`; folder name (`＋ ` prefix for new folders, tooltip = full path); `⚠` link to the folder page if the folder has findings. Rows below the depth have a tooltip explaining why they are shown (deviation / way to one). Pending inheritance or clear is marked. | `_matrix.html`, `matrix.js` |
| F-M7 | Legend with all cell styles and the keyboard help. | `matrix.html` |
| F-M8 | Pending bar (when anything is pending): "<n> pending change(s)", list of new folders, plan error if the plan fails, **Preview** and **Discard**. | `_matrix.html` |
| F-M9 | Banner if there is drift: "<n> change(s) to ACL entries or folder inheritance made outside owlseye since the last desired state." with a link to the desired-state page. | `matrix.html` |
| F-M10 | Empty share: "No group has an entry in this share yet. Add one with ＋ Group." | `_matrix.html` |

## 7. Editing

| ID | Requirement | Source |
| --- | --- | --- |
| F-E1 | Clicking a cell opens the cell panel (F-E5). Setting a value: explicit (`R`, `W`, `R\|`, `W\|`, none) or cycle (no value given): from (pending value, else current explicit value) none→R, R\|→R, R→W, W\|→W, W→none. | `routes_matrix.set_cell` |
| F-E2 | If the new value equals the current explicit value and that entry is standard, the pending change is removed; otherwise it is set. If the resulting pending set cannot be planned (PlanError), the change is reverted and the error shown. | `routes_matrix.set_cell`, `check_plan` |
| F-E3 | Keyboard on a focused cell (ignored with Ctrl/Meta/Alt): arrows move (clamped to the grid, skipping collapsed rows); `r` → R; `w`/`m` → W; `l` or `\|` → R\|; `Shift+W` → W\|; `Delete`/`Backspace`/`0`/`-` → none; `Enter`/`Space` open the panel. Focus stays on the same cell (sid + path) after the update. | `matrix.js` |
| F-E4 | Every change updates the matrix **and** the open panel together. | `_update.html` |
| F-E5 | Cell panel: account short name, full name, kind, folder; state text by kind (direct, incl. a non-standard explanation / inherited with source and own entry / blocked / pending / auto / no access); buttons for none, R\|, R, W\|, W (current one highlighted) unless the folder has `other_aces` (then "This folder's ACL cannot be read completely or has entries of other types; owlseye does not rewrite it."); a note below the depth; for blocked cells "Restore inheritance on <folder>"; for inherited cells an explanation and "Break inheritance on <folder>" where possible; members (first 30, "… and <n> more"; unknown/none texts); link "Who has access to <folder>?". | `_panel_cell.html` |
| F-I1 | Inheritance can be toggled on every existing folder except the root (also below the depth, for cleanup). Toggling back to the current state removes the pending change. Validation as in the Python code. | `routes_matrix.set_inheritance`, `acl.can_toggle` |
| F-I2 | Clear (reset to default): allowed on existing folders below the root without other ACE types that are protected or have explicit entries. Toggling again takes it back. | `state.can_clear`, `routes_matrix.clear_folder` |
| F-I3 | Folder panel: name, path, level; state (new folder pending with "Cancel this folder" / clear pending / protected or inherits, pending note); buttons Break/Restore inheritance and Clear/Undo clear with explanations; "New subfolder" form (only if level < depth and the folder is not new itself), list of pending new subfolders with cancel; "Groups with access" (name, effective right, here / from <source>), sorted by right descending then name; findings of this folder; link to the folder page. | `_panel_folder.html`, `folder_panel_ctx` |
| F-N1 | New folder: name trimmed, max 200 characters, parent must exist or be pending, level ≤ depth, valid name (S-5), not existing, not already pending. Creation order: parents first. A new folder inherits; rights can be set on it before applying. | `create.py`, `routes_matrix.new_folder` |
| F-N2 | Cancelling a new folder also cancels pending new folders below it and pending cells on them. | `routes_matrix.cancel_new_folder` |
| F-G1 | "＋ Group" panel: prefix search on `sAMAccountName` of groups in the directory (max 50, sorted, without HIDDEN) plus built-in groups by localized name or alias (Users/Benutzer `S-1-5-32-545`, Power Users/Hauptbenutzer `-547`, Remote Desktop Users/Remotedesktopbenutzer `-555`, Authenticated Users/Authentifizierte Benutzer `S-1-5-11`, Everyone/Jeder `S-1-1-0`). Already shown accounts are marked "column", others get "Add". | `ad.find_groups`, `_panel_groups.html` |
| F-G2 | Searching for administrators/administratoren/system/creator owner/ersteller-besitzer (prefix) shows the explanation that owlseye sets SYSTEM and Administrators itself. | `routes_matrix.hidden_hint` |
| F-G3 | An added column stays (as extra) until it appears in an ACL or pending changes are discarded. | `state.add_column` |
| F-E6 | Discard drops all pending changes and extra columns. | `state.clear` |

## 8. Planner

| ID | Requirement | Source |
| --- | --- | --- |
| F-P1 | Input: cell changes, inheritance changes, new folders, clears, extra columns, depth. Output: ACL operations per folder (path, protected before/after, explicit ACEs before/after, cell changes with auto flag, new_folder, cleared), create operations, user impact, snapshot after. | `planner.build` |
| F-P2 | Validation, raising PlanError with the Python texts: invalid new folders (F-N1); clear only on existing folders below the root without other ACE types; inheritance only on existing folders below the root, not with other ACE types, not when breaking a folder that inherits an unresolved SID; no HIDDEN accounts; unknown account; unknown folder; value not in the four cells; folder with other ACE types; a folder path with a bad component (S-5) is refused in the preview already. | `planner.build`, `_protect` |
| F-P3 | Breaking inheritance: explicit = previous explicit + all inherited ACEs converted to explicit (INHERITED flag removed), deduplicated by (sid, allow, mask, flags without INHERITED). New folders: only the flag changes. | `acl.after_break` |
| F-P4 | Restoring inheritance: remove explicit ACEs whose (sid, allow, mask) the parent passes down (parent ACE with CI). "This folder only" entries stay. | `acl.after_restore` |
| F-P5 | Setting a cell replaces all Allow ACEs of that account on the folder with the one standard entry (or removes them). Deny entries and other accounts stay. | `acl.set_cell` |
| F-P6 | A change to the same value is a no-op, except on a non-standard entry, which gets normalized to the standard entry. | `planner.build.differs` |
| F-P7 | Automatic R\| add: for every account that gets a right on a folder, add `R\|` on each ancestor where it is not covered (F-R10) and has no change of its own. | `planner._traverse` |
| F-P8 | Automatic R\| remove: when an account's entry is removed, walk up the ancestors (deepest changes first): remove an ancestor's `R\|` only if it is a standard `R\|`, has no change of its own, and no other right of that account remains below; stop at the first ancestor that does not qualify. An `R\|` set by hand on a folder without rights below stays. | `planner._traverse` |
| F-P9 | On every written protected folder, ensure SYSTEM and Administrators have FULL with OI\|CI. | `acl.ensure_admins` |
| F-P10 | ACE order: Deny before Allow, otherwise stable. | `acl.canonical` |
| F-P11 | Clear: inheritance on, all cells of the folder removed (so automatic R\| above goes too), and then **all** remaining explicit ACEs dropped (Deny, special entries, SYSTEM/Administrators). A clear overrides cells and inheritance changes on the same folder. | `planner.build` |
| F-P12 | Operations sorted by (level, path case-insensitive). Folders whose protected flag and ACE set do not change are skipped (new folders without own entries are only created). | `planner.build` |
| F-P13 | Impact: per user and folder, effective right before → after where it differs; sorted by path, user. "Gained" = rank after > rank before. | `planner.impact`, `gained` |

## 9. Preview, apply, undo

| ID | Requirement | Source |
| --- | --- | --- |
| F-A1 | Preview page: cards (folders, entries changed, new folders, rights gained, rights lost); new folders (with note "Creating a folder cannot be undone automatically."); ACL changes per folder (clear with the number of deny entries removed, break/restore lines, cell changes before → after, "automatic R\|" tag); explanatory notes; user impact table; reason/ticket field; Apply; Back. Plan error or empty plan show their message. | `preview.html` |
| F-A2 | Apply applies exactly the plan that was shown: if the plan has changed since the preview, go back to the preview with "The plan has changed, please review it again." | `routes_apply.apply` (`plan_hash`) |
| F-A3 | Conflict check against the live file system before writing: a planned new folder already exists → conflict; for each ACL op on an existing folder, re-read the ACL of the folder (and of its parent when the protected flag changes) and compare the protected flag and the ACE set (sorted by sid, allow, mask, flags without INHERITED) with the snapshot. Conflict → rescan, back to the preview with "<reason> Rescanned, please review again." | `routes_apply.conflict` |
| F-A4 | Writing: log `change_start` (actor, provider, share, reason, total, planned_create, planned_acl) **before** the first write. Create folders first, then ACLs in plan order. After each step log `change_step` (`create` or `acl_op`); after each ACL write update the desired state for that operation (F-D5). If saving the desired state fails once, record the error and stop trying for further operations. Stop at the first failed write. Finally log `change` (status ok/error, error, acl_ops done, create_ops done, impact), clear pending, rescan. | `routes_apply.execute`, `apply` |
| F-A5 | Messages: "<n> changes applied (log <id>). Users get them at their next access." / "Error after <n> of <total> operations: <error>". | `routes_apply.apply` |
| F-A6 | Writing a DACL sets the explicit ACEs and the protected flag; Windows computes inherited ACEs and propagates into the subtree. | `ad.set_folder_acl`, `windows.write_dacl` |
| F-U1 | Undo from a log entry (only entries with ACL operations in the current format, including unfinished runs): refuse if it belongs to another share ("This log entry belongs to another share"), or if it cannot be parsed ("Log entry cannot be read (older format?): …"). Otherwise drop pending changes, set each changed cell back to its `before` value where the current value differs, add extra columns for accounts no longer present, set inheritance back where it differs, then open the preview. Folder creation is not undone. | `routes_apply.undo` |

## 10. Desired state and drift

| ID | Requirement | Source |
| --- | --- | --- |
| F-D1 | Desired state = cells (sid, path, value), names (sid → name, for display), protected folders. On a scan without a desired-state file, the current state becomes the desired state and `baseline_init` is logged, under the file lock (a second instance must not overwrite the first one's). | `state._load_desired` |
| F-D2 | Drift on all scanned levels: `folder_gone` (folder in the desired state no longer exists; its cell differences are not listed separately), entry `added`/`removed`/`changed`, inheritance `broken`/`restored`. Sorted by path, name, change. Key = `change\|sid\|path`. | `drift.diff` |
| F-D3 | Desired-state page: file location (or "in memory only (demo)"), state (not saved yet / earlier group model / last written <time> by <who> / cannot be read), counts, log path; "Delete desired state…" with reason and confirmation; drift table with checkboxes (all checked, select-all); "Restore desired state…"; reason field + "Keep current state"; user impact desired → current. A broken file shows the error, and comparison stays off until it is fixed or deleted. Badge in the navigation: count or `!` on error. | `drift.html` |
| F-D4 | Keep (accept): the selected items become the desired state; log `drift_accept` with the items and the reason; rescan. | `drift.accept`, `routes_apply.drift_accept` |
| F-D5 | Own applies carry the written operations into the desired state, so they are not reported as drift, also for other admins sharing the baseline folder; unrelated drift stays. | `drift.applied` |
| F-D6 | Restore (revert): turn the selected items into pending changes and open the preview. Missing folders are recreated (with missing parents) together with their desired cells and protected flag; accounts no longer present get extra columns. | `drift.revert` |
| F-D7 | Delete desired state: delete the file (also a broken one), log `baseline_reset` (reason, path), rescan; the current state becomes the desired state. | `routes_apply.drift_reset` |
| F-D8 | Drift impact: what the outside changes did for users (desired → actual), missing folders excluded. | `drift.impact` |

## 11. Other pages

| ID | Requirement | Source |
| --- | --- | --- |
| F-V1 | Users page: search (sam + display name), list sorted by display name; the selected user shows every folder in the matrix with access: right and "via" (F-R6). | `users.html` |
| F-V2 | Folder page: full path, level, protected, number of users with access; findings; "Who has access?" (user with link to the users page, right; sorted by right descending then name); explicit ACL table (account, kind, Deny or label, mask `0x%06X`, flags `0x%02X`). | `folder.html` |
| F-L1 | Log page: log path; newest first, at most 200 entries; columns time (UTC), who, reason (+ error), changes, undo button. Rendering per kind: `baseline_init`, `baseline_reset`, unfinished `change_start` ("Not finished: <n> of <total> operations written." + not-written paths + hint), accepted drift items, created folders, ACL ops (cleared / break / restore / cell changes with "(auto)"), earlier-model entries (`ops`, ACL ops without `protected_before`) as muted lines. | `audit.html` |
| F-L2 | Folding: an apply logs start, steps, end; the end is the entry. A start without an end stays as the entry, with its steps as its operations and the planned but unwritten paths as `missing`. Broken or foreign lines are skipped. | `audit.fold` |

## 12. Progress, busy state, closing

| ID | Requirement | Source |
| --- | --- | --- |
| F-B1 | Progress status: task (scan / apply / idle), phase text, current path, done, total. Scan: indeterminate bar + "<n> folders read"; apply: bar "<done> of <total> done" + "Keep owlseye open until this is finished." | `progress.py`, `_progress.html` |
| F-B2 | During rescan, apply, share switch, drift accept/reset: overlay with the live progress; the controls cannot be triggered twice. The UI stays responsive (work runs off the UI thread). | `busy.js` |
| F-B3 | Closing the window while ACLs are being written asks first: "owlseye is writing ACLs right now." with "Keep open" (default) / "Close anyway" and the explanation from `electron/main.js`. | `electron/main.js` (removed) |
| F-B4 | Phases during apply: "Checking the ACLs for outside changes", "Creating folders", "Writing ACLs", then the rescan phases. | `routes_apply` |
| F-B5 | Flash messages after actions (green; errors red). | `base.html` |
| F-B6 | Header on every page: brand, navigation (Matrix, Users, Findings + badge, Desired state + badge, Log), share (link to the share page), actor, provider tag, Rescan. | `base.html` |

## 13. Safety

| ID | Requirement | Source |
| --- | --- | --- |
| S-1 | **Handle chain on every write and mkdir:** open every component from the share root to the target with `FILE_FLAG_OPEN_REPARSE_POINT \| FILE_FLAG_BACKUP_SEMANTICS`, no `FILE_SHARE_DELETE`, keep all handles open until done; refuse if a component is a reparse point or not a directory. Second layer: the final path of the leaf must equal the root's final path + rel (case-insensitive). Write the DACL via `SetSecurityInfo` on the leaf handle (`WRITE_DAC`). | `windows._open_chain`, `write_dacl` |
| S-2 | mkdir: hold the parent chain open, create, then verify the new folder through the chain; if that fails, remove it again and fail. | `windows.mkdir`, `sim.mkdir` |
| S-3 | Junctions/symlinks: never descended during the scan; recorded per scan. A write on a folder that has a recorded link anywhere below is refused (propagation could follow it), until the open question in the ADR is settled. | `windows.subdirs`, `ad._refuse_links_below` |
| S-4 | Before every write, re-read the DACL; if it now has other ACE types, refuse ("owlseye does not rewrite it"). Folders with `other_aces` or a read error are never written. | `ad.set_folder_acl` |
| S-5 | Bad path components (`""`, `.`, `..`, trailing dot or space, `\ / : * ? " < > \|`, control characters < 32): such folders are flagged ("Name Windows cannot address as written"), never read, never descended, never written; reading/writing such paths is refused. New folder names additionally may not be reserved device names (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM1–9, COM¹²³, LPT1–9, LPT¹²³), also with an extension or trailing spaces before the dot. | `model.bad_component`, `bad_folder_name` |
| S-6 | Suspicious names (finding, see F-F1): Unicode category Cf, Co, Cn, or a Zs other than the normal space; not NFC; Latin mixed with Greek or Cyrillic letters. | `model.suspicious_name` |
| S-7 | Paths deeper than `scan_depth` are refused for reads and writes. | `ad._check_folder` |
| S-8 | Drive letters are converted to UNC (`WNetGetUniversalName`); on failure, explain that elevated processes cannot see normally mapped drives (text from `windows.to_unc`). Local mode: absolute local path, must exist. | `windows.to_unc`, `local.to_unc` |
| S-9 | LDAP filter values are escaped (RFC 4515): everything outside `[A-Za-z0-9 =,.-_]` as `\xx` per UTF-8 byte. | `ad.ldap_escape` |
| S-10 | A desired-state file that is broken, has an unexpected format, or belongs to another share (same sanitized name) is **never overwritten**; the error is shown. | `baseline.py` |
| S-11 | Only ACLs are written and folders created. owlseye never changes groups or members, never deletes folders, never touches deny entries or other accounts' entries except through an explicit clear/break/restore. | README |
| S-12 | The UI loads only its own content. External links open in the default browser; navigation away from the app is blocked; developer tools are off in release builds. | `electron/main.js` (removed) |

## 14. Data formats and compatibility

All files are UTF-8 JSON and must stay **read and write compatible with the Python version** (both may run side by
side during the transition, also on a shared admin share).

| ID | File | Format | Source |
| --- | --- | --- | --- |
| D-1 | `settings.json` | Object; keys see F-S6; written with indent 1, sorted keys, atomically (temp file + replace). | `settings.py` |
| D-2 | `desired-<name>.json` | Name: share lowercased, every run of characters outside `[\w.-]` (Unicode `\w`, so umlauts stay) → `_`, trimmed of `._`, empty → `share`. Content: `{"share", "updated" (ISO UTC seconds, e.g. `2026-10-04T12:00:00+00:00`), "by", "cells": [[sid, path, value], …] sorted, "names": {sid: name} (only SIDs with cells), "protected": [paths] sorted case-insensitive}`, indent 1, sorted keys, non-ASCII unescaped. A file with `groups` and without `cells` (earlier model) counts as absent. Validation as `baseline._validate`. Atomic replace. | `baseline.py` |
| D-3 | Locks | Read-modify-write of the desired state under a lock on byte 0 of `desired-<name>.lock`; audit appends under a lock on byte 0 of the audit file itself. Python uses `msvcrt.locking` (`LockFile`, retries for ≈10 s); C# must use the same byte range (`FileStream.Lock(0, 1)`) and retry, so both versions exclude each other. | `audit.file_lock` |
| D-4 | `audit*.jsonl` | One JSON object per line, appended. Every entry: `id` (12 hex characters), `ts` (ISO UTC seconds). Kinds and fields: `change_start` (actor, provider, share, reason, total, planned_create, planned_acl), `change_step` (run, create \| acl_op), `change` (run, actor, provider, share, reason, status, error, acl_ops, create_ops, impact), `baseline_init` (actor, provider, share, ops: []), `baseline_reset` (…, reason, status, path), `drift_accept` (…, reason, status, accepted: [drift]). `acl_op` = `{path, protected_before, protected_after, before: [ace], after: [ace], changes: [change], new_folder, cleared}`; `ace` = `{sid, name, kind, mask, allow, inherited, flags}`; `change` = `{sid, name, path, before, after, auto}`; `drift` = `{change, sid, name, path, before, after}`; `impact` = `{user, display, path, before, after}`. Values use `null` for "none". | `audit.py`, `routes_apply.py` |
| D-5 | `config.json` | See F-S5. | `config.py` |
| D-6 | Sim directory | `state.json`: `whoami`, `root` (base DN), `objects` {DN: attributes with real AD names; `objectSid` as string}, `sids` {sid: [name, domain, SID_NAME_USE]}, `acls` {rel path: {protected, aces: [[type, flags, mask, sid]]}}, `config.share`, `drives` {letter: UNC}; re-read when the file changes, written atomically. `share/` = real folder tree (dot folders and names containing `\` are skipped; symlinks count as links). Inherited ACEs are emulated: the parent's CI ACEs, with INHERIT_ONLY removed and INHERITED set. Directory queries go through the LDAP filter subset (D-7). | `sim.py` |
| D-7 | LDAP filter subset | `&`, `\|`, `!`, equality (case-insensitive), presence `=*`, prefix `=value*`, bit rules `1.2.840.113556.1.4.803`/`804`; anything else raises an error. Rows like ADODB: multi-valued `member`/`objectClass` as lists, empty as null, `objectSid` binary. | `ldapfilter.py`, `sim.query_objects` |

## 15. Providers and platform mapping

The ports stay as they are (`providers/ports.py`): `Directory { WhoAmI, RootDn, Query(base, filter, attrs, scope) }`
and `Filesystem { Links, ToUnc, ReadDacl, LookupSid, WriteDacl, Exists, Mkdir, Subdirs }`. `AdProvider` contains the
logic (F-SC, S-3–S-7) and implements the provider interface: `Name, WhoAmI, Scan(progress), FolderAcl, FolderExists,
CreateFolder, SetFolderAcl, FindGroups`.

| Python | C# / .NET |
| --- | --- |
| `GetNamedSecurityInfo` + pywin32 ACL decode | `GetSecurityInfo` / `GetNamedSecurityInfo` (P/Invoke) → `RawSecurityDescriptor`; non-Allow/Deny ACEs → `other_aces` |
| `ACL.AddAccessAllowedAceEx` …, `SetSecurityInfo` | `RawAcl` with `CommonAce` in the given order → binary → `SetSecurityInfo(handle, SE_FILE_OBJECT, DACL \| (UN)PROTECTED_DACL)` |
| `CreateFile`, `GetFileInformationByHandle`, `GetFinalPathNameByHandle` | CsWin32 P/Invoke with `SafeFileHandle` |
| `LookupAccountSid` | P/Invoke `LookupAccountSidW` (needs `SID_NAME_USE`) |
| `ConvertSidToStringSid`, binary SID | `SecurityIdentifier` |
| ADODB `<base>;filter;attrs;subtree`, page size 1000 | `DirectorySearcher` (filter, `PropertiesToLoad`, `PageSize = 1000`, `SearchScope.Subtree`) behind `IDirectory.Query` |
| `RootDSE.defaultNamingContext` | `new DirectoryEntry("LDAP://RootDSE")` |
| `GetUserNameEx(NameSamCompatible)` | `WindowsIdentity.GetCurrent().Name` |
| `NetUserEnum`, `NetLocalGroupEnum`, `NetLocalGroupGetMembers` | `System.DirectoryServices.AccountManagement` (`ContextType.Machine`), presented as LDAP rows (`OU=Groups`/`OU=Users,DC=<PC>`) as in `local.py`, skipping RIDs 500/501/503/504 and BUILTIN groups |
| `WNetGetUniversalName` | P/Invoke `WNetGetUniversalNameW` |
| `os.scandir` + reparse attribute | `DirectoryInfo.EnumerateDirectories` + `FileAttributes.ReparsePoint` |
| `msvcrt.locking` | `FileStream.Lock(0, 1)` with retry |

| ID | Requirement | Source |
| --- | --- | --- |
| F-X1 | Demo provider: the same demo share (users, groups, folders, ACLs including the legacy issues) as `demo.py`, in memory; writes change memory only. | `demo.py` |
| F-X2 | Sim seed: create a sim directory from the demo data. | `sim_seed.py` |
| F-X3 | Local seed (admin, as a subcommand, e.g. `owlseye seed-local E:\Share`): users `owl.<name>` (disabled), groups G-\*/P-\* as in the demo (nested demo groups flattened, since local groups cannot nest), folders and explicit ACLs as in the demo, other folders under the root get an empty explicit ACL; clean up leftovers of the earlier AGDLP model (`DL_FS_*` groups, earlier owl.\* users, `local-nesting.json`). | `local_seed.py` |

## 16. Non-functional requirements

| ID | Requirement |
| --- | --- |
| N-1 | Runs in the context of the signed-in admin: no service account, no stored passwords, integrated sign-in for AD. |
| N-2 | Performance budget, measured on a generated share with 2,000 folders in the matrix × 60 columns: a cell change (plan + re-render) under 300 ms; first paint of the matrix under 1 s after the scan. The scan itself is I/O-bound and must report progress at least every 500 ms. |
| N-3 | No network listener of any kind. |
| N-4 | One signed, self-contained `win-x64` executable (`dotnet publish -p:PublishSingleFile=true`), with its native libraries next to it rather than extracted to `%TEMP%` at run time. WebView2 handling according to the ADR's open question 1. |
| N-5 | Elevation according to the ADR's open question 2; "restart as administrator" replaces `start-local.ps1`. |
| N-6 | Light and dark theme as today (`app.css`). |
| N-7 | HTML-special characters, umlauts, spaces and commas in folder, group and share names are handled correctly everywhere (display, LDAP, file names). |

## 17. Removed (no longer needed)

| ID | What | Why |
| --- | --- | --- |
| X-1 | `--port`, `--token`, `--open`, `OWLSEYE_TOKEN`, `OWLSEYE_READY`, `/auth`, `/health`, session cookie, CSRF token, SameSite handling | no HTTP |
| X-2 | Electron shell, splash, `preload.js`, `taskkill` cleanup | one process |
| X-3 | PyInstaller, `run_backend.py`, `build-windows.ps1`, `start-local.cmd/.ps1` | `dotnet publish`, manifest, F-S2 |
| X-4 | HTMX, `htmx.min.js`, `busy.js` polling, `HX-Refresh` | Blazor rendering, progress events |
| X-5 | Browser mode (`--open`, using the UI in a normal browser) | to be confirmed (ADR open question 4) |

## 18. Acceptance

1. **Test parity.** Every Python test has a C# counterpart with the same name in PascalCase (166 tests in
   `test_acl_model`, `test_acl_planner`, `test_acl_sim`, `test_acl_app`, `test_acl_hardening`, `test_desired`,
   `test_names`, `test_progress`, `test_share`, `test_ldapfilter`). App tests that checked HTTP behaviour
   (token, CSRF, 404) are replaced by tests of the UI state layer; the HTTP-only ones are listed in the PR as
   dropped, with the reason.
2. **Golden files.** A Python exporter writes, for the demo and sim data and a set of change scenarios (cells,
   break/restore, clear, new folders, drift accept/revert, undo): cells, findings, plan (ACL ops, create ops,
   auto R|), impact, drift. The C# tests reproduce them exactly.
3. **Compatibility.** The C# app reads a data directory written by the Python app (settings, desired states,
   audit with finished and unfinished applies) and shows the same drift, log and undo; and the other way round.
4. **Lab test** (local mode and a test domain): scan, set cells, break/restore/clear, new folders, conflict
   check (change an ACL in Explorer between preview and apply), stop during an apply (log shows the unfinished
   run, undo works), junction inside the subtree, drive letter vs. UNC, elevated vs. not elevated.
5. **This checklist** is ticked off requirement by requirement in the final PR.

## 19. Port status (2026-10-04)

| Area | Status | How it was verified |
| --- | --- | --- |
| 2 Modes, start, configuration | done (`Launch`, `Startup.cs`) | ShareTests (start rules), app run in demo/sim/local, missing folder at start |
| 3 Scan | done (`AdProvider`) | AclSimTests, AclHardeningTests, Win32 integration tests |
| 4 Rights model, 5 Findings | done | AclModelTests, NamesTests, findings page in the app |
| 6 Matrix, 7 Editing | done | AclAppTests, app: click, keyboard (all keys), panels, depth, filter, collapse, new folder, ＋ Group, clear |
| 8 Planner | done | AclPlannerTests (59 cases) |
| 9 Preview, apply, undo | done | AclAppTests, ProgressTests, app: apply and undo in demo/sim/local (icacls checked) |
| 10 Desired state, drift | done | DesiredTests, app: outside change in the sim, keep and restore |
| 11 Other pages | done | app: users, folder, findings, log, share |
| 12 Progress, busy, closing | done | ProgressTests, busy overlay in the app; close guard asks with Yes/No |
| 13 Safety | done | AclHardeningTests, Win32Tests (handle chain, junction below and in the path, conflict) |
| 14 Data formats | done | the app read a data folder written by the Python version (log, desired state, undo) |
| 15 Providers | Demo, Sim, Local done; Windows (ADSI) written, **not run against a domain** | lab spike pending |
| 16 Non-functional | N-2 met (about 100 ms per change on 2,100 × 60, start about 3.5 s); N-3/N-4 met (no listener, one exe plus its native libraries, not yet signed) | measured with a generated sim share |
| 17 Removed | X-1 to X-5 removed | |

Beyond the Python version (new features):

- **Access rights report** (`Reports/RightsReport.cs`, page `/report`): PDF via WebView2 `PrintToPdfAsync`, Excel via
  a small built-in OpenXML writer (`Reports/Xlsx.cs`, validated with the OpenXML SDK: no schema errors). Tests:
  `ReportTests`; the smoke test opens the page.
- Members through the primary group (Domain Users), member ranges of large AD groups, paths beyond 260 characters,
  parallel scan, window title per page.

Deviations from the Python version, all deliberate:

- **F-S3:** without any share the window opens anyway and asks for a folder (the PoC exited with a message); a share
  that cannot be opened at start shows the error and offers another folder.
- **F-A2:** the plan hash is SHA-256 over the plan as JSON (the PoC hashed Python's `repr`); it never leaves the process.
- **F-E2:** an action refused by the planner shows its reason in the panel (HTMX dropped 400 responses silently).
- **F-E5:** the pending text reads "Pending: – → W" (the PoC printed "Pending: Pending: – → W").
- **F-B3:** the close warning is a Yes/No message box ("Close anyway?").
- **X-5:** there is no browser mode any more.
- **Acceptance 2 (golden files):** replaced by the complete port of the Python tests (all cases, same names); the
  golden-file exporter was not needed.
- **Tests skipped (3):** token/CSRF, serving the keyboard script, the busy-overlay markup: HTTP mechanics that no
  longer exist.
