# IME Game Guard

IME Game Guard is a lightweight Windows tray utility that disables Chinese/English IME switching while a game has focus. It detaches the focused window's IME context, forces an English conversion mode, and restores the previous context when the game loses focus or the utility exits.

## Features

- Detects games by an online process database, without requiring users to enter game names.
- Ships with an offline copy of the database and refreshes it over HTTPS every 168 hours.
- Supports manual process names with `*` and `?` wildcards.
- Detects fullscreen and borderless windows automatically.
- Optionally blocks `Ctrl+Space`, `Win+Space`, and `Alt+Shift` while a game is protected.
- Runs in the system tray and restores the previous IME context on exit.

## Quick start

1. Extract the contents of the release archive.
2. Run `ImeGameGuard.exe`. Keep it running in the tray while you play.
3. Edit `config.json` only when you need to add a game that is missing from the database or change detection behavior.

The default configuration recognizes fullscreen and borderless games that cover at least 90% of a monitor. Set `MatchFullscreenWindows` to `false` if you want process-list matching only.

## Online game list

The default source is [GameProcessesDB](https://github.com/nino-exe/GameProcessesDB). Its JSON URL is stored in `OnlineGameListUrl`. This release includes an offline copy with 2,787 entries at `gameprocessesdb.json`; the application checks for updates every 168 hours and keeps using the local cache when the network is unavailable.

Only the `processName` field is read. Downloaded data is parsed as JSON and never executed. Set `OnlineGameListEnabled` to `false` to disable network updates.

If a game is missing, add its executable name to `GameProcesses`, for example:

```json
"GameProcesses": [
  "cs2.exe",
  "MyGame-Win64-Shipping.exe",
  "my-launcher-?.exe"
]
```

## Configuration

| Setting | Description |
| --- | --- |
| `Enabled` | Enables or pauses protection. |
| `PollIntervalMs` | Foreground window polling interval. |
| `MatchFullscreenWindows` | Detects windows that cover most of a monitor. |
| `MinimumFullscreenCoverage` | Coverage threshold from `0.5` to `1.0`. |
| `DisableImeContext` | Detaches the IME context while a game is focused. |
| `BlockImeHotkeys` | Blocks common IME switching shortcuts. |
| `OnlineGameListEnabled` | Enables online list updates. |
| `OnlineGameListRefreshHours` | Minimum time between updates. |
| `GameProcesses` | Additional process names or wildcard patterns. |
| `ExcludeProcesses` | Process names that should never be treated as games. |

If a game uses `Ctrl+Space`, `Win+Space`, or `Alt+Shift` as an action key, set `BlockImeHotkeys` to `false`; IME context detachment remains enabled.

## Build and publish

Requires the .NET 8 SDK on Windows. Run:

```powershell
.\publish.ps1
```

The script creates a self-contained `win-x64` single-file build in `publish\\`, copies `config.json` and `gameprocessesdb.json`, and produces `publish\\ImeGameGuard.exe`.

You can also build manually:

```powershell
dotnet publish .\ImeGameGuard\ImeGameGuard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
Copy-Item .\ImeGameGuard\config.json .\publish\config.json -Force
Copy-Item .\ImeGameGuard\gameprocessesdb.json .\publish\gameprocessesdb.json -Force
```

The utility normally does not require administrator rights. If a game runs elevated, run IME Game Guard elevated as well so Windows permits cross-process window access.

## Release

The release archive contains the self-contained executable, configuration, and offline process database. No .NET runtime installation is required.
