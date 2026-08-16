# WaveLab — project notes

WaveLab-style audio editor for Windows. C# / WPF / .NET 10 (`net10.0-windows`), single project `src/WaveLab`.

## Decisions

- C# over C++: WPF is .NET-native; MVVM + NAudio ecosystem gives the more sophisticated app.
- Audio stored deinterleaved 32-bit float in `AudioDocument`; 16/24-bit sources load losslessly into float.
- Custom RIFF and AIFF codecs (`WavCodec` / `AiffCodec`) provide exact 16/24/32-bit control + TPDF dither on 16-bit export. AIFF-C import accepts uncompressed `NONE`/`twos`/`sowt`/`raw `/`in24`/`in32` PCM and `fl32`/`fl64`; compressed media import uses MediaFoundationReader.
- All edits go through `AudioDocument.ReplaceRange` (region splice) → undo/redo + change events for free.
- Waveform drawing reads `PeakStore` (min/max/RMS pyramid, base bin 64, ×4 per level); rebuilt synchronously on every edit. The base bin also sets where `Query` stops falling back to a per-sample scan (below `BaseBin * 2` samples/pixel) — keep it small enough that intermediate zoom levels stay on the pyramid.
- Playback: NAudio `WasapiOut` shared mode ← `MasterSection` (StudioEq → Limiter → meters/LUFS/FFT ring) ← `DocumentProvider`.
- Loudness: BS.1770 K-weighting (highshelf 1681.97 Hz +4 dB, highpass 38.13 Hz), 100 ms sub-blocks, gated integrated; true peak is the BS.1770 Annex 2 order-48 four-phase polyphase FIR. `FlushTruePeak` rings out the final taps and is non-destructive (it works on a clone), but it must only be called when the measurement is finished — on a live path it interpolates the current tail against zeros and reads high.
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
- The Recording Level Assistant is `Audio/Dsp/RecordingLevelAnalyzer.cs` (streaming, fed from `RecordingEngine.OnData`) plus `RecordViewModel` and the card in `RecordDialog.xaml`. Everything is summarized from 100 ms blocks in an append-only `BlockHistory`; `Publish()` hands a snapshot build a stable `ArraySegment` without copying, and `BuildSnapshot` works on pooled index sets and one scratch buffer rather than LINQ over copied summaries — **do not reintroduce `List<BlockSummary>` partitions or `OrderBy` percentiles**, a whole-side scan holds 72 000 88-byte blocks. The live cache is invalidated in `CompleteBlock`, not per capture packet, so the readout refreshes at 10 Hz instead of ~30 Hz on the dispatcher.
- The recommendation targets `TargetCeilingDb` — a continuous value, not a preset list. `AppSettings.NormalizeTargetCeilingDb` clamps to the analyzer's own `[-24, -1]` bounds and snaps to 0.5 dB, so an out-of-range or hand-edited value is corrected rather than discarded; only a non-finite one falls back to the −6 default (the analyzer's own default is −3, for callers that set nothing). The slider reaches down to `AdjustableRecordingTargetCeilingFloorDb` (−12) and extends its floor to meet a deeper stored value rather than quietly raising it. `Views/Controls/TargetCeilingScale` is shared by the Settings page and the level card's popup — landmark marks are **drawn**, because setting `Slider.Ticks` would make `IsSnapToTickEnabled` snap to those three values and undo the continuous ceiling. Setting the ceiling on the card re-derives a running scan for free (the analyzer's setter drops its cached snapshot) but discards a *finished* check, whose blocks are gone — the analyzer is reset on stop. Reserve = `max(time schedule, tail + novelty) + crest premium`, capped at 9 dB, where *tail* is how far the loudest block sits above the p99 the recommendation is built on and *novelty* is how much the running maximum rose after the scan's halfway point. Both evidence terms are exactly zero for steady material, which is why the original schedule tests still pass unchanged — keep that property when tuning.
- Narrow-artifact rejection (`narrowArtifactOwnsAbsolutePeak`) deliberately lets a stylus click touch full scale without lowering the whole side, but it is **overridden** by `hasSustainedRailHits`: any single block clipping over the trimmed-peak fraction of its samples is real overload. Without that override a genuinely clipping input reported "Good".
- Block programme/quiet classification is shared with `RunOutDetector` via `Audio/Dsp/ProgramBlockClassifier.cs`; only the minimum-peak floor differs per caller.
- Recording auto-start/auto-stop are mirror images and both live on the capture thread inside `RecordingEngine.OnData`: `NeedleDropDetector` promotes a monitor stream into a take, `RunOutDetector` ends one. Each raises an event (`NeedleDropTriggered` / `AutoStopTriggered`); `RecordViewModel` marshals to the dispatcher and drives the ordinary start/stop path, so the engine never stops itself. Auto-stop trims by *lowering the snapshot's sample count* in `StopAndSnapshotAsync` — `BuildDocument` stops at that frame count, so trailing blocks need no surgery. Run-out is decided relatively (learned noise floor + the analyzer's 150 Hz activity/zero-crossing tests), never by an absolute dBFS gate, and nothing can fire before programme has been heard once.

## Threading model (responsiveness)

- File open/save, peak rebuilds, offline processing and exports all run on Task.Run; the UI thread never does O(file) work. Background code always operates on **snapshot channel-array refs** captured up front (splices replace arrays, never mutate them) — follow this pattern for any new heavy op.
- `PeakStore.Rebuild` builds into fresh lists and swaps atomically; `DocumentViewModel.ScheduleRebuild` coalesces edit bursts. Waveform may show stale peaks for a beat after an edit — by design.
- The band bitmap is repainted only when (view, size, ampZoom, peaksVersion, channels, doc) changes; playhead/cursor/selection/markers are overlays drawn on top. Don't put per-frame-changing state into that paint key.
- `WaveformView`/`OverviewBar` paint the wave per *device-pixel column* from `PeakStore` into a `WriteableBitmap` (Pbgra32, transparent background so the grid shows through), then blit it and draw overlays as vectors. **Do not go back to path geometry for the bands.** The old design cached a 2×-viewport `StreamGeometry` window and rebuilt it every ~0.5 screens of scroll — a rate of roughly `73.5 / samplesPerPixel` Hz, which is zero at both zoom extremes and maximal in between. That is what made playback stutter only at intermediate zoom, and wrapping it in a `BitmapCache` made each rebuild worse, not better.
- Invalidation in the view-following controls is **synchronous** (`InvalidateVisual` when `Dispatcher.CheckAccess()`). The playhead is driven from `CompositionTarget.Rendering`, which runs inside the render pass; posting the invalidate through `Dispatcher.BeginInvoke` pushed it into the next pass, and at `Normal` priority it also outranked the `Render`-priority meter/spectrum timers and `Input`-priority wheel events — which is why those felt starved whenever the waveform got busy.
- `WaveformView.ShowDiagnostics` (Ctrl+Shift+D over the waveform) overlays frame interval, render/paint ms, playhead advance and view scroll. Use it before theorising about smoothness.
- `WaveTheme.Text` caches `FormattedText` per thread, keyed by brush identity; only frozen brushes are cached. Pass a shared frozen brush, not a per-render `new SolidColorBrush(...)`.
- Saves are version-checked: `MarkSaved` only fires if `EditVersion` didn't change while writing.

## Gotchas

- Absolutely-positioned canvases in the HTML mockups need explicit width/height 100% (replaced elements ignore inset stretching).
- `EnableWindowsTargeting` is set so the project also builds on non-Windows CI.
- WPF menu/combo templates are custom — new menu items inherit styling automatically; keep `InputGestureText` in sync with `Window.InputBindings` in MainWindow.xaml.
