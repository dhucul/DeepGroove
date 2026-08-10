# WaveLab — project notes

WaveLab-style audio editor for Windows. C# / WPF / .NET 10 (`net10.0-windows`), single project `src/WaveLab`.

## Decisions

- C# over C++: WPF is .NET-native; MVVM + NAudio ecosystem gives the more sophisticated app.
- Audio stored deinterleaved 32-bit float in `AudioDocument`; 16/24-bit sources load losslessly into float.
- Custom RIFF and AIFF codecs (`WavCodec` / `AiffCodec`) provide exact 16/24/32-bit control + TPDF dither on 16-bit export. AIFF-C import accepts uncompressed `NONE`/`twos`/`sowt`/`raw `/`in24`/`in32` PCM and `fl32`/`fl64`; compressed media import uses MediaFoundationReader.
- All edits go through `AudioDocument.ReplaceRange` (region splice) → undo/redo + change events for free.
- Waveform drawing reads `PeakStore` (min/max/RMS pyramid, base bin 256, ×4 per level); rebuilt synchronously on every edit.
- Playback: NAudio `WasapiOut` shared mode ← `MasterSection` (StudioEq → Limiter → meters/LUFS/FFT ring) ← `DocumentProvider`.
- Loudness: BS.1770 K-weighting (highshelf 1681.97 Hz +4 dB, highpass 38.13 Hz), 100 ms sub-blocks, gated integrated; true peak = 4× Catmull-Rom oversample.
- Theme: dark studio, teal accent #3FD6C2, Inter + JetBrains Mono embedded from `Assets/Fonts` (referenced as `#Inter` / `#JetBrains Mono`).
- Custom window chrome (`WindowChrome`, CaptionHeight 40); menus/combos/sliders fully re-templated in `Themes/Theme.xaml`.
- David wants to SEE UI mockups as pictures before UI changes are coded — render HTML mockups (docs/design/) and get approval first.

## v2 additions

- Effects are `IAudioEffect` implementations (`Audio/Effects/`), registered in `EffectFactory`; `MasterSection` holds an ordered chain, meters, and stereo ring buffers. Offline processing goes through `MasterSection.ProcessOffline` (latency-compensated). Rack UI is auto-generated from each effect's `Params` descriptors; per-effect reset comes from `EffectBase.RestoreDefaults`.
- Chain presets are JSON in `%AppData%\WaveLab\Presets` (`EffectFactory` capture/instantiate); factory presets are created on first run only.
- Settings (`Util/AppSettings`) persist to `%AppData%\WaveLab\settings.json`; autosave (`Util/AutosaveService`) snapshots dirty docs by array-reference capture (splices replace arrays, so refs are point-in-time safe) and recovers via a manifest.
- Markers/regions live on `DocumentViewModel`, persist to `<file>.wlmeta.json` sidecars, and are re-anchored through splices in `OnDocChanged`.
- Restoration/stretch/pitch/SRC DSP is in `Audio/Dsp/` (`Restoration`, `TimeStretch`, `Resampler`, `PitchDetect`, `TempoDetect`); tool orchestration (dialogs → `RunRangeTool` → undoable `ReplaceRange`) is in `MainWindow.xaml.cs`.
- Generic dialogs: `ParamDialog` (combo + sliders), `InfoDialog`, `TextPromptDialog` — reuse these instead of new one-off windows.
- Export uses `AudioExporter` (custom WAV codec or MediaFoundationEncoder; FLAC only if the MFT exists — check `FlacAvailable`).

## Threading model (responsiveness)

- File open/save, peak rebuilds, offline processing and exports all run on Task.Run; the UI thread never does O(file) work. Background code always operates on **snapshot channel-array refs** captured up front (splices replace arrays, never mutate them) — follow this pattern for any new heavy op.
- `PeakStore.Rebuild` builds into fresh lists and swaps atomically; `DocumentViewModel.ScheduleRebuild` coalesces edit bursts. Waveform may show stale peaks for a beat after an edit — by design.
- `WaveformView`/`OverviewBar` cache their peak geometries keyed on (view, size, ampZoom, peaksVersion); playhead/cursor/selection/markers are overlays. Don't put per-frame-changing state back into the geometry key.
- Saves are version-checked: `MarkSaved` only fires if `EditVersion` didn't change while writing.

## Gotchas

- Absolutely-positioned canvases in the HTML mockups need explicit width/height 100% (replaced elements ignore inset stretching).
- `EnableWindowsTargeting` is set so the project also builds on non-Windows CI.
- WPF menu/combo templates are custom — new menu items inherit styling automatically; keep `InputGestureText` in sync with `Window.InputBindings` in MainWindow.xaml.
