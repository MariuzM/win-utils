# WinOS Utils

Windows 11 tweaking utilities, written in C# / .NET 9.

Two projects live here:

| Project          | What it is                                                                                                       |
| ---------------- | ---------------------------------------------------------------------------------------------------------------- |
| `src/WinOsUtils` | WPF app with the debloat / privacy / performance tools. Runs against the .NET 9 Desktop Runtime.                 |
| `src/WinShell`   | Work in progress. Raw Win32 (no WPF/WinForms) Windows 7-style taskbar and Start menu replacement. See `TODO.md`. |

## Download

Prebuilt binaries are in [`dist/`](dist) — `win-x64` for Intel/AMD, `win-arm64` for ARM machines.
Download the whole folder for your architecture and run `WinOsUtils.exe`; it needs the files next to
it. Requires the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).

## WinOsUtils

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

## Build

Requires Windows 11 (x64 or arm64) and the .NET 9 SDK.

```powershell
dotnet publish src\WinOsUtils\WinOsUtils.csproj -c Release -r win-arm64 -p:Platform=arm64 --self-contained false -p:PublishSingleFile=true
```

Swap `win-arm64` / `arm64` for `win-x64` / `x64` on Intel/AMD machines. Both apps request
Administrator via their `app.manifest`, so run Visual Studio elevated if you want to `F5` debug.

Formatting is handled by [CSharpier](https://csharpier.com):

```powershell
dotnet tool restore
dotnet csharpier format .
```

## Caveats

These tools change system settings, remove inbox apps and write to `HKLM`. Read what a toggle does
before flipping it, and have a restore point. No warranty.
