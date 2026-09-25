---
name: arkcopybuffer-project
description: Work on ArkCopyBuffer, the Windows .NET 9 WPF clipboard-history app in D:\Projects\ArkCopyBuffer; use for its code, bugs, performance, UI, tests, and local releases.
---

# ArkCopyBuffer

Work from the repository root. This is a Windows-only .NET 9 WPF clipboard-history app. `WinVHook.cs` handles Win+V; `ClipboardWatcher.cs` captures clipboard changes; `HistoryStore.cs` persists entries; `Paster.cs` restores and pastes them; `PanelWindow.xaml` / `.xaml.cs` render the panel; `App.xaml.cs` wires these parts together, owns the tray menu, and stores settings under `HKCU\Software\ArkCopyBuffer`.

- Keep clipboard capture and panel opening responsive. Check image dimensions before decoding; keep large file and encryption work off the UI thread. Preserve WPF dispatcher ownership and the foreground-target checks before simulated input.
- Preserve the current record-time display unless the user explicitly asks to change it.
- Runtime data lives in `%LOCALAPPDATA%\ArkCopyBuffer`: encrypted `history.dat`, encrypted `images\`, and `symbols.json`. Do not use real history as test data or remove it during cleanup. Diagnostic files there include `error.log`, optional `trace.log`, and `selftest.log`.
- `UnrealGraph.cs` parses Unreal material/Blueprint text and Unity **Shader Graph** copy data, draws graph previews, and opens the full viewer. Unity Visual Scripting is a separate clipboard mechanism and is not supported by this parser. Keep graph previews off the UI thread (`IsAsync=True` binding); freeze shared WPF brushes and the completed `DrawingImage` before crossing threads. The full viewer uses a `Canvas` so the image receives its full natural size before pan/zoom and viewport clipping.
- The panel has animation timings, hide-on-deactivate, image/graph hover enlargement (default delay 1 s, size 3×), GIF playback on hover, and a tray restart command. Settings are persisted in the registry by `App.xaml.cs`.
- For code changes, build with `dotnet build ArkCopyBuffer.csproj -c Release -p:PublishReadyToRun=false --nologo`. Normally run the built executable with `--selftest` (exit code 0 means success; it uses separate temporary history and writes `%LOCALAPPDATA%\ArkCopyBuffer\selftest.log`). Respect a user's explicit request to skip tests.
- To update the running release, publish to a repository-local `.tmp` staging folder; never publish directly over the running executable. Stop only the `ArkCopyBuffer` process whose path is `publish\ArkCopyBuffer.exe`, copy staged files into `publish\`, then start that executable with a hidden window. Normally self-test the staged executable first, unless the user has asked to skip tests. Default Release publish enables ReadyToRun.
