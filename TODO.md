# WinShell — TODO / handoff

Custom Windows 7-style taskbar and Start menu for Windows 11, living at `src/WinShell/`.
This file is the handoff for a fresh session with no prior context. Read it fully before
changing anything — several non-obvious decisions here were made for reasons that are easy to
undo by accident.

---

## 1. Goal

Replace the Windows 11 taskbar and Start menu with a minimal, Windows 7-style, low-resource
alternative, and disable the Windows 11 shell surfaces that back them.

**The original user request had two parts, and only one is architecturally underway:** the
taskbar exists in skeleton form; the Start menu does not exist at all.

---

## 2. Environment gotchas — read before building

These cost a lot of time to rediscover.

- **The machine is ARM64.** Only ARM64 .NET runtimes are installed. Always build and run with
  `-p:Platform=arm64`. An x64 build launches under emulation, finds no x64 runtime, and pops a
  **".NET needs to be installed" dialog** at the user. That dialog is a live process with a
  plausible working set — it is very easy to mistake for the app and measure it by accident.
  If a launched app behaves oddly, **check its top-level window class**: `#32770` is a dialog,
  `WinShellTaskbar` is ours. Note the repo README documents x64 as the default; it is wrong for
  local runs here.
- **No C++ toolchain and no Windows SDK are installed.** No `cl.exe`, no `link.exe`, no
  `vswhere`. This is why the project is C# rather than C++, and why `PublishAot` is not enabled
  yet (NativeAOT requires `link.exe`).
- **The project root is a UNC path** (`\\Mac\winos-utils\`, a share from the user's Mac). This
  appears to break Claude Code's in-workspace path check, causing a permission prompt on every
  file edit despite `defaultMode: acceptEdits`. Path-scoped rules were added to
  `.claude/settings.local.json`. If prompts persist, map a drive letter
  (`net use Z: \\Mac\winos-utils /persistent:yes`) and work from `Z:\`.
- **The published binaries run under a different process name.** `dist/` ships them renamed, so
  a running instance appears as `WinShell-arm64` / `WinShell-x64`, *not* `WinShell`.
  `Get-Process -Name WinShell` silently returns nothing while one is running, which makes
  cleanup checks report a clean machine that is not clean, and makes `dist/` copies fail with a
  file lock for no visible reason. Match on `Get-Process | Where-Object { $_.Path -like
  '*WinShell*' }` instead.
- **Child processes are killed when a shell command finishes.** To launch the app and inspect
  it, do the launch, the sleep, the measurement and the `Stop-Process` **all inside a single
  PowerShell invocation**, or the process will be gone by the next call.

### Build and run

```powershell
dotnet build src\WinShell\WinShell.csproj -c Debug -p:Platform=arm64

# ALWAYS use --no-hide while developing: it leaves the native taskbar visible so a crash
# cannot leave the machine with no taskbar at all.
src\WinShell\bin\arm64\Debug\net9.0-windows\WinShell.exe --no-hide

# RECOVERY: un-hide the native taskbar without starting a shell. Run this if a force-kill or
# crash left the machine with no taskbar (see 4.1). Safe to run any time; no-op if nothing
# is hidden.
src\WinShell\bin\arm64\Debug\net9.0-windows\WinShell.exe --restore
```

Two things that will waste your time when testing this:

- **`ShowWindow` on another process's window is asynchronous.** It returns before Explorer has
  processed it, so `IsWindowVisible` polled immediately afterwards still reports the *old*
  state. Sleep ~2 s before asserting, or you will conclude a working restore has failed.
- **`FindWindow` cannot find our windows from another process** — not by class
  (`WinShellTaskbar`) and not by title, even though the window is an ordinary top-level window
  with a null parent that `EnumWindows` finds fine. Test harnesses must locate it by enumerating
  and matching the class.
  Root cause still unidentified. `CS_GLOBALCLASS` was the obvious candidate and was **tested and
  disproven** — adding it changed nothing, and the by-title failure had already ruled out a
  class-atom explanation. Do not re-try that one. Note this is not what blocks §4.3: there,
  `FindWindow` resolves fine, it just resolves to *Explorer's* window rather than ours.

---

## 3. Current state

### Works, verified running

- Appbar-docked window along the bottom edge. Verified docking at `(0,1462)-(2466,1542)` on a
  2466x1638 screen at 200% scaling — correctly pushed *above* the native taskbar by the
  `ABM_QUERYPOS` negotiation rather than overlapping it.
- Window list with per-window icons, ellipsised titles, foreground highlight with accent
  underline, dimmed text for minimised windows. **Button order is stable**: entries keep their
  index across refreshes and only new windows are appended. Rebuilding straight from
  `EnumWindows` reordered the bar on every window switch, because that returns Z-order and
  activating an app moved its button to the first slot.
- Volume, network and battery indicators, with a volume slider flyout (§4.3).
- Click to activate; click again on the foreground window to minimise.
- Clock (time over date), repainting only when the displayed minute changes.
- **Start menu (Phase 3) — implemented and verified.** Two-column Windows 7 layout: filterable
  program list plus search box on the left, shell folders and power actions on the right.
  Verified by driving the real UI: open on Start click, toggle shut on a second click, dismiss
  on click-away, type-to-filter, and launching Notepad end-to-end through the menu.

### Measured

Re-measured 2026-07-18, 4-core ARM64 box:

| Metric | Taskbar only | After opening the Start menu |
|---|---|---|
| Working set | 24–31 MB | **63–67 MB** |
| Private memory | 6.4–9.6 MB | 13–16 MB |
| Idle CPU | 0.36–0.63% of one core | 0.47% |

Leak-tested over 12 open/close cycles: GDI handles flat at 115, USER objects flat at 39, memory
plateaus at ~66.7 MB and stops growing. The cost is one-time, not per-open.

#### Shell-replacement mode vs Windows 11 — measured 2026-07-18

Like-for-like, with WinShell running as the shell (taskbar + Start menu opened once + 2 live
tray icons) and Explorer not running at all:

| | Working set | Private (commit) | CPU |
|---|---|---|---|
| Windows 11 (explorer + SearchHost + StartMenuExperienceHost) | 447.2 MB | 103.2 MB | 0.73–2.0% |
| WinShell as shell | **67.3 MB** | **16.3 MB** | 0.42% |
| **Saved** | **~380 MB** | **~87 MB** | roughly half |

The commit figure is the one that matters and it is a real win: ~87 MB of genuinely freed
memory, not reclaimable working set. Caveats, so this is not read as better than it is:

- **`SearchHost` and `StartMenuExperienceHost` terminate on their own** once Explorer stops
  being the shell. They are not in the Phase B numbers at all.
- **Phase B had no desktop** at the time these numbers were taken — no wallpaper, no desktop
  icons. That has since been fixed by `DesktopWindow.cs` (see §4.6), so the functional loss is
  gone, but **the memory figures above predate it and were not re-measured.** Expect the
  desktop to add to them: a decoded wallpaper is a screen-sized bitmap (~15 MB at 2466x1638
  32bpp), plus one 48px icon per file and a back buffer of the same size.
- Opening File Explorer in this mode spawns an `explorer.exe` again, so day-to-day usage will
  sit above the 67 MB floor.
- CPU is a single 30 s sample and Explorer's idle CPU is noisy (0.68–2.03% observed across this
  session). WinShell's own 0.4–0.6% is consistent across many runs; the *direction* is reliable,
  the exact delta is not.

**The Start menu roughly doubles working set, and this needs a decision.** §6 rejects WPF on the
grounds that ~70 MB is "worse than the shell surfaces it replaces" — opening our menu once puts
us at 66.8 MB, essentially at that threshold.

The cause is walking the AppsFolder shell namespace: it loads shell extension DLLs and the UWP
resource machinery into our process, and the icons add to it. Two mitigating facts:

- **Private commit only grows ~10 MB** (6.4 → 16.3 MB). Most of the working-set jump is shared
  shell DLL pages that are already resident system-wide for Explorer's benefit; our process is
  charged for mapping them, not for allocating them.
- The cost is only paid once, on first open, and does not grow with repeated use.

Task Manager still shows the user 66.8 MB, so if the headline number matters, options are:
release icons and PIDLs when the menu has been closed for a while (reclaims some), or do the
enumeration in a short-lived helper process and cache the results (reclaims most, much more
complex). Neither is done.

Idle CPU is **not** zero. Any app that retitles itself once a second (a terminal showing
elapsed time, a browser showing load progress) fires `EVENT_OBJECT_NAMECHANGE`, which currently
triggers a full window-list rebuild and an unconditional repaint. Already reduced from 1.17% by
caching icons and narrowing hook ranges. See §5 for the remaining fix.

### Hide / restore — verified 2026-07-18 (was "never executed")

`NativeTaskbar.Hide()` / `Reapply()` / `Restore()` have now been run for real on this machine.
Results:

| Case | Result |
|---|---|
| Hide: native bar disappears, ours remains | pass |
| Clean exit (`WM_CLOSE`) restores it, exit code 0 | pass |
| `ABM_REMOVE` releases reserved space (work area back to full height) | pass |
| Explorer restart → 5 s re-hide timer re-hides the new bar | pass |
| Force kill (`Stop-Process -Force`) | **leaves the bar hidden** — mitigated, see below |
| Log off / restart (`WM_ENDSESSION`) | **still unverified** — not testable without ending the session |

Force-kill leaving the bar hidden was predicted and is now confirmed: neither `finally` nor
`ProcessExit` runs. Mitigated by the new **`--restore` flag** (`Program.cs`), which un-hides the
native bar without starting a shell and is verified to recover from exactly that state.

The appbar reservation, by contrast, does **not** leak on a force kill — Windows releases it
when the window is destroyed. The earlier suspicion in §5 was wrong.

`WM_ENDSESSION` is the one path left untested. It is wired in `TaskbarWindow.WndProc` and shares
the same `Restore()` used by every verified path, so the risk is low, but it is unproven.

### Not started

System tray, Start menu, shell-host disabling, NativeAOT, multi-monitor, and the whole tail in
§4.5.

---

## 4. Remaining work, in recommended order

### 4.1 Verify hide/restore — DONE (2026-07-18), except log-off

Results are in §3. `--restore` was added as the force-kill recovery path and is verified.

Remaining, both small:

1. **`WM_ENDSESSION` on log off / restart** — the only unverified restore path. Test it the next
   time a reboot is happening anyway rather than forcing one.
2. **Nothing auto-runs `--restore`.** Recovery today is manual: the user must know the flag
   exists and have a way to run it with no taskbar. Worth deciding between a watchdog process,
   a `RunOnce` registry entry written at hide-time and cleared on clean exit, or simply
   accepting it as documented. The registry approach is the cheapest and survives a hard power
   loss, which a watchdog does not.

### 4.2 Disable the Windows 11 shell hosts — LARGELY MOOT, see §4.3

**Do not start this work before reading this.** Once WinShell runs as the shell (§4.3),
`SearchHost` and `StartMenuExperienceHost` terminate by themselves — measured, they are simply
absent. The policy-key work described below buys nothing in that configuration.

This section stays relevant only for the *non*-shell mode, where Explorer still runs and
WinShell merely hides its taskbar. In that mode the numbers below still apply, and they are
much weaker than the original plan assumed.

`--disable-search` exists but be clear about what it can and cannot do, because the obvious
approaches were tried and failed:

- **`SearchHost.exe` (132 MB) cannot be kept down while Explorer is the shell.** Killing it
  works for a few seconds and Explorer relaunches it on demand — measured, it came back at
  143 MB. Writing `DisableSearchBoxSuggestions` under `HKCU\Software\Policies` is **access
  denied** even though the key exists. The only thing that actually removes it is
  `--install-shell`, after which it never starts.
- **Indexing (`WSearch` / `SearchIndexer.exe`, 34 MB) needs elevation.** `--disable-search`
  self-elevates through a UAC prompt to run
  `Stop-Service WSearch -Force; Set-Service WSearch -StartupType Disabled`. Declining the
  prompt changes nothing. `--enable-search` reverses it.
- Bing/Cortana values under `HKCU\...\CurrentVersion\Search` do write successfully.



**This is what the user actually asked for, and none of it is written.** The taskbar rewrite
buys the *look*; the memory savings come entirely from here.

**The headline saving in the original plan was overstated. Read this before investing in 4.2.**

Re-measured 2026-07-18, two 30 s samples. Working set *and* private (commit) bytes, because the
distinction turns out to decide whether this phase is worth doing:

| Process | Working set | Private (commit) |
|---|---|---|
| explorer.exe (taskbar + desktop + File Explorer) | 198 MB | 46 MB |
| SearchHost.exe | 131–133 MB | 33 MB |
| StartMenuExperienceHost.exe | 125 MB | 27 MB |

Killing SearchHost + StartMenuExperienceHost drops ~211 MB of **working set** but only
**~34 MB of commit**. Those two are suspended UWP apps: Windows already trims their working set
on memory pressure, so most of that 211 MB is memory the system reclaims for free when something
else wants it. The ~34 MB of commit is the only part that is genuinely freed.

So the realistic figure is **tens of MB, not ~258 MB** — and our own bar costs 31 MB of working
set / 9.6 MB commit, which eats most of it. Explorer's 198 MB cannot be touched at all: it owns
the desktop and File Explorer, and stays alive by design (§6).

**Current net effect of running WinShell is a regression**, since 4.2 is unimplemented and every
Windows 11 surface is still running underneath:

| | Working set | Private | CPU |
|---|---|---|---|
| Win11 default | 455 MB | 106 MB | noisy, see below |
| + WinShell (native bar hidden) | 502–507 MB | 131 MB | +0.4–0.6% |

Hiding the native taskbar does **not** reduce Explorer's memory — reproducibly across both runs
it *rose* ~15 MB (198 → 213 MB WS, 46 → 61 MB commit), likely appbar registration overhead.
Explorer's CPU was too noisy to draw any conclusion (baseline alone ranged 0.68–2.03%).

None of this makes the taskbar pointless — the Win7 *look* was half the original request — but
4.2 should be justified on removing shell surfaces the user dislikes, not on RAM. If RAM is the
real goal, this phase does not deliver it.

Implement as a page in the existing WinUI/WPF `WinOsUtils` app, following the established
scan/apply pattern in `src/WinOsUtils/Services/StartMenuTweaker.cs` (a `Rule` list with
`Evaluate`/`Remediate`, surfaced through `RemediationCheck`). Targets: `SearchHost` /
`SearchApp`, `StartMenuExperienceHost`, `Widgets` / `WidgetService`, Copilot. Note
`CopilotRemover.cs` and `SearchService.cs` already exist — check for overlap before writing new
code. **Everything must be reversible**, matching the existing pages' contract.

Caveat: these hosts are relaunched by Windows on demand. Killing is not enough; they need policy
keys to stay down.

### 4.3 Phase 2 — system tray — WORKING, 2026-07-18

Implemented in `TrayHost.cs`, rendered by `TaskbarWindow.PaintTray`. Verified end-to-end with
Explorer's taskbar absent: three synthetic icons plus **Windows Security's real icon** appeared on
our bar, the latter re-registering itself in response to our `TaskbarCreated` broadcast.
`NIM_ADD`, `NIM_MODIFY` and `NIM_DELETE` all handled; left- and right-clicks are forwarded to the
owning window as its registered callback message; icons whose owner dies are pruned on the 1 s
timer.

**The undocumented payload layout, confirmed against real captures.** `WM_COPYDATA` arrives with
`dwData == 1` and `cbData == 1484`. Offsets are from the start of the payload:

| Offset | Field |
|---|---|
| 0 | `dwSignature` = `0x34753423` |
| 4 | `dwMessage` (`NIM_*`) |
| 8 | `cbSize` = 956 |
| 12 | `hWnd` |
| 16 | `uID` |
| 20 | `uFlags` |
| 24 | `uCallbackMessage` |
| 28 | `hIcon` |
| 32 | `szTip[128]` |

**Every handle in this struct is 32 bits, even on ARM64/x64.** This is the detail that breaks
naive implementations, and it is not a guess: `cbSize == 956` only adds up with 32-bit handles
(20 + 4 + 256 + 8 + 512 + 4 + 128 + 4 + 16 + 4), and the 32-bit reading is the only one that
yields sane values — `uFlags == 7` (`MESSAGE|ICON|TIP`) and `uCallbackMessage == 0x800` (`WM_APP`,
exactly what `System.Windows.Forms.NotifyIcon` uses). Reading them as 64-bit produces garbage
that still *looks* plausible at a glance.

Incoming `hIcon` values are `CopyIcon`'d, because the owning app may destroy the original at any
time. Those copies are owned and destroyed on delete/replace/shutdown — the same ownership rule
as `AppList`, and the opposite of the taskbar's borrowed window icons (§6).

#### The catch: this requires replacing Explorer as the shell

The tray only works when Explorer's `Shell_TrayWnd` does not exist. While it does, it wins
`FindWindow` unconditionally and every `Shell_NotifyIcon` call goes there. Measured, not assumed:

| Question | Answer |
|---|---|
| Can we register the `Shell_TrayWnd` class while Explorer holds it? | **Yes** — classes are per-process |
| Does `FindWindow` then resolve to ours? | **No** — Explorer's, even when ours is created later |
| Does closing Explorer's tray window help? | **No** — `WM_CLOSE` is ignored, and `DestroyWindow` only works on your own thread's windows |
| Does it work with Explorer's taskbar gone? | **Yes** — verified, see above |

Mirroring Explorer's tray instead is also dead on Windows 11: a system-wide sweep found **no
`ToolbarWindow32` anywhere**, and `TrayNotifyWnd` has no children. The notification area is a
XAML island, so there are no toolbar buttons left to enumerate.

So the tray is gated on `--install-shell`, which sets the per-user Winlogon `Shell` value
(`HKCU`, no admin needed) to WinShell. `--uninstall-shell` reverts it, `--shell-status` reports
both HKLM and HKCU values.

`TrayHost` is created automatically when no Explorer `Shell_TrayWnd` exists at startup — i.e.
exactly when we are the shell. It is deliberately *not* created alongside a live Explorer:
registering a competing `Shell_TrayWnd` there could win the race for some app and swallow its
icon into a tray nobody is rendering. `--tray` forces it on for testing.

**Expect an empty right-hand side when running alongside Explorer.** That is not a bug, it is
this constraint: Explorer receives every `Shell_NotifyIcon` call and our tray has nothing to
show.

**Installing is now done from the UI**, not the command line: Start menu → *WinShell Settings*,
or `WinShell.exe --settings` to open the same window standalone (useful when the shell is not
running). It shows current shell status, installs, restores Explorer, and disables indexing.

The install button **copies the build to `%LOCALAPPDATA%\WinShell` and registers that copy**,
which is what makes installing from the Mac share safe — the login shell starts before network
shares mount, so registering a UNC path would guarantee a blank screen. It also means the
registered shell survives rebuilding or deleting the source tree.

**Still unverified, and it is the important one: nobody has actually signed in with this set.**
Everything above was tested by killing Explorer at runtime, which is not the same as being the
login shell. Before trusting it:

- ~~**The desktop, wallpaper and desktop icons are Explorer's and will be gone.**~~ Confirmed on
  2026-07-18 by signing in with the shell set, and now **fixed**: WinShell draws its own
  desktop. See §4.6. Running `explorer.exe` to get the desktop back was the alternative and was
  rejected — it recreates Explorer's taskbar, which takes `Shell_TrayWnd` and therefore the tray
  with it (§4.3), and gives back most of the memory saving.
- Recovery if the screen is blank: Ctrl+Shift+Esc still opens Task Manager with no shell running,
  then File > Run new task > `explorer.exe`, then `--uninstall-shell`.
- `--install-shell` refuses to run from a UNC path. The login shell starts before network shares
  mount, so installing from `\\Mac\winos-utils\...` would guarantee a blank screen. Copy to a
  local disk first.

Not implemented: the overflow/hidden-icon area, hover tooltips, balloon notifications.

#### Replacing the shell breaks UWP activation — fixed by `ShellLaunch.cs` (2026-07-18)

Found by a user report of "Settings won't open", and it was much wider than Settings.

With Explorer not registered as the shell, the immersive-shell activation path that
`ShellExecute` delegates to for packaged apps is not registered either. Measured on this
machine:

| Target | via `ShellExecute` | Result |
|---|---|---|
| `ms-settings:display` | direct | fails, `0x80040900` |
| `shell:AppsFolder\<AUMID>` | direct | fails, "class not registered" |

So **every Store app in the Start menu was silently unlaunchable**, not just Settings —
`AppList.Launch` used exactly that AppsFolder path. Three routes were tested for real:

1. `explorer.exe <uri>` — works for protocols (`SystemSettings` launched), **not** for
   AppsFolder: it leaves a stray `explorer.exe` sitting on a modal "OK" dialog.
2. `IApplicationActivationManager::ActivateApplication` — works for packaged apps by AUMID,
   and does not involve Explorer at all.
3. Direct `ShellExecute` — still correct for ordinary files, folders and `.lnk`.

`ShellLaunch.Open` routes on what the target actually is: `!` in an AppsFolder parsing name
means a packaged AUMID and goes to the activation manager; everything else goes to
`ShellExecute`, falling back to `explorer.exe` **only for protocol URIs** — that restriction is
what stops route 1's stray dialog. `CoAllowSetForegroundWindow` is called first, or the
activated app opens behind the caller.

Verify with `WinShell.exe --open ms-settings:display` and
`--open "shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"`.

##### Launching is only half of it: UWP apps also need somewhere to draw

Fixing activation was not enough, and the second half is much less obvious. With activation
working, `SystemSettings.exe` **ran** — and still nothing appeared on screen. The reason:

- A true UWP app does not own its window. It renders inside an `ApplicationFrameWindow`
  created by `ApplicationFrameHost.exe`, which is part of the **immersive shell**.
- The immersive shell is bootstrapped by `ShellExperienceHost`, which Explorer starts as part
  of being the shell. Nothing starts it when WinShell is the shell.
- So the app process starts, never gets a frame, and sits there invisible. The symptom is a
  click that does nothing — indistinguishable from a launch that failed.

Measured while diagnosing this:

| State | `SystemSettings` | `ApplicationFrameWindow` |
|---|---|---|
| Nothing done | runs | none |
| `ApplicationFrameHost` started by hand | runs | **none** — AFH alone is not sufficient |
| `ShellExperienceHost` started | runs | **`'Settings'` visible** |

Also worth knowing, because it makes the bug look inconsistent: **packaged Win32 apps are
unaffected.** Notepad is an MSIX-packaged Win32 app with an ordinary `Notepad` window class and
displays fine with no immersive shell at all. Only true CoreWindow apps (Settings, Store) break.
"Some Store apps work and some do not" is this distinction, not a flaky bug.

`ShellLaunch.EnsureImmersiveShell()` starts `ShellExperienceHost` on demand and polls for
`ApplicationFrameHost` before returning. **Lazy, not at startup**, and deliberately so: it costs
~15 MB private commit across the two hosts, and a session that never opens a Store app should
not pay it.

Note this also partly reclaims §4.2 — `ShellExperienceHost` is no longer purely something to be
rid of; it is a dependency for anything packaged.

Explorer, incidentally, cannot be used to fix this: launched while the HKCU `Shell` value points
at WinShell, it **refuses to create `Progman`** and never initialises the immersive shell, so
running it alongside achieves nothing here. Verified 2026-07-18.

#### Volume / network / battery are NOT tray icons

Worth stating plainly because it is the obvious wrong assumption: these never arrive through
`Shell_NotifyIcon`. Explorer renders them itself as separate shell surfaces, so no amount of
owning `Shell_TrayWnd` will make them appear — they had to be built from scratch.

`SystemIndicators.cs` implements all three and they work regardless of whether we are the shell:

- **Volume** — Core Audio (`IMMDeviceEnumerator` → `IMMDevice` → `IAudioEndpointVolume`), again
  through raw COM vtable slots to keep the AOT path clean. Slot numbers are in the file.
  Clicking opens `VolumeFlyout.cs`, a popup slider with drag, click-anywhere-on-track, a mute
  button and a live percentage. Scrolling the taskbar icon adjusts volume without opening it.
- **Network** — `InternetGetConnectedState`. Connected/disconnected only, no signal strength.
- **Battery** — `GetSystemPowerStatus`, hidden entirely when the machine reports no battery.

Glyphs come from the **Segoe MDL2 Assets** font rather than shipped images, which is what keeps
this asset-free. Verified live: volume reported 33%, mute toggled `False -> True -> False`, and
dragging the slider to 98% moved the real system volume. State is polled every 2 s on the
existing timer and only repaints when a glyph actually changes.

Right-clicking an indicator opens the matching `ms-settings:` page.

Note §6's "Explorer is hidden, not killed" reasoning is now partly overtaken: with WinShell as
the shell, Explorer no longer owns the desktop either. That decision should be re-read as
"do not kill Explorer *while it is the shell*", which is still true and still why
`NativeTaskbar.Hide()` exists for the non-shell mode.

### 4.4 Phase 3 — Windows 7-style Start menu — DONE (2026-07-18)

Implemented in `AppList.cs` + `StartMenuWindow.cs`. `StartMenu.Toggle()` no longer forwards to
the native menu, so **the ordering constraint on 4.2 is now satisfied** — disabling
StartMenuExperienceHost will no longer kill the Start button.

Two decisions worth knowing before changing this code:

- **Apps come from AppsFolder, not from scanning `.lnk` files.** The original plan listed both
  as separate sources. AppsFolder is a superset: it contains classic shortcuts *and* Store apps,
  so one enumeration replaces two. This matters more than it sounds — on this machine the
  `.lnk` folders hold 40 entries, but Settings, Terminal, Notepad, Paint, Photos and the Store
  exist *only* as AppsFolder items. A shortcut-only menu would have silently omitted most of
  what a Windows 11 user opens. Everything launches through
  `shell:AppsFolder\{ParsingName}` regardless of which kind it is.
- **COM is called through raw vtable slots, not `[ComImport]` interfaces.** Generated COM
  interop needs reflection and would break the AOT path §6 exists to protect. The vtable calls
  are confined to the bottom of `AppList.cs` and documented with their slot numbers.
  `Program.cs` must keep the `CoInitializeEx` STA call — without it enumeration returns nothing
  and the menu is silently empty.

#### Search

Three sources, merged into one result list, with no search index involved anywhere:

- **Apps** — matches every whitespace-separated token against both the display name and the
  parsing name, so "notepad" finds the Store Notepad through its AppUserModelID.
- **Settings** — a static catalogue of ~55 `ms-settings:` pages in `SettingsCatalog.cs`. Add
  entries there; nothing enumerates them at runtime.
- **Files** — a live filesystem walk (`FileSearch.cs`) over Desktop, Documents, Downloads,
  Pictures, Music and Videos. Bounded on purpose: 350 ms, 40 results, 6 levels deep, skipping
  hidden/system entries, reparse points, `node_modules`, `.git` and `AppData`.

File search runs on a 250 ms debounce timer and only for queries of 3+ characters, so typing
stays responsive and each keystroke does not kick off a disk walk. Widening the roots or the
budget is a one-line change in `FileSearch.cs` if the trade lands differently in practice.

Still plain substring matching, so "charmap" does not find "Character Map" — the real Start menu
resolves that through the index we are deliberately not using.

Not done, in rough priority order:

1. **Icons are never released until exit**, and neither are PIDLs. See the memory note in §3.
2. No pinned/recent/frequently-used section — the list is a flat alphabetical roll of everything.
3. No right-click context menu (pin, open file location, run as administrator).
4. No jump lists, no folder grouping, no keyboard launch of right-column items.
5. The app list is enumerated once on first open and never refreshed, so an app installed while
   WinShell is running will not appear until restart.

### 4.5 Not yet ticketed, but needed for daily use

Multi-monitor (Phase 1 is primary-monitor only — `Dock()` uses `SM_CXSCREEN`/`SM_CYSCREEN`),
right-click context menus (close/pin), window thumbnail previews, autostart at login, drag to
reorder, window grouping, show-desktop button, jump lists, keyboard shortcuts (Win key capture,
Alt+Tab integration).

### 4.6 Phase 5 — the desktop — DONE (2026-07-18)

Signing in with WinShell as the shell confirmed the warning in §4.3: no wallpaper, no icons, no
right-click, just the desktop background colour. The desktop is a window Explorer creates
(`Progman`, containing `SHELLDLL_DefView`) and it only creates it when it starts and finds no
shell already running — so with WinShell owning the Winlogon `Shell` value, nothing ever builds
one.

Two ways out, and the choice matters:

1. **Spawn `explorer.exe` and hide its taskbar.** ~30 lines, reusing `NativeTaskbar.Hide()`.
   Rejected: Explorer then owns `Shell_TrayWnd`, so `TrayHost` is skipped (§4.3) and every
   third-party tray icon disappears. It also gives back most of the memory saving. The Win11
   workaround of reading Explorer's tray via `TB_GETBUTTON` is already ruled out — that moved
   to a XAML island (§4.3).
2. **Draw our own.** Chosen. Keeps the tray, keeps the memory win, and reuses the raw-vtable
   COM approach `AppList` already established.

`DesktopWindow.cs` is a full-screen window pinned to `HWND_BOTTOM` on **every**
`WM_WINDOWPOSCHANGING`, not just at creation — it is activatable (F2 and Delete need the
keyboard), so without re-asserting the pin, clicking it would raise it over real windows.

What works, and how it was verified — see the `--desktop-status` flag, which exists precisely
because a screenshot cannot tell "correct at 200% DPI" apart from "scaled by the compositor":

| Piece | How |
|---|---|
| Wallpaper | `IDesktopWallpaper` for path/position, registry fallback; GDI+ flat API to decode (GDI reads only BMP); fill/fit/stretch/centre/tile, `HALFTONE` resampling |
| Icons | 48px from `SHGetImageList(SHIL_EXTRALARGE)` — `SHGFI_ICON` tops out at 32px |
| Layout | Column-major into a cell grid, clamped to the work area so nothing hides under the taskbar; positions persisted to `%LOCALAPPDATA%\WinShell\desktop-layout.tsv` |
| Context menus | Real `IContextMenu` — background via `CreateViewObject` (slot 8), items via `GetUIObjectOf` (slot 10). Probed with `--desktop-status --menus`: 5 background entries, 22 for a `.lnk` |
| Rename / delete | Subclassed `EDIT` over the label; delete through `SHFileOperation` with `FOF_ALLOWUNDO` so it reaches the Recycle Bin |
| Refresh | `FileSystemWatcher` on both Desktop folders, posting to the message loop |

**Icons here are owned, not borrowed.** `IImageList::GetIcon` returns a fresh `HICON` every
call, so they are destroyed on refresh and at shutdown — the same rule as `AppList`, the
opposite of `WindowList`. See §6.

Deliberately not done yet: multi-monitor (primary only, matching the taskbar), and drag-and-drop
*from* the desktop into other windows. Drops *onto* the desktop use `WM_DROPFILES`, which is far
simpler than implementing `IDropTarget` but does not handle non-file drags (a URL from a
browser) or show drop feedback.

`IContextMenu2` is queried and held for the life of a popup so `WM_INITMENUPOPUP` can be
forwarded — without it, "Open with" and "New" appear as arrows that never populate.

### 4.7 Phase 4 — NativeAOT

Install VS Build Tools with the C++ workload, then set `PublishAot=true`. The code is already
written AOT-clean, so this should need **no source changes**. Expect ~27 MB → ~12 MB and much
faster startup. Verify the `[UnmanagedCallersOnly]` callbacks and `[LibraryImport]` stubs
survive trimming.

---

## 5. Known issues

- **Idle CPU 0.83%, should be near zero.** `Refresh()` runs a full `EnumWindows` and then
  unconditionally calls `InvalidateRect` on every `EVENT_OBJECT_NAMECHANGE`. Fix: diff the new
  list against the old (handles + titles + minimised state) and skip the repaint when nothing
  visible changed. Optionally throttle refreshes to ~4/sec.
- **Force-kill leaves the native taskbar hidden** — confirmed, not suspected. `WinShell.exe
  --restore` recovers it, but nothing runs that automatically yet (§4.1.2).
- **Force-kill also leaks the appbar reservation.** An earlier note here claimed Windows
  reclaims it; that was wrong and has been retested. A killed instance leaves its strip
  reserved, and the visible symptom is the next launch docking *higher up the screen* on each
  run, above a gap nothing draws in. Measured: 176 px reserved with no WinShell running (96 px
  native taskbar + 80 px orphan).
  **Only restarting Explorer clears it.** `SPI_SETWORKAREA` does not stick — the shell
  recomputes the work area from its registered appbar list — and issuing `ABM_QUERYPOS` from a
  live appbar does not prune dead entries either. Both were tried and measured.
  Hide mode is now immune regardless: `Dock()` pins to the screen bottom instead of negotiating
  when the native bar is suppressed, so a stale reservation no longer displaces the bar. Only
  `--no-hide` (development) still gets pushed around by orphans.
- **`FindWindow` cannot see our taskbar window cross-process** (§2). Unexplained, and a
  prerequisite to understand before §4.3.
- **Nobody has signed in with WinShell as the shell yet.** The tray is verified only by killing
  Explorer at runtime. See §4.3 before flipping `--install-shell`.
- **Primary monitor only.** The Start menu inherits this: it anchors to the taskbar's rectangle,
  so it follows the bar, but neither is multi-monitor aware.
- **Start menu doubles working set on first open** (§3). Unresolved trade-off, not a bug.
- Start menu app list never refreshes after first open; newly installed apps need a restart.

---

## 6. Design decisions — do not undo casually

- **Raw Win32 + GDI, no WPF/XAML.** A WPF taskbar costs ~70 MB, which is *worse than the shell
  surfaces it replaces*, defeating the entire purpose. Do not "modernise" this to WPF/WinUI.
- **`[LibraryImport]` not `[DllImport]`, `[UnmanagedCallersOnly]` not delegates, zero
  reflection.** This is what keeps the AOT path in §4.6 free. The AOT/trim analyzers are enabled
  in the csproj and the build is currently warning-clean — keep it that way.
- **All state is static.** The window procedure must be a static function pointer to stay
  AOT-compatible, so there is no instance to route messages to.
- **Explorer is hidden, not killed.** The taskbar is only a window inside `explorer.exe`, which
  also owns the desktop, File Explorer, and shell plumbing other apps depend on. Killing it to
  remove a window takes all of that with it.
- **`WS_EX_NOACTIVATE`** on the taskbar window — without it, clicking a task button deactivates
  the very window being switched to.
- **`asInvoker`, not elevated.** Running elevated breaks drag-and-drop from Explorer (UIPI) and
  makes every launched app inherit admin.
- **Event-driven window tracking, never polling.** A polling timer is the main reason naive
  taskbar replacements burn CPU at idle.
- **Taskbar icon handles are borrowed, not owned.** They come from `WM_GETICON` / the window
  class and belong to the target app — never `DestroyIcon` them. `PruneIconCache` forgets them
  without destroying.
  **The Start menu is the exact opposite** and the two must not be confused: `AppList` icons come
  from `SHGetFileInfo`, which creates a handle for the caller, so they *are* owned and *are*
  destroyed in `AppList.Release()`. Copying either rule into the other file leaks handles or
  destroys someone else's icon.

---

## 7. Worth reconsidering

This was flagged before work started and the user chose to build from scratch anyway — a valid
call, but the tradeoff should stay visible:

[RetroBar](https://github.com/dremin/RetroBar) (MIT, C#/WPF) already implements a Win7/XP-style
taskbar **including a working tray**, and Open-Shell implements the Win7 Start menu. §4.3 and
§4.4 are the two hardest phases here, and both are exactly what those projects already solved
across years of edge cases. If the remaining effort starts to outweigh the appeal of owning the
code, orchestrating those two — install, configure, autostart, clean revert — is a days-not-months
path to the same end state.

---

## 8. File map

```
src/WinShell/
  WinShell.csproj        # no WPF; AOT/trim analyzers on; arm64 + x64
  app.manifest           # asInvoker, PerMonitorV2 DPI
  Program.cs             # entry point, --no-hide/--restore flags, message loop, restore-on-exit
  TaskbarWindow.cs       # window, appbar docking, GDI paint, layout, mouse, WndProc
  WindowList.cs          # WinEvent hooks, filtering, icon cache, activate
  NativeTaskbar.cs       # hide/reapply/restore Shell_TrayWnd (verified, see 4.1)
  StartMenu.cs           # thin forwarder to StartMenuWindow
  StartMenuWindow.cs     # Start menu window: layout, GDI paint, search, keyboard, launch
  AppList.cs             # AppsFolder enumeration via raw COM vtables; lazy icons; launch
  TrayHost.cs            # Shell_TrayWnd ownership, Shell_NotifyIcon parsing, icon list (--tray)
  ShellRegistration.cs   # --install-shell / --uninstall-shell / --shell-status (HKCU Winlogon)
  SystemIndicators.cs    # volume (Core Audio COM) / network / battery; --indicator-status
  VolumeFlyout.cs        # popup volume slider with drag + mute
  SettingsWindow.cs      # in-app settings: install/restore shell, disable indexing (--settings)
  SearchControl.cs       # --disable-search / --enable-search / --search-status
  SettingsCatalog.cs     # static ms-settings: page list for menu search
  FileSearch.cs          # bounded index-free filesystem search
  FileIcons.cs           # per-extension icon cache for file results
  ShellLaunch.cs         # launch routing: AUMID via activation manager, protocols via explorer
  DesktopWindow.cs       # the desktop: bottom-most window, paint, selection, drag, keys, drop
  DesktopIcons.cs        # Desktop folder enumeration, 48px icons, grid layout, position save
  Wallpaper.cs           # IDesktopWallpaper + GDI+ decode, fit/fill/tile/centre painting
  ShellContextMenu.cs    # real IContextMenu for items and background (--desktop-status --menus)
  DesktopFileOps.cs      # SHFileOperation: recycle, and move/copy for dropped files
  DesktopRename.cs       # F2 in-place rename via a subclassed EDIT control
  Native/
    Win32.cs             # LibraryImport P/Invoke surface
    Structs.cs           # blittable structs + constants
```

Related existing code in the sibling `WinOsUtils` app worth reading before §4.2:
`Services/StartMenuTweaker.cs` (the scan/apply pattern to follow), `Services/CopilotRemover.cs`,
`Services/SearchService.cs`, `Services/RemediationModels.cs`.

Per repo convention, `dist/` ships both x64 and arm64 executables and must be rebuilt after code
changes. `WinShell-x64.exe` and `WinShell-arm64.exe` are now published there (framework-dependent
single-file, same recipe as `WinOsUtils`). The published arm64 binary was smoke-tested: taskbar
docks, Start menu opens, search runs, Notepad launches.

Publish with the working directory on a **local** disk — a UNC cwd breaks SDK resolution:

```powershell
Push-Location $env:TEMP
foreach ($rid in 'win-x64','win-arm64') {
  $plat = if ($rid -eq 'win-x64') { 'x64' } else { 'arm64' }
  dotnet publish '\\Mac\winos-utils\src\WinShell\WinShell.csproj' -c Release -r $rid `
    --self-contained false -p:PublishSingleFile=true -p:Platform=$plat -o "$env:TEMP\winshell-publish\$rid"
}
Pop-Location
Copy-Item "$env:TEMP\winshell-publish\win-x64\WinShell.exe"   '\\Mac\winos-utils\dist\WinShell-x64.exe'   -Force
Copy-Item "$env:TEMP\winshell-publish\win-arm64\WinShell.exe" '\\Mac\winos-utils\dist\WinShell-arm64.exe' -Force
```

Note `dist/` is on the Mac share, so it is a UNC path and **`--install-shell` will refuse to run
from there** (§4.3). Copy to a local disk before installing as the shell.
