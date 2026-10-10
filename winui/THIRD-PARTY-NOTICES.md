# Third-party notices

This file records every third-party component shipped with the app:
exact version and source, integrity hash, license, and how it is linked.
Keep it current: any new binary or package must add an entry here first.

## FFmpeg (decode + probe via CLI)

- Version: n8.1.3-14-g330caae0c1 (BtbN autobuild 2026-10-08), shared builds
- Source: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-08-13-05
- Files: `ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip`
  and `ffmpeg-n8.1.3-14-g330caae0c1-winarm64-lgpl-shared-8.1.zip`
- SHA256 (locally computed with `Get-FileHash`; no upstream `.sha256`
  sidecar is published for these assets, but both sizes match the GitHub
  release asset metadata exactly — 80993033 / 55863874 bytes):
  - win64: `1AF9562D002B8E2AB66817616E8A9211E77AB4B0FEF58C5C345F467DC0213729`
  - winarm64: `3BF84E187CC00482AD5D0892C17C529DCB0CADDBC0DC63984D09DCEAFF5E8793`
- Shipped files per arch (`winui/libs/ffmpeg/x64`, `winui/libs/ffmpeg/arm64`,
  copied next to the app binary at build time): `ffmpeg.exe`, `ffprobe.exe`,
  `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`,
  `swscale-9.dll`, `avdevice-62.dll`, `avfilter-11.dll`
- Linking: dynamic. The app shells out to `ffmpeg.exe`/`ffprobe.exe` over
  stdin/stdout/stderr; no FFmpeg code is linked into the app and no
  P/Invoke ABI is used.
- License: GNU Lesser General Public License v3. Verified on the shipped
  binaries: `ffmpeg -version` configuration contains neither `--enable-gpl`
  nor `--enable-nonfree` (with `--enable-version3`, i.e. LGPLv3).
  Full text: `winui/libs/ffmpeg/LGPL-3.0.txt` (verbatim from the upstream zip).

## SoundTouch (item 3: playback speed)

- Status: evaluation pending (native SoundTouch vs maintained managed port,
  chosen by CPU benchmark). This section gets the version, source, SHA256,
  and license entry before any binary or package is added.
