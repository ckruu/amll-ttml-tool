# Bundled FFmpeg shared libraries (decode-only)

- Source: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-08-13-05
- Files: `ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip`
  and `ffmpeg-n8.1.3-14-g330caae0c1-winarm64-lgpl-shared-8.1.zip`
- SHA256 (of the zips, locally computed):
  - win64: `1AF9562D002B8E2AB66817616E8A9211E77AB4B0FEF58C5C345F467DC0213729`
  - winarm64: `3BF84E187CC00482AD5D0892C17C529DCB0CADDBC0DC63984D09DCEAFF5E8793`
  (sizes match the GitHub release asset metadata exactly: 80993033 / 55863874 bytes;
  no upstream .sha256 sidecar is published for these assets)
- Kept files per arch: `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`,
  `swresample-6.dll`, `swscale-9.dll`, `avdevice-62.dll`, `avfilter-11.dll`,
  plus `ffmpeg.exe` and `ffprobe.exe` (decode/probe via CLI, no P/Invoke ABI risk).
- License: LGPLv3 (`ffmpeg -version` configuration contains neither
  `--enable-gpl` nor `--enable-nonfree`). Full text in `winui/THIRD-PARTY-NOTICES.md`.
- All binaries (`dl/`, `x64/`, `arm64/`, ~900MB total) are gitignored: too
  large to push. Download per the source links above to enable WinUI audio
  decode; without them the app reports "bundled FFmpeg is missing".
