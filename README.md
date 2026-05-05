# codex-usage

Windows HUD for Codex usage limits.

`codex-usage` is a lightweight WPF overlay that reads Codex account rate limits through the local `codex app-server` API and displays the remaining 5-hour and 1-week usage percentages near the Codex pet. It does not patch the Codex desktop UI.

## Features

- Separate Windows HUD, outside the Codex app UI.
- Uses only `account/rateLimits/read` through `codex app-server`.
- Follows the Codex pet overlay when it can identify the pet window.
- Falls back to the bottom-right work area when the pet window is unavailable.
- Compact two-row card:
  - 5-hour window with reset time and remaining percent.
  - 1-week window with reset date and remaining percent.
- Green/orange/red percent and bar colors:
  - 70% and above: green.
  - 30-69%: orange.
  - Below 30%: red.
- Double-click to collapse or expand.
- Light/dark theme follows the Windows system theme.
- Korean/English text follows the OS UI culture.
- Automatically resolves the `codex` command path.
- Framework-dependent .NET 8 Windows build.

## Requirements

- Windows 10 or later.
- .NET 8 Desktop Runtime.
- Codex CLI available as `codex` or `codex.cmd`.
- Codex account signed in through the same environment used by `codex app-server`.

## Build

```powershell
dotnet publish .\codexpet\codexpet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\codexpet\publish
```

The executable is created at:

```text
codexpet\publish\codex-usage.exe
```

## Run

```powershell
.\codexpet\publish\codex-usage.exe
```

## Notes

- The app starts a local `codex app-server` process with `--session-source codex-usage`.
- It does not consume model tokens or task limits.
- If the Codex pet is disabled, hidden, or not identifiable, the HUD stays near the bottom-right of the screen instead of following other Codex menus or popups.
- The source folder is still named `codexpet` for continuity, but the built executable is `codex-usage.exe`.
