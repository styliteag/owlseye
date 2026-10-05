# ADR 0001: Native Windows app in C# (.NET 10, WPF + Blazor Hybrid)

- Status: accepted, implemented on branch `csharp-port` (2026-10-04)
- Date: 2026-10-04
- Deciders: Wim Bonis
- Feature scope of the port: [port-spec.md](../port-spec.md)

## Context

owlseye today is three runtimes glued together over loopback HTTP:

```
Electron (Node + Chromium)  --spawn-->  PyInstaller exe (Python + FastAPI + uvicorn + pywin32)
        |                                         ^
        +---- http://127.0.0.1:<port>  -----------+   token in env, cookie, CSRF header, /health polling
```

The PoC has proven the model (rights matrix, automatic R|, inheritance, desired state, audit, undo). What the
stack costs us now:

- **Two runtimes for one window.** Electron only starts the backend, shows a splash while PyInstaller
  unpacks, polls `/health`, and offers one native call (folder picker). It accounts for most of the
  download size.
- **A network listener in an admin tool.** The backend runs with the admin's rights and listens on
  127.0.0.1. Token, cookie, CSRF and SameSite exist only to keep other local processes (on a terminal
  server: other users) away from it. Without the listener, none of that is needed.
- **Distribution.** PyInstaller executables are often flagged by virus scanners, especially on servers.
  Signing and a UAC manifest are awkward across the Electron/PyInstaller split; `start-local.ps1` works
  around elevation.
- **The Windows adapter is the weakest part.** `providers/windows.py` (ADSI via ADODB/COM, Win32 security
  via pywin32) has never run against a real domain. pywin32 cannot decode some ACE types
  (`NotImplementedError`), and ADODB needs its own escaping rules.

The logic (≈2,500 lines: model, rights, acl, planner, create, baseline, drift, audit) has no Windows
dependencies and is covered by 166 tests. The UI is ≈500 lines of Jinja/HTMX plus ≈130 lines of JS.

## Decision

Port owlseye to **C# on .NET 10 (LTS)** as one signed Windows executable:

| Project | Target | Contents |
| --- | --- | --- |
| `Owlseye.Core` | `net10.0` (any OS) | model, rights, acl, planner, create, baseline, drift, audit, settings, config, progress, `AdProvider` logic over the ports, LDAP filter, Demo and Sim providers |
| `Owlseye.Windows` | `net10.0-windows` | `Win32Fs` (handle chain, DACL read/write), `AdsiDirectory`, `LocalDirectory`, SID lookup |
| `Owlseye.App` | `net10.0-windows` | WPF window hosting **Blazor Hybrid** (`BlazorWebView`): Razor components call Core directly, no HTTP |
| `Owlseye.Tests` | `net10.0` | xUnit port of the Python tests plus golden-file parity tests; runs on Windows, macOS, Linux |
| `Owlseye.Windows.Tests` | `net10.0-windows` | integration tests against the local provider (admin) |

Key choices inside the decision:

- **Platform APIs.** Read and write DACLs as binary security descriptors via `System.Security.AccessControl`
  (`RawSecurityDescriptor`, `RawAcl`, `CommonAce`), which keeps unknown ACE types as opaque ACEs instead of
  failing. Open handles with `CreateFileW` (`FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`) and
  write with `SetSecurityInfo` on the handle, through CsWin32-generated P/Invoke. Look up SIDs with
  `LookupAccountSid` (P/Invoke, because we need `SID_NAME_USE`). Query AD with `System.DirectoryServices`
  (`DirectorySearcher`, integrated sign-in, page size 1000). Read the local SAM with
  `System.DirectoryServices.AccountManagement` (`ContextType.Machine`).
- **The ports stay.** `Directory` (`Query(base, filter, attrs, scope)` returning rows) and `Filesystem` keep
  their shape, so Sim, Local and Windows plug into the same `AdProvider` logic as today.
- **UI.** Each Jinja template becomes a Razor component. CSS (`app.css`) carries over. HTMX goes away; the
  matrix keeps its keyboard model (arrows, R/W/L/Shift+W/Del) through Blazor key handlers, with JS interop
  only where focus handling needs it. Progress is pushed as events instead of polled.
- **Data stays compatible.** `settings.json`, `desired-<share>.json`, `audit*.jsonl` and the sim `state.json`
  keep their format and location (`%LOCALAPPDATA%\owlseye`, or the paths in `config.json`). The C# app
  reads what the Python app wrote and the other way round, including the file locks (byte 0 via
  `LockFileEx`), so both versions can run side by side during the transition.
- **Python stays until parity.** The Python implementation is the reference. It is removed together with
  Electron only after the parity checklist in the spec is green and the lab test has passed.

## Alternatives considered

| Option | Why not |
| --- | --- |
| **Keep Python, replace Electron with pywebview** | About one day of work, and the HTML/HTMX stays. But the loopback HTTP server, token and CSRF remain (removing them would mean rewriting HTMX to pywebview's JS bridge), PyInstaller false positives remain, and pywin32 stays the weakest layer. Good as a stopgap, not as a target. |
| **Rust + Tauri** | Tauri can keep the HTML through a custom URI scheme without a socket. But Win32 security through windows-rs needs a lot of `unsafe` handwork, and ADSI via COM or LDAP with Kerberos is laborious in Rust. Most effort, least gain for a Windows-only admin tool. |
| **C / C++** | Same platform access as C#, without the safety net, in a tool whose job is rewriting ACLs. No. |
| **C# with a native WPF matrix (no WebView2)** | No WebView2 runtime dependency. But the matrix, panels and all pages would have to be designed from scratch in XAML; the HTML/CSS work is lost. Revisit only if WebView2 is a blocker on target machines (see open questions). |
| **C# + Avalonia / Photino (cross-platform UI)** | Keeps a UI on macOS/Linux for sim mode. Adds a less common stack for a benefit only developers need; Core tests already run everywhere. |

## Consequences

Positive:

- One process, one runtime, one signed exe. No port, token, cookie, CSRF, `/health`, splash, `OWLSEYE_READY`
  handshake or child-process cleanup.
- Platform calls go through documented, maintained .NET APIs instead of pywin32 and ADODB command strings.
- A UAC manifest and a "restart elevated" action replace `start-local.ps1`.
- Strong typing for the plan and ACL structures that are written to disk and the audit log.

Negative:

- A full rewrite of logic and tests. Mitigation: a 1:1 port of the 166 tests, plus golden files produced by
  the Python implementation (matrix cells, findings, plans, impact, drift for demo and sim scenarios) that
  the C# side must reproduce exactly.
- The UI needs the **WebView2 runtime**. It ships with Windows 11 and current Windows 10, and is often missing
  on Windows Server 2019/2022. Mitigation: bundle the Evergreen bootstrapper or a fixed-version runtime.
- **No UI on macOS/Linux.** Sim mode remains for tests (Core) but is no longer clickable outside Windows.
- Blazor re-renders the whole matrix component on change. Large shares (hundreds of rows × dozens of
  columns) need `@key`, row components with `ShouldRender`, and a measured performance budget (see spec,
  N-2).

## Migration plan

0. **Spike `Owlseye.Windows`** against the local provider and a lab domain: read/write DACL through the
   handle chain, SID lookup, AD query with nested members. Settle the open question from `windows.py`
   (does `SetSecurityInfo` propagation follow junctions inside the subtree?).
1. **Core + tests**: port modules in dependency order (model → rights → acl → create → planner → baseline →
   drift → audit → settings/config), each with its tests. Add the golden-file exporter on the Python side
   and the parity tests on the C# side.
2. **Providers**: Demo, Sim (including `sim_seed`), Local (including `local_seed` as a subcommand), Windows.
3. **UI**: shell (header, navigation, flash, progress/busy, close guard), matrix and panels, preview/apply,
   desired state, users, folder, findings, log, share.
4. **Packaging**: `dotnet publish` self-contained, win-x64, signed; WebView2 handling.
5. **Sign-off and cleanup**: parity checklist green, lab test passed, then remove `backend/`, `electron/`,
   `build-windows.ps1`, `start-local.*`, and update the README.

## Decisions taken during the implementation

- **Elevation:** the exe starts `asInvoker` (an elevated process cannot see the admin's mapped drives). In local mode the
  header offers *Restart as administrator*, which replaces `start-local.ps1`.
- **Layout:** `src/` and `tests/` at the repository root. The Electron shell (`electron/`) was removed on 2026-10-04.
  The Python implementation (`backend/`) and `start-local.*` were removed the same day at the owner's decision, ahead of
  the lab test (sign-off step 5), when the port reached parity and went to `main`.
- **Packaging:** self-contained single-file exe, compressed, with the native libraries next to it:
  `publish\owlseye.exe` (about 66 MB) plus six DLLs of WebView2 and WPF, about 75 MB in all. All web assets (our
  `wwwroot` and the framework's `blazor.webview.js`) are embedded resources served by a custom file provider. The WebView2
  profile lives in `%LOCALAPPDATA%\owlseye\WebView2` (elevated: a separate one, created fresh at each start; a high integrity label on it locked out WebView2, whose
  browser process runs de-elevated).
  At first the native libraries were bundled into the exe too, but .NET extracts them to `%TEMP%\.net\…` at start,
  where any unelevated process of the user can replace them before an elevated owlseye loads them (security review,
  finding 2). So they are not bundled (`IncludeNativeLibrariesForSelfExtract=false`); the folder has to be installed
  where only administrators can write, which an elevated owlseye checks and reports in the header. P/Invoke loads
  Windows libraries from `System32` only.

## Implementation notes

- Blazor Hybrid serves the app from `https://0.0.0.1/` (since .NET 8, not `0.0.0.0`); foreign links open in the
  default browser by BlazorWebView's default, no own `UrlLoading` handler.
- The former HTTP routes live in `Owlseye.Ui.Session` (view models plus actions), so the UI logic is tested without a UI.
- Large shares: the plan is remembered until the pending changes change, per-scan rights are cached for the planner,
  user rights after a change are recomputed only for affected users, the matrix body is virtualized and rows redraw only
  when their content signature changes. 2,100 folders × 60 groups: about 100 ms per change (was 1.25 s).
- Junctions inside the subtree: on local NTFS, `SetSecurityInfo`'s propagation updates the junction object but does not
  follow it into the target (integration test). Over SMB still open (lab); the refusal to write above links stays.
- Locks use `FileStream.Lock(0, 1)` (byte 0 like `msvcrt.locking`); the C# app reads desired-state and audit files
  written by the Python version (checked with real PoC data, including undo of a Python log entry).

## Open questions

1. Where does owlseye run: on admin workstations (Windows 10/11, WebView2 present) or also on file servers
   (Server 2019/2022, WebView2 often missing)?
2. ~~Elevation~~: decided, see above.
3. Code-signing certificate: which one, and who signs releases?
4. Is losing the sim UI on macOS acceptable, or is a browser-based dev mode needed? (Core and its tests run on macOS.)
