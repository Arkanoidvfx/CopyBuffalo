# Development

CopyBuffalo is a Windows x64 WPF app targeting .NET 10. Application code and XAML live in the repository root. `tools/ocr/` builds the Rust OCR worker; `packaging/` builds and checks installers.

The public product and installer names are CopyBuffalo. Keep the internal `ArkCopyBuffer` project/assembly/executable, installer identity and mutexes, registry/data paths, clipboard formats and encryption identifiers stable for compatibility. Version 0.2.4 renames the public installer assets to `CopyBuffalo-Setup.exe` and `CopyBuffalo-Full-Setup.exe.part1` / `.part2`; older clients require one manual upgrade because their updater expects the previous asset name.

CopyBuffalo's original code uses the MIT license in the root `LICENSE`, which is included in published app files, installers and prepared GitHub assets. Third-party components keep their own licenses. FFmpeg is a separate GPLv3 executable; public redistributions must also provide its complete corresponding source and enabled-library sources as required by the GPL, as described in `packaging/THIRD-PARTY-NOTICES.txt`.

## Build

Use Windows and a .NET 10 SDK. A normal development build does not require the OCR bundle:

```powershell
dotnet build -c Debug
```

For a distributable build, first create the OCR bundle, then publish:

```powershell
pwsh -NoProfile -File tools/ocr/Build-Ocr.ps1
dotnet publish -c Release
```

The OCR build downloads pinned sources and models and requires the Rust toolchain and Windows C++ build tools. See [OCR](ocr.md). Release publishing produces a self-contained x64 build in `publish/`; no system .NET runtime is needed on the destination computer. Publishing refuses an incomplete OCR bundle.

## Checks

Rapid clipboard capture uses a single `OleGetClipboard` attempt, detached format snapshots, and asynchronous retries at 5 ms while the sequence stays current. Avoid WPF's blocking `Clipboard.GetDataObject` retry loop or holding `OpenClipboard` across delayed OLE rendering. All supported item kinds use one processing queue. Synthetic regression checks cover mixed bursts, common-prefix URLs, formatting, sequence changes, privacy exclusions, and encrypted save/reload.

```powershell
.\publish\ArkCopyBuffer.exe --selftest
.\publish\ArkCopyBuffer.exe --selftest .tmp\checks-en en
.\publish\ArkCopyBuffer.exe --selftest .tmp\checks-ru ru
.\publish\ArkCopyBuffer.exe --localization-selftest .tmp\locale-en en
.\publish\ArkCopyBuffer.exe --localization-selftest .tmp\locale-ru ru
pwsh -NoProfile -File packaging/Test-Updater.ps1 -App .\publish\ArkCopyBuffer.exe
```

Self-tests write a log under `%LOCALAPPDATA%\ArkCopyBuffer\selftest.log` and use synthetic fixtures. Run them outside an active gaming session: some checks exercise native window lifecycle and rendering. Do not use real clipboard history as test data.

The optional directory and language arguments isolate self-test output and select the language before application resources load. Localization checks render the panel, settings, editor, and merge window offscreen and verify all six catalogs, placeholders and format specifiers, language selection, and synthetic history. They do not show windows or use the real clipboard.

After packaging, `packaging/Test-Distribution.ps1` checks the staged bundle without relying on system .NET or FFmpeg. Some distribution checks exercise local AI; check available GPU memory before running them alongside other GPU work.

## Installers

`packaging/Build-Distribution.ps1` requires .NET 10, Inno Setup 7, a local FFmpeg distribution, and the built OCR bundle. It uses cached tools under `.cache/distribution-tools/` or explicit `-Sdk`, `-Iscc`, and `-FfmpegDir` paths. It does not download these packaging inputs.

```powershell
pwsh -NoProfile -File packaging/Build-Distribution.ps1
pwsh -NoProfile -File packaging/Build-Distribution.ps1 -WithAI
```

The full build also requires the model (`-Model`), official llama.cpp CPU/CUDA packages under `.cache/distribution-tools/llama-cpu` and `llama-cuda`, and Microsoft VC runtime DLLs (`-VcRuntimeDir`). Keep component licenses alongside these cached inputs. Output goes to `dist/`; staging goes to `.tmp/distribution/`.

For GitHub, `packaging/Prepare-GitHubRelease.ps1` splits the full installer into 1.9 GB parts, reads them back to verify SHA-256, and writes `dist/github/`. `CopyBuffalo-Full-Setup.cmd` downloads missing parts from its pinned release, verifies each part and the assembled installer, then opens it using Windows PowerShell 5.1. The CMD is the single public AI setup link; existing verified parts also work offline. Set `ARKCOPYBUFFER_VERIFY_ONLY=1` to verify without launching the installer.

## Release and update contract

1. Increase `<Version>` in `ArkCopyBuffer.csproj` and update the installation guides.
2. Build, run the relevant checks, and prepare both distributions.
3. Publish a stable `v<Version>` release in `Arkanoidvfx/CopyBuffalo`. Upload **CopyBuffalo-Setup.exe** with that exact name, plus the optional full-package parts, launcher, guides, manifest, and checksums.
4. Confirm the public GitHub API exposes the asset's size and `sha256` digest and that the download works without authentication.

`AppUpdater.cs` accepts stable releases from this repository only. It verifies the core installer's size and GitHub SHA-256 on download and again before installation. The app checks at startup and every 15 minutes; automatic updates default to on. Installation waits for 30 seconds with the panel hidden and no pending capture, editor, selection, drag, or processing work. The app saves history before exiting, then a hidden helper runs Inno Setup and restarts it.

`/UPDATE=1` preserves shortcuts, autostart, user data, and the optional `ai/` directory. A failed history save keeps the app open. An installer failure records `%LOCALAPPDATA%\ArkCopyBuffer\updates\install-error.txt` and stops automatic retries until manual action. Trust comes from GitHub HTTPS and its digest; there is no independent release-key signature. Version 0.1.0 needs one manual installation to gain the updater.

Validation for v0.2.0 included 489 synthetic checks, distribution dependency checks, helper failure/tamper checks, and an isolated end-to-end download/install/restart with exact history/settings comparisons. That does not cover every physical PC or interrupted power supply.

## UI conventions

English is the default. Settings → View offers English, Russian, Simplified Chinese, Traditional Chinese, Japanese, and Hindi; the saved `Language` preference applies on restart. `Strings.resx` is English and `Strings.ru.resx` is Russian. `UiText.T` resolves a source string; `UiText.F` formats a translated `FormattableString`; XAML uses `Loc`. Keys are `S` plus the first 16 uppercase SHA-256 hex characters of the source after normalizing line endings to LF. Keep every resource catalog and its format arguments in sync. Bundled Noto CJK and Plex Devanagari faces provide script coverage without Windows font-pack installation. User content, stored group names, OCR character tables, and model examples are not UI translations.

Use the shared `TextFont` / `MonoFont` resources; see [Typography](typography.md). Side menus and previews use `SidePlacements` to respect monitor work areas and DPI. Keep window activation and clipboard writes out of background work.

Generated bundles, caches, local agent notes, and research artifacts are excluded from Git. Documentation screenshots are offscreen renders of real WPF controls with synthetic content; they do not read the user's history or capture their desktop.
