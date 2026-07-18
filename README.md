# WinUtils

Windows 11 tweaking utilities, written in C# / .NET 9.

Three projects live here:

| Project        | What it is                                                                                                       |
| -------------- | ---------------------------------------------------------------------------------------------------------------- |
| `src/WinUtils` | WPF app with the debloat / privacy / performance tools. Runs against the .NET 9 Desktop Runtime.                 |
| `src/WinSnip`  | Tray screenshot tool — full screen, region, or click a window. Saves straight to the Desktop.                    |
| `src/WinShell` | Work in progress. Raw Win32 (no WPF/WinForms) Windows 7-style taskbar and Start menu replacement. See `TODO.md`. |

## Download

Prebuilt binaries are in [`dist/`](dist), one folder per app and architecture — `winutils-x64` /
`winutils-arm64` and `winsnip-x64` / `winsnip-arm64`.

- **WinUtils** — download the whole folder; the exe needs the files beside it, and the
  [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed.
- **WinSnip** — a single self-contained exe. Nothing to install.

## WinUtils

Six pages, each a list of toggles that report what they changed:

- **System** — passwordless sign-in (auto-login)
- **Debloat** — remove OneDrive, Copilot, Edge leftovers
- **Privacy** — telemetry, Bing web search in Start
- **Performance** — disable background services this machine doesn't need
- **Apps** — app inventory, browser management, Claude Code installer
- **Personalization** — Start menu and shell tweaks

### Auto-login

Configured the same way Sysinternals Autologon and `netplwiz` do it: `AutoAdminLogon`,
`DefaultUserName` and `DefaultDomainName` under
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon`, with the password stored as an
encrypted LSA secret (`LsaStorePrivateData`) — never as a plaintext registry value.

> **Security:** auto sign-in means anyone with physical access boots straight into the account, and
> a local administrator can still recover the stored password. Only enable it on a device you
> physically trust.

## WinSnip

Runs in the tray. Captures go straight to the Desktop as `Screenshot 2026-07-18 at 20.31.15.png` —
no editor, no save dialog.

- `Ctrl+Shift+1` — the monitor under the cursor
- `Ctrl+Shift+2` — drag a region
- `Ctrl+Shift+3` — hover a window, click to capture it

`Esc` or right-click cancels. Capture uses Windows.Graphics.Capture, so hardware-accelerated windows
(Chrome, Electron, games) come out correctly rather than black.

## Build

Requires Windows 11 (x64 or arm64) and the .NET 9 SDK.

```powershell
dotnet publish src\WinUtils\WinUtils.csproj -c Release -r win-arm64 -p:Platform=arm64 --self-contained false -p:PublishSingleFile=true
```

Swap `win-arm64` / `arm64` for `win-x64` / `x64` on Intel/AMD machines. WinUtils requests
Administrator via its `app.manifest`, so run Visual Studio elevated if you want to `F5` debug;
WinSnip and WinShell run as the invoking user.

Adding `-p:EnableWindowsTargeting=true` lets all three build from macOS or Linux, WPF included.

Formatting is handled by [CSharpier](https://csharpier.com):

```powershell
dotnet tool restore
dotnet csharpier format .
```

## Caveats

These tools change system settings, remove inbox apps and write to `HKLM`. Read what a toggle does
before flipping it, and have a restore point. No warranty.
