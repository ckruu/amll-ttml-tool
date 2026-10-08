# AMLL TTML Tool — native WinUI 3 port (Windows 11)

C# + Windows App SDK. No WebView. Tracks the web app (`src/`) feature by feature.

## Projects

- `AmllTtmlTool.WinUI` — app shell (Mica, extended title bar, `NavigationView`: Edit / Sync / Preview). Targets `net8.0-windows10.0.19041.0`, unpackaged (`WindowsPackageType=None`) during development; MSIX via Publish profiles in `Properties/PublishProfiles`.
- `AmllTtmlTool.Core` — plain `net8.0` library. Ports of the web app's pure logic, starting with the TTML core:
  - `Models/LyricModels.cs` — from `src/types/ttml.ts`
  - `AppleTtml.cs` — from `src/modules/ttml-processor/appleTtml.ts`
  - `LegacyTtml.cs` — from `src/modules/ttml-processor/legacyCompat.ts`
  - `TtmlNormalize.cs` — pure parts of `src/modules/ttml-processor/index.ts`
- `AmllTtmlTool.Core.Tests` — xUnit coverage for the above.

Full TTML parse/generate (currently Rust/WASM in `src/modules/ttml-processor/wasm`) is a later milestone; `Core` will gain a `System.Xml.Linq` implementation with the same `Result<T>` contract.

## Build

```powershell
dotnet build winui/AmllTtmlTool.sln
dotnet test winui/AmllTtmlTool.Core.Tests
```

## Roadmap

1. Phase 0 (here): skeleton + TTML core + tests.
2. Phase 1 (MVP): lyrics import, edit, sync/timing, preview (Win2D karaoke renderer), audio via MediaPlayer + FFmpegInteropX.
3. Stability gate, then Phase 2: spectrogram, BPM, segmentation/ruby, project autosave (SQLite), settings, LRCLIB, updater via AppInstaller.
