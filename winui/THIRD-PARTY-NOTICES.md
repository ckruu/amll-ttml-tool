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

## SoundTouch (playback speed)

- Chosen: **SoundTouch.Net 2.3.2** (complete managed C# port of SoundTouch
  2.3.2, tempo/pitch/rate engine), because the CPU benchmark below shows it
  is more than fast enough while needing no native binary and no arm64
  build at all. Native SoundTouch 2.3.3 was also built cleanly from the
  official tarball as a fallback (kept out of the repo).
- NuGet: https://www.nuget.org/packages/SoundTouch.Net/2.3.2
  (`dotnet add package SoundTouch.Net --version 2.3.2`)
- SHA512 of the package (from the local NuGet cache metadata):
  `8VqYrvz0NsF/xTN0KI4EHX+0SFvNHrO1zYiplzFHfC2v1t4v+o+m7x0gcpMKMgV5BN5Qfm8GGV2oLs7MkC6h8Q==`
- Upstream: https://github.com/owoudenberg/soundtouch.net (port of
  https://codeberg.org/soundtouch/soundtouch, official release
  https://www.surina.net/soundtouch/soundtouch-2.3.3.tar.gz)
- License: LGPL-2.1-or-later (both the port and the original).
- Linking: managed assembly referenced by the app (no native DLL shipped).
- Benchmark, 60 s of 48 kHz stereo float on this machine (process ms for
  the full output; output length exact and 440 Hz preserved in both):
  - tempo 0.5x: native 212 ms (0.18% realtime), managed 1091 ms (0.91%)
  - tempo 1.0x: native 132 ms (0.46% realtime), managed 640 ms (2.22%)
  - tempo 2.0x: native 117 ms (0.39% realtime), managed 582 ms (1.94%)
  Native is ~5x faster, but both are under ~2.5% realtime, so the managed
  port wins on zero-native-binary grounds. Caveats: the port tracks
  SoundTouch 2.3.2 (one minor behind 2.3.3), handles float mono/stereo
  only (exactly what the playback graph feeds it), and was last updated
  2021 (the WSOLA algorithm itself is decades stable; version pinned).
- Native fallback, if ever needed: official tarball SHA256
  `43B23DFAC2F64A3AFF55D64BE096FFC7B73842C3F5665CAFF44975633A975A99`,
  clean MSVC x64 shared build verified (`SoundTouch.dll` + C API wrapper
  compile; arm64 build was not attempted since the managed port won).
