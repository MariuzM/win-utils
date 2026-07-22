# WinUtils

Windows 11 tweaking utilities, written in C# / .NET 9.

Four projects live here:

| Project         | What it is                                                                                                  |
| --------------- | ----------------------------------------------------------------------------------------------------------- |
| `src/WinUtils`  | WPF app with the debloat / privacy / performance tools. Runs against the .NET 9 Desktop Runtime.            |
| `src/WinBorder` | Tiny event-driven helper that removes Windows 11 DWM window borders. Published with Native AOT.             |
| `src/WinSnip`   | Tray screenshot tool — full screen, region, or click a window. Saves straight to the Desktop.               |
| `src/WinView`   | Minimal image viewer — zoom, pan, arrow-key folder navigation. ~0.2 MB, no dependencies beyond the runtime. |

## Download

Prebuilt binaries are in [`dist/`](dist), one folder per app and architecture — `winutils-x64` /
`winutils-arm64`, `winsnip-x64` / `winsnip-arm64` and `winview-x64` / `winview-arm64`.

- **WinUtils** — download the whole folder; the exe needs the files beside it, and the
  [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) installed.
- **WinSnip** — a single self-contained exe. Nothing to install.
- **WinView** — a single exe; needs the .NET 9 Desktop Runtime.

## WinUtils

![WinUtils Debloat screen](assets/screen.png)

Six pages, each a list of toggles that report what they changed:

- **System** — passwordless sign-in (auto-login)
- **Debloat** — remove OneDrive, Copilot, Edge leftovers
- **Privacy** — telemetry, Bing web search in Start
- **Performance** — disable background services this machine doesn't need
- **Apps** — app inventory, browser management, Claude Code installer
- **Personalization** — dark mode, Start menu, window borders and shell tweaks

The window-border toggle starts `WinBorder.exe` in the current desktop session and registers it for
future sign-ins. The helper has no tray icon and does not poll: it blocks in the Windows message
loop until a window is shown or activation changes, and keeps borders removed from both active and
inactive windows. Turning the toggle off stops the helper and restores the default DWM border color.

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

Right-click the tray icon and enable **Include window shadow** to capture the composed Windows
shadow and a small surrounding desktop margin with window captures.

`Esc` or right-click cancels. Capture uses Windows.Graphics.Capture, so hardware-accelerated windows
(Chrome, Electron, games) come out correctly rather than black.

## WinView

An image viewer without the weight of Photos. Opens with a path argument, a dropped file, or from
Explorer's **Open with** menu after running `WinView.exe --register` (`--unregister` reverses it).

- Scroll to zoom at the cursor, drag to pan
- `←` `→` step through the folder in Explorer's order
- `0` fit to window, `1` actual pixels, double-click toggles, `Esc` closes

JPEGs are auto-rotated from their EXIF orientation tag. Decoding is GDI+, so PNG, JPEG, GIF, BMP,
TIFF and ICO work — HEIC, WebP and AVIF do not.

Registration only adds WinView to the "Open with" list; it deliberately does not seize any file type
as the system default.

## Build

Requires Windows 11 (x64 or arm64) and the .NET 9 SDK.

```powershell
dotnet publish src\WinUtils\WinUtils.csproj -c Release -r win-arm64 -p:Platform=arm64 --self-contained false -p:PublishSingleFile=true
```

Swap `win-arm64` / `arm64` for `win-x64` / `x64` on Intel/AMD machines. WinUtils requests
Administrator via its `app.manifest`, so run Visual Studio elevated if you want to `F5` debug;
WinSnip runs as the invoking user.

Publishing `WinBorder.exe` with Native AOT requires Windows. When cross-publishing WinUtils from
macOS or Linux, add `-p:WinBorderNativeAot=false`; the helper then uses the .NET 9 runtime already
required by WinUtils.

Adding `-p:EnableWindowsTargeting=true` lets the managed projects build from macOS or Linux, WPF
included.

Formatting is handled by [CSharpier](https://csharpier.com):

```powershell
dotnet tool restore
dotnet csharpier format .
```

## Caveats

These tools change system settings, remove inbox apps and write to `HKLM`. Read what a toggle does
before flipping it, and have a restore point. No warranty.
