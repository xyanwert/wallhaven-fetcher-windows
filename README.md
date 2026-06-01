# Wallhaven Fetcher — Windows

Native Windows tray app for daily wallpaper sync from
[wallhaven.cc](https://wallhaven.cc) and [konachan.com](https://konachan.com).
Companion to the macOS [wallhaven-fetcher-bar](https://github.com/xyanwert/wallhaven-fetcher-bar).

Built in **C# / .NET 8 / WinForms**, packaged as a self-contained single-file
executable, distributed via an Inno Setup installer with **auto-update**
prompts driven by GitHub Releases.

## Install (end user)

1. Download the latest `WallhavenFetcherSetup-X.Y.Z.exe` from
   [Releases](https://github.com/xyanwert/wallhaven-fetcher-windows/releases/latest).
2. Run it. Tick "Start at login" if you want auto-start. Finish.
3. (Recommended) Drop your wallhaven API key at
   `%APPDATA%\WallhavenFetcher\api_key` (single line, no quotes).
4. In **Windows Settings → Personalization → Background**:
   - Choose **Slideshow**
   - Browse to `%USERPROFILE%\Pictures\Wallhaven\`
   - Set rotation interval (1 minute → 1 day)
   - Choose fit mode (Fill, recommended)
5. Right-click the tray icon → **Sync now** to do a first sync. ~30 s later
   your folder is populated and Windows starts rotating.

## Updates

The app polls GitHub Releases on startup. When a newer release exists, the
tray menu shows **⬇ Update to vX.Y.Z…** and Windows shows a toast. Click it:
the new installer downloads + runs; existing settings are preserved.

## Feature parity with the macOS version

| Feature | Windows |
|---|---|
| Daily auto-sync | ✅ via in-app timer (6h default) |
| Multi-source (wallhaven + konachan) | ✅ |
| Save / Ban current wallpaper | 🟡 menu items present, current-wallpaper detection coming |
| Presets, preset-from-URL | 🟡 backend present, UI coming |
| Rollout / Bootstrap / Steady-state | ✅ |
| fit_to_target (crop or pad with blur) | ✅ via ImageSharp |
| `--import` (move files into folder) | 🟡 backend present, UI coming |
| Notifications | ✅ native Win10/11 toasts |
| Wallpaper folder rotation | ✅ via built-in Windows Slideshow |
| Auto-update | ✅ via GitHub Releases |

## Storage

| What | Where |
|---|---|
| Config & overrides | `%APPDATA%\WallhavenFetcher\config.json` |
| State (saved, banned, fingerprints) | `%APPDATA%\WallhavenFetcher\state.json` |
| API key | `%APPDATA%\WallhavenFetcher\api_key` |
| Sync log | `%APPDATA%\WallhavenFetcher\Logs\sync.log` |
| Wallpapers | `%USERPROFILE%\Pictures\Wallhaven\` (configurable) |

## Build from source

Requires **.NET 8 SDK** (any platform — cross-compiles to Windows from
macOS / Linux too).

```bash
git clone https://github.com/xyanwert/wallhaven-fetcher-windows
cd wallhaven-fetcher-windows

# Run directly (requires Windows + .NET 8 runtime)
dotnet run --project src/WallhavenFetcher

# Or publish a single-file release exe
dotnet publish src/WallhavenFetcher/WallhavenFetcher.csproj \
  -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:SelfContained=true \
  -o publish/

# Build the installer (requires Inno Setup 6+)
iscc /DVersion=0.1.0 installer/WallhavenFetcher.iss
# → installer/Output/WallhavenFetcherSetup-0.1.0.exe
```

## Releases

Push a tag matching `v*` (e.g. `git tag v0.2.0 && git push --tags`).
GitHub Actions builds the single-file exe, runs Inno Setup, creates a
release with the installer attached. Auto-update clients pick it up on
their next startup.

## License

Personal use. See LICENSE.
