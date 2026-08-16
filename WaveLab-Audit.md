# WaveLab — Architecture Audit

**Scope:** all 127 `.cs` files under `src/` and `tests/` (obj/bin/.git excluded). 9 modules, read in full — nothing sampled or skipped.
**Focus:** resource management · sequence flow · runtime errors · business logic (plus security/performance where concrete).
**Findings:** 1 CRITICAL · 17 HIGH · 63 MEDIUM.

Files with no in-scope findings are omitted. Clean files are listed at the end.

---

## CRITICAL

### Audio/Effects/SaturationEffect.cs

- **Oversampling ring cursor is shared across channels — half of every FIR history is zeros** — `SaturationEffect.cs:34,141-148`. `_osPos` is a single `int` but `_osHist` is per-channel. With 2 channels the cursor advances 4 slots per frame, so `_osHist[0]` is only ever written at ring indices ≡0,1 (mod 4) and `_osHist[1]` only at ≡2,3. The 16-tap anti-alias convolution then reads 8 taps that were never written. Every stereo file with the default 2× oversample is processed at roughly half wet level with a destroyed anti-alias response. Mono is correct, which is why this survived.
  ```csharp
  // current
  private int _osPos;
  ...
      float[] hist = _osHist[c];
      hist[_osPos] = ShapeSample(mid * drive, curve) * comp;
      _osPos = (_osPos + 1) & (OsTaps - 1);
      hist[_osPos] = ShapeSample(x * drive, curve) * comp;
      double acc = 0;
      for (int k = 0; k < OsTaps; k++)
          acc += _osFir[k] * hist[(_osPos - k) & (OsTaps - 1)];
      _osPos = (_osPos + 1) & (OsTaps - 1);

  // fix
  private int[] _osPos = [];          // OnConfigure: _osPos = new int[ChannelCount];
  ...
      float[] hist = _osHist[c];
      int p = _osPos[c];
      hist[p] = ShapeSample(mid * drive, curve) * comp;
      p = (p + 1) & (OsTaps - 1);
      hist[p] = ShapeSample(x * drive, curve) * comp;
      double acc = 0;
      for (int k = 0; k < OsTaps; k++)
          acc += _osFir[k] * hist[(p - k) & (OsTaps - 1)];
      _osPos[c] = (p + 1) & (OsTaps - 1);
  ```

---

## HIGH

### App.xaml.cs

- **No global unhandled-exception handlers exist anywhere in the tree** — `App.xaml.cs:8`. Verified by grep across all of `src/`: no `DispatcherUnhandledException`, no `AppDomain.CurrentDomain.UnhandledException`, no `TaskScheduler.UnobservedTaskException`. Any exception escaping a UI event handler or `RelayCommand` body kills the process with the bare Windows crash dialog, losing every unsaved document and skipping `MainViewModel.OnCleanExitAsync`. Several `async void` handlers below become app-killers precisely because of this.
  ```csharp
  // current
  protected override void OnStartup(StartupEventArgs e)
  {
      base.OnStartup(e);
      try { MediaFoundationApi.Startup(); } catch { }
  }

  // fix
  protected override void OnStartup(StartupEventArgs e)
  {
      DispatcherUnhandledException += (_, args) =>
      {
          MessageBox.Show("WaveLab hit an unexpected error:\n\n" + args.Exception.Message,
              "WaveLab", MessageBoxButton.OK, MessageBoxImage.Error);
          args.Handled = true;               // keep the process and unsaved documents alive
      };
      AppDomain.CurrentDomain.UnhandledException += (_, args) => AutosaveService.RunNow(CollectDirtyDocuments());
      TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
      base.OnStartup(e);
      try { MediaFoundationApi.Startup(); } catch { }
  }
  ```

### Audio/Processing.cs

- **De-click bridge overshoots by an order of magnitude — writes samples ~17 dB over full scale** — `Processing.cs:214`. The Hermite tangent terms are scaled by the window length `n` (~440 samples at the 5 ms default), turning a one-sample slope into a full-interval derivative. For ordinary programme (1 kHz at full scale, |Δ| ≈ 0.14) the bridge peaks near ±9.0, and since `weight` reaches 1 at the centre it replaces the signal outright. `SmoothEditCommand` writes this straight into the document.
  ```csharp
  // current
  float bridge = (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * d0 * n
               + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * d1 * n;

  // fix — smoothstep between the window endpoints: monotone, bounded by [min(y0,y1), max(y0,y1)]
  float bridge = (2 * t3 - 3 * t2 + 1) * y0 + (-2 * t3 + 3 * t2) * y1;
  ```

### Audio/Dsp/Restoration.cs

- **Ephraim-Malah gain is inverted — loud programme is muted, noise passes at unity** — `Restoration.cs:222`. The `v >= 0.01` branch reduces to `0.5 * (1 + 1/(8v)) * exp(-v/2)`, which *decreases* monotonically with `v`: v=1 → 0.34, v=4 → 0.07, v=10 → 0.003. Since `v ≈ γ` for high-SNR bins, every loud bin is attenuated by the full reduction amount while bins at the noise floor get `gain ≈ 6.7` (clamped to 1.0). The `/gamma` divisor and the `[(1+v)I₀(v/2) + v·I₁(v/2)]` bracket are both missing, and the two branches are discontinuous at v=0.01 (0.125 → 1.0).
  ```csharp
  // current
  double v = snrPrior / (1 + snrPrior) * gamma;
  double gain;
  if (v < 0.01)
      gain = Math.Sqrt(Math.PI * v / 2) * (1 + v) * Math.Exp(-v / 2);
  else
  {
      double bessel = (1 + 1 / (8 * v)) / Math.Sqrt(2 * Math.PI * v);
      gain = Math.Sqrt(Math.PI / 2) * Math.Sqrt(v) * bessel * Math.Exp(-v / 2);
  }

  // fix
  double v = snrPrior / (1 + snrPrior) * gamma;
  double gain;
  if (v > 15.0)
      gain = v / Math.Max(1e-12, gamma);          // Wiener asymptote of MMSE-STSA
  else
  {
      double half = v / 2;
      gain = Math.Sqrt(Math.PI) / 2 * Math.Sqrt(v) / Math.Max(1e-12, gamma)
           * Math.Exp(-half) * ((1 + v) * BesselI0(half) + v * BesselI1(half));
  }
  ```

### Audio/Dsp/Restoration.Advanced.cs

- **Amplitude-outlier span expansion is unbounded — whole-file walk per candidate** — `Restoration.Advanced.cs:1241`. Unlike the curvature path (bounded by `maximumPopSamples` at lines 127/130), these walks have no length bound. On a quiet block whose median absolute amplitude is tiny (dither, fade tail), the `> referenceEnvelope * 1.5` test is true for essentially the rest of the file, so every candidate walks `start` to 1 and `end` to `n-1` — only to be discarded at line 1250. `AnalyzeClicks` becomes O(blockSize × n) and hangs the Restoration Workbench on long files.
  ```csharp
  // current
  int start = i;
  while (start > 1 && Math.Abs(samples[start - 1]) > referenceEnvelope * 1.5) start--;
  int end = i + 1;
  while (end < n - 1 && Math.Abs(samples[end]) > referenceEnvelope * 1.5) end++;

  // fix
  int start = i;
  int expansionFloor = Math.Max(1, i - maximumPopSamples);
  while (start > expansionFloor && Math.Abs(samples[start - 1]) > referenceEnvelope * 1.5) start--;
  int end = i + 1;
  int expansionCeiling = Math.Min(n - 1, start + maximumPopSamples);
  while (end < expansionCeiling && Math.Abs(samples[end]) > referenceEnvelope * 1.5) end++;
  ```

- **Spectral click repair writes the zeroed gap back over the audio** — `Restoration.Advanced.cs:1490`. The frame is built with the gap forced to 0 and the context Hann-windowed; the only spectral edit is 3-bin magnitude smoothing with the original phase retained. The inverse transform is then read out **without dividing by `window[fftIdx]`**, so every detected click is replaced by a near-silent window-scaled hole — a new click. (Currently unreferenced outside this file: a latent trap, not a live regression.)
  ```csharp
  // current
  double reconstructed = re[fftIdx] / fftSize;
  if (double.IsFinite(reconstructed))
      samples[i] += ((float)reconstructed - samples[i]) * strength;

  // fix
  float w = window[fftIdx];
  if (w < 1e-3f) continue;                 // undo the analysis window
  double reconstructed = re[fftIdx] / fftSize / w;
  if (double.IsFinite(reconstructed))
      samples[i] += ((float)reconstructed - samples[i]) * strength;
  ```
  The magnitude-smoothing step must also actually synthesise gap energy (e.g. iterative gap filling), or the reconstruction stays zero regardless of the window correction.

### Audio/Dsp/LoudnessMeter.cs

- **`_blockLoudness` grows without bound and is sorted under the lock the audio callback needs** — `LoudnessMeter.cs:235`. One entry per 100 ms sub-block, cleared only by `Reset()` — an hour of playback reaches 36 000 entries. `MasterSectionViewModel.Tick` reads `IntegratedLufs` and `LoudnessRangeLu` twice a second; `LoudnessRangeLu` does a full `OrderBy(...).ToList()` and `IntegratedLufs` two LINQ passes with a `Math.Pow` per element — all inside `lock (_lock)`, which `Process` (WASAPI render thread) takes for every buffer. The render callback stalls behind a multi-millisecond sort, and it gets worse the longer the session runs.
  ```csharp
  // current
  var abs = _blockLoudness.Where(l => l > -70).OrderBy(l => l).ToList();
  if (abs.Count < 4) return 0;

  // fix — cache on block count so repeated reads at the same count do no work
  if (_lraCacheCount == _blockLoudness.Count) return _lraCacheValue;
  double[] abs = _blockLoudness.Where(l => l > -70).ToArray();
  if (abs.Length < 4) return _lraCacheValue = 0;
  Array.Sort(abs);
  _lraCacheCount = _blockLoudness.Count;
  ```
  Store block *powers* in a fixed-capacity ring rather than an unbounded `List` to cap the memory as well.

### Audio/RecordingEngine.cs

- **`StopCore` parks the WPF dispatcher on a gate only the dispatcher can release** — `RecordingEngine.cs:1083`. `Stop()` runs on the UI thread, and `_finalizeGate.Wait()` / `_stopGate.Wait()` have no timeout. An in-flight `StopAndGetDocumentAsync` holds `_finalizeGate` across `StopAndSnapshotAsync` *and* `await Task.Run(BuildDocument)`. `StopAndSnapshotAsync` waits on `session.Stopped.Task`, completed by `OnRecordingStopped` — and NAudio's `WasapiCapture` captures `SynchronizationContext.Current` in its constructor (UI thread) and `Post`s `RecordingStopped` to it. With the UI thread blocked, that post can never run: the 5 s `WaitAsync` burns its full timeout and the dispatcher freezes for 5 s plus the whole flatten of up to 768 MiB. Trigger: press Record again, or close the window, while the previous take is finalizing.
  ```csharp
  // current
  _finalizeGate.Wait();
  try
  {
      _stopGate.Wait();

  // fix
  if (!_finalizeGate.Wait(TimeSpan.FromMilliseconds(250)))
      throw new InvalidOperationException("A recording is still being finalized.");
  try
  {
      if (!_stopGate.Wait(TimeSpan.FromMilliseconds(250)))
          throw new InvalidOperationException("A recording stop is still in progress.");
  ```
  Structural fix: release `_finalizeGate` before `Task.Run(BuildDocument)` — the gate only needs to guard stop/snapshot, not the flatten.

### Audio/SoftwareInputMonitor.cs

- **`_sync` is held across WASAPI device open/teardown while the capture callback needs the same lock** — `SoftwareInputMonitor.cs:37`. `Configure` holds `_sync` for the whole of `MonitorSession.Start`: `MMDeviceEnumerator` enumeration, `device.AudioClient`, `WasapiOut` construction, `Init`, `Play` — tens to hundreds of ms. `Enqueue` (line 84) takes the same lock from NAudio's capture thread. Toggling playthrough mid-recording stalls the capture callback well past the WASAPI period (3–500 ms), overrunning the endpoint buffer and dropping recorded audio. `StopSession` (line 103) has the same shape — `_output.Stop()` joins the render thread under `_sync`.
  ```csharp
  // current
  public void Enqueue(float[] samples, int count)
  {
      lock (_sync)
      {
          if (_session == null) return;
          try { _session.Enqueue(samples, count); }
          catch (Exception ex) { _lastError = ex.Message; _enabled = false; StopSession(); }
      }
  }

  // fix — the audio callback must never wait on a lock held across device open/teardown
  public void Enqueue(float[] samples, int count)
  {
      MonitorSession? session = Volatile.Read(ref _session);
      if (session == null) return;
      try { session.Enqueue(samples, count); }
      catch (Exception ex)
      {
          ThreadPool.QueueUserWorkItem(static s =>
          {
              var (monitor, failed, message) = ((SoftwareInputMonitor, MonitorSession, string))s!;
              lock (monitor._sync)
              {
                  monitor._lastError = message;
                  monitor._enabled = false;
                  if (ReferenceEquals(monitor._session, failed)) monitor.StopSession();
              }
          }, (this, session, ex.Message));
      }
  }
  ```
  All `_session` writes must become `Volatile.Write` for this to hold.

### Audio/CdAudioService.cs

- **Audio track preceding a data track gets the session gap appended (CD-Extra / Enhanced discs)** — `CdAudioService.cs:365`. `CreateDisc` computes each track's exclusive end as the *next* descriptor's start sector, with no session-boundary check. On a mixed-mode disc the last audio track of session 1 is followed by a session-2 data track ~11 400 sectors later, so `SectorCount` is inflated by ~2.5 minutes; `ExtractTrack` then reads past the audio programme into the lead-out and the drive fails with `ERROR_IO_DEVICE`, aborting the whole rip.
  ```csharp
  // fix — after the existing range validation
  const int SessionGapSectors = 11_400; // lead-out + lead-in + pregap between sessions

  int endSector = next.StartSector;
  if (!current.IsData && next.IsData)
  {
      endSector = next.StartSector - SessionGapSectors;
      if (endSector <= current.StartSector)
          throw InvalidToc(device.DevicePath, $"Track {number:00} has an invalid sector range.");
  }
  // ...then pass endSector (not next.StartSector) into new CdAudioTrack(...) at line 376
  ```

### Audio/Effects/StereoWidthEffect.cs

- **Side signal is double-counted when MONO BASS is off** — `StereoWidthEffect.cs:124-130`. In the `monoBass == 0` branch the full side signal feeds *both* band terms, so `candidateSide = side * (lowWidth + width)` instead of `side * width`. With the defaults (`monoBass = 0`, `lowWidth = 1`, `width = 1.2`) the side is boosted 2.2× instead of 1.2× — nearly double the requested width, with the phase-safety limiter then fighting it.
  ```csharp
  // current
  else
  {
      _lowSide = side;
      lowSidePart = side * lowWidth;
      highSidePart = side * width;
  }

  // fix
  else
  {
      _lowSide = 0;               // no low band split when mono bass is off
      lowSidePart = 0;
      highSidePart = side * width;
  }
  ```

### Audio/Effects/HumRemovalEffect.cs

- **AUTO DETECT never reaches the notch bank** — `HumRemovalEffect.cs:172`. `_detectedFundamental` is consumed only inside `Rebuild()`, and `Rebuild()` is invoked only from `OnConfigure` (line 38) and `OnParamsChanged` (line 41). `DetectFundamental` updates the estimate on the audio thread but never triggers a rebuild — so with AUTO DETECT on and MAINS left at 60 Hz, the notches stay at 60 Hz forever on a 50 Hz transfer. Only the readout changes. Worse, line 77 overwrites `_detectedFundamental` from the parameter on the next rebuild.
  ```csharp
  // fix — at the end of the confidence update in DetectFundamental
  _detectedFundamental = 0.9 * _detectedFundamental + 0.1 * candidate;
  _fundamentalConfidence = 0.9 * _fundamentalConfidence + 0.1 * confidence;
  if (_fundamentalConfidence > 0.3 && Math.Abs(_detectedFundamental - _appliedFundamental) > 0.05)
  {
      _appliedFundamental = _detectedFundamental;
      Rebuild();
  }
  ```

- **DYNAMIC mode re-injects the raw input at every harmonic stage, undoing the previous notches** — `HumRemovalEffect.cs:123`. The per-stage blend mixes against `input` (the untouched sample) rather than `pre` (what entered this stage), so each harmonic adds back `(1 - dynamicAmount)` of the *original* signal, hum included. With the default AMOUNT 0.85 and 6 harmonics only the last notch survives. Line 130 then applies `amount` a second time.
  ```csharp
  // current
  float notchOut = notches[channel][harmonic].Process(filtered);
  ...
  filtered = input * (1 - (float)dynamicAmount) + notchOut * (float)dynamicAmount;

  // fix
  float pre = filtered;
  float notchOut = notches[channel][harmonic].Process(pre);
  ...
  double depth = _harmonicSmoothing[harmonic] > 0.01
      ? 1 - Math.Clamp(_harmonicSmoothing[harmonic] * 20, 0, 1) * 0.7
      : 1.0;
  filtered = (float)(pre * (1 - depth) + notchOut * depth);   // global AMOUNT applied once, at line 130
  ```

### Audio/Effects/ChannelBalanceEffect.cs

- **AUTO ALIGN autocorrelates the L·R product, so it can only ever return lag 0 — and it silently overrides manual ALIGN** — `ChannelBalanceEffect.cs:154,173`. The window stores `left * right`, then line 173 correlates that single signal against a lagged copy of *itself*. By Cauchy–Schwarz the maximum is at lag 0, which is in the tested set, so `bestLag` is always 0. `_alignmentConfidence = bestCorr * 10` saturates to 1 on any loud material, and line 95 then forces `targetAlignment = 0`, cancelling whatever the user dialled in.
  ```csharp
  // fix — keep the channels separate and cross-correlate L against lagged R, energy-normalised
  _leftWindow[_correlationWindowPos] = left;
  _rightWindow[_correlationWindowPos] = right;
  ...
  for (int i = 0; i < _leftWindow.Length; i++)
  {
      int j = i + lag;
      if (j >= 0 && j < _rightWindow.Length) corr += _leftWindow[i] * _rightWindow[j];
  }
  corr /= Math.Sqrt(Math.Max(1e-12, leftEnergy * rightEnergy));
  ```

### Audio/Effects/CompressorEffect.cs (+ 7 more files)

- **`ResetState` does not reset any `Biquad` — `foreach` over a struct array mutates copies** — `CompressorEffect.cs:84`. `Biquad` is a `struct` (`Biquad.cs:4`), so `foreach (var f in _sidechainHpf) f.Reset()` binds a copy and the write is discarded. Filter delay lines survive every transport stop/start and offline render boundary, so the detector opens on a stale tail and the first frames are compressed against the previous take. **Identical defect at:** `LevelNormalizerEffect.cs:77,78`, `StereoWidthEffect.cs:68,69`, `MonoToStereoEffect.cs:73`, `DelayEffect.cs:70`, `TrimEffect.cs:79`, `GateEffect.cs:67`.
  ```csharp
  // current
  foreach (var f in _sidechainHpf) f.Reset();

  // fix
  for (int c = 0; c < _sidechainHpf.Length; c++) _sidechainHpf[c].Reset();
  ```

### Audio/Effects/LevelNormalizerEffect.cs

- **`ResetState` does not reset the K-weighting filters** — `LevelNormalizerEffect.cs:77-78`. Same struct-copy defect; the shelf and high-pass state persist across resets, so the loudness measurement driving the gain starts every render from the previous stream's filter memory.
  ```csharp
  // fix
  for (int c = 0; c < _kStage1.Length; c++) _kStage1[c].Reset();
  for (int c = 0; c < _kStage2.Length; c++) _kStage2[c].Reset();
  ```

### Audio/Effects/FilterEffect.cs

- **Coefficients mutated in place from the UI thread while the audio thread runs the recursive filter** — `FilterEffect.cs:64`. `Rebuild()` runs on the UI thread and writes five doubles into `_filters1[c]`; the `Volatile.Read` in `Process` (line 90) publishes only the array *reference* and does nothing for element tearing. A cutoff sweep can leave the audio thread with `_a1` from 20 Hz paired with `_a2` from 20 kHz, violating `|a1| < 1 + a2` — a pole pair outside the unit circle. The filter diverges to ±Inf/NaN, and since nothing calls `Reset()` the NaN is latched in `_z1`/`_z2`; the channel stays dead until the effect is reconfigured.
  ```csharp
  // fix — publish an immutable coefficient snapshot; only the audio thread touches state
  private sealed record Stages(Biquad One, Biquad Two);
  private Stages _stages = new(Biquad.Identity(), Biquad.Identity());
  // Rebuild(): Volatile.Write(ref _stages, new Stages(stage1, is24Db ? stage2 : Biquad.Identity()));
  // Process(): var s = Volatile.Read(ref _stages);
  //            for (int c = 0; c < ChannelCount; c++)
  //            { _filters1[c].CopyCoefficientsFrom(s.One); _filters2[c].CopyCoefficientsFrom(s.Two); }
  ```

### ViewModels/MainViewModel.cs

- **Last-played document is pinned forever — full sample buffer leaks after the tab is closed** — `MainViewModel.cs:1276`. `OnPlaybackStopped` stores the finished `AudioDocument` in `_stoppedPlaybackSource` on every natural end-of-playback; it is cleared *only* inside `OnPlaybackFailed`, when a matching device-failure event follows. After a normal play-to-end — the common case — nothing clears it, so closing the tab still leaves the whole deinterleaved float buffer rooted (~635 MB for a 30-minute stereo 44.1 kHz document). The session id is already unique, so the document reference is redundant.
  ```csharp
  // current
  _stoppedPlaybackSession = playbackSession;
  _stoppedPlaybackSource = sourceDocument;
  // ...in OnPlaybackFailed:
  if (_stoppedPlaybackSession != playbackSession
      || !ReferenceEquals(_stoppedPlaybackSource, sourceDocument)) return;

  // fix — drop the _stoppedPlaybackSource field entirely
  _stoppedPlaybackSession = playbackSession;
  // ...in OnPlaybackFailed:
  if (_stoppedPlaybackSession != playbackSession) return;
  ```

### Views/MainWindow.xaml.cs

- **`RunRangeTool` re-resolves the active document after the modal dialog — tools can be applied to the wrong file** — `MainWindow.xaml.cs:365`. `OnReduceNoise` captures document A (and A's noise print at line 470), but `RunRangeTool` reads `Doc` again after `ShowDialog()` returns. Because `MainViewModel.OpenFiles` is `async void` and its continuation is pumped by the modal dialog's nested dispatcher frame, `AddDocument` can change `ActiveDocument` mid-dialog — the noise reduction (or Time Stretch / Pitch Shift / Remove Hum / Remove Clicks) is then spliced into a different file, using A's profile.
  ```csharp
  // fix
  private async Task<bool> RunRangeTool(string undoName, Func<float[][], int, float[][]?> transform,
      DocumentViewModel? target = null)
  {
      if (_longOperationRunning) return false;
      var d = target ?? Doc;                       // callers pass the document they validated
      if (d == null || d.Doc.Length == 0 || !_vm.Documents.Contains(d)) return false;
  // ...and at every call site:  _ = RunRangeTool("Reduce Noise", (data, _) => { ... }, d);
  ```

### Views/BatchConvertDialog.xaml.cs

- **Batch conversion silently overwrites pre-existing files in the output folder** — `BatchConvertDialog.xaml.cs:120-137`. The pre-flight loop rejects outputs colliding with a *source* file or with another queue item, but never calls `File.Exists(output)` — and `AudioExporter.Export` ends with `File.Move(stagePath, finalPath, overwrite: true)`. Converting `mix.flac` into a folder that already holds a hand-mastered `mix.wav` destroys it with no prompt.
  ```csharp
  // fix — in the pre-flight loop, after the usedPaths check
  else if (File.Exists(output) &&
           MessageBox.Show($"{Path.GetFileName(output)} already exists in the output folder. Overwrite it?",
               "Batch Converter", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
  {
      item.Status = "skipped: file already exists";
      item.StatusBrush = FaintBrush;
  }
  else outputPaths[item] = output;
  ```

### Views/Controls/LevelMeter.cs

- **Vertical meter builds a negative-width `Rect` and throws when arranged below 2 px** — `LevelMeter.cs:86-100`. `OnRender` has no minimum-size guard for the vertical path (the horizontal path was written defensively with `Math.Max(0, width - 2)` at line 112). When a splitter or grid column arranges the meter to `ActualWidth < 2` with any signal present, `new Rect(1, h - 1 - fh, w - 2, fh)` gets a negative width and `Rect`'s constructor throws `ArgumentException` from inside the render pass — which, with no `DispatcherUnhandledException` handler, kills the app.
  ```csharp
  // fix — at the top of OnRender
  double w = ActualWidth, h = ActualHeight;
  if (w < 4 || h < 4) return;                 // keeps w-2 / h-2 positive
  ...
  double fh = Math.Max(0, (h - 2) * peakFrac);
  ```

### Views/Controls/SpectrogramView.cs

- **Full-range mono downmix allocated when only ~800 KB of it is ever read** — `SpectrogramView.cs:101`. `RenderWorker` allocates `float[count]` and touches every sample in the range, but the column loop reads only `cols × FftSize` = 800 × 1024 samples at `hop` strides. `MainWindow.xaml.cs:313` passes the whole visible view, so at full zoom-out on a 60-minute 44.1 kHz file `count ≈ 1.6e8` → a 635 MB transient allocation on top of the document's own arrays, for work that is a fixed 1.6 M samples.
  ```csharp
  // fix — delete the mono[] buffer; downmix only the FftSize window each column needs
  int s0 = (int)(x * hop);
  for (int i = 0; i < FftSize; i++)
  {
      int s = s0 + i;
      if (s >= count) { frame[i] = 0; continue; }
      float value = 0;
      for (int c = 0; c < channels.Length; c++) value += channels[c][start + s];
      frame[i] = value / channels.Length;
  }
  ```

### Views/Controls/AmplitudeRuler.cs

- **Ruler never repaints when the document's channel count changes** — `AmplitudeRuler.cs:40`. The control subscribes to exactly one property, `AmpZoom`, but lays itself out from `document.Doc.ChannelCount` (line 56). `AudioDocument.ReplaceAllOwned` deliberately allows channel-topology changes, reached from `MainViewModel.cs:1406` when the rack contains `MonoToStereoEffect`. The `Document` DP value doesn't change, so `AffectsRender` never fires: the waveform redraws (it listens for `PeaksVersion`) and shows two channels while the ruler keeps drawing a single-channel scale.
  ```csharp
  // fix
  private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
      if (e.PropertyName is nameof(DocumentViewModel.AmpZoom) or nameof(DocumentViewModel.PeaksVersion))
          RequestRedraw();
  }
  private void RequestRedraw()
  {
      if (Dispatcher.CheckAccess()) InvalidateVisual();
      else Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(InvalidateVisual));
  }
  ```

### tests/WaveLab.Tests/GuiActionStatusTests.cs

- **Test writes into the real `%AppData%\WaveLab\Presets` and reads the real `settings.json`** — `GuiActionStatusTests.cs:21`. `new MainViewModel()` runs `AppSettings.Instance` (loads the developer's real settings) and `EffectFactory.EnsureFactoryPresets()`, which creates preset files in the user profile and can rewrite existing ones via `TryUpgradeLegacyFactoryPreset`. Nothing cleans up. `EffectFactoryPresetTests` / `CleanupAnalyzerTests` construct `MasterSectionViewModel`, whose ctor enumerates that same directory — so with xUnit's default cross-class parallelism one class writes the directory another is reading. The ctor also mutates the process-wide static `AudioDocument.UndoBudgetBytes` from whatever the machine's settings.json contains.
  ```csharp
  // fix — give AppSettings a redirectable root
  // AppSettings.cs: public static string AppDataDir { get; set; } = Path.Combine(
  //     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveLab");
  string sandbox = Path.Combine(Path.GetTempPath(), $"WaveLab.Tests.{Guid.NewGuid():N}");
  AppSettings.AppDataDir = sandbox;          // restore + Directory.Delete(sandbox, true) in finally
  viewModel = new MainViewModel();
  ```

---

## MEDIUM

### Audio/RecordingEngine.cs

- **A software-playthrough failure aborts the recording entirely** — `RecordingEngine.cs:564`. `SoftwareInputMonitor.Configure` rethrows as `InvalidOperationException` whenever the *output* endpoint can't be opened. That call sits inside `StartCore`'s `try`, so the catch at line 582 disposes the capture and rethrows: with playthrough armed and the monitoring output unplugged or busy, `Start()` throws and no recording happens at all — even though capture itself was ready.
  ```csharp
  // fix — monitoring is an accessory; its failure is reported via SoftwarePlaythroughError
  try { _inputMonitor.Configure(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels)); }
  catch (InvalidOperationException) { }
  ```

- **`Dispose` waits for in-flight finalizations with no timeout** — `RecordingEngine.cs:1131`. `Monitor.Wait(_lifecycleLock)` is unbounded and runs on the UI thread at shutdown. The finalization it waits for is itself gated on a completion posted to that same blocked dispatcher; it only unblocks via the 5 s internal timeout, and if `BuildDocument` is cancelled/retried or a subscriber wedges, shutdown hangs with no upper bound.
  ```csharp
  // fix
  long deadline = Environment.TickCount64 + 10_000;
  while (_activeFinalizations != 0)
  {
      int remaining = (int)Math.Min(int.MaxValue, deadline - Environment.TickCount64);
      if (remaining <= 0 || !Monitor.Wait(_lifecycleLock, remaining)) break;
  }
  ```

### Audio/PlaybackEngine.cs

- **Unbounded blocking wait on the dispatcher in `DrainPendingCleanups`** — `PlaybackEngine.cs:378`. `Task.WhenAll(pending).GetAwaiter().GetResult()` has no timeout and is reached from `Play()` (line 179) and `Stop()` (line 299), both on the UI thread. The queued work is `WasapiOut.Dispose()` → `Stop()` → `playThread.Join()`; if the endpoint was invalidated (device unplugged mid-playback) that teardown is at the driver's mercy and starting or stopping playback hangs the whole UI.
  ```csharp
  // fix
  if (!Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(2)))
  {
      lock (_cleanupLock) _pendingCleanupTasks.AddRange(pending.Where(t => !t.IsCompleted));
      return;
  }
  ```

### Audio/Processing.cs

- **"Crossfade" applies a +3.01 dB gain bulge instead of unity** — `Processing.cs:105`. `cos(tπ/2) + sin(tπ/2)` equals 1 at both ends but √2 at t = 0.5, so the centre of the fade window is boosted 3 dB; material near 0 dBFS clips on export. The trailing comment asserts the opposite of what the expression computes.
  ```csharp
  // current
  ch[i] *= (float)(fadeOut + fadeIn); // unity gain at center

  // fix — the equal-power pair sums to unity in *power*; one buffer holds both sides of the join
  ch[i] *= (float)Math.Sqrt(fadeOut * fadeOut + fadeIn * fadeIn);
  ```

- **Inter-sample peak estimate leaks across channel boundaries** — `Processing.cs:56`. `prev` is declared outside the `foreach (var ch in data)` loop, so the first sample of channel *n+1* is averaged with the last sample of channel *n*. On a stereo file whose L ends at +0.9 and R begins at −0.9 the phantom "peak" is 0; in the opposite case it's spuriously large — and the −1 dBTP gain trim at line 72 is computed from a value that exists in neither channel.
  ```csharp
  // fix — move the declaration inside the channel loop
  foreach (var ch in data)
  {
      float prev = 0;
  ```

- **"Loudness Normalize N LUFS" measures unweighted RMS, not LUFS** — `Processing.cs:49`. `currentLufs` is `20*log10(RMS)` over raw samples: no K-weighting, no 400 ms gating, no −0.691 dB BS.1770 offset — despite the project already shipping a compliant `LoudnessMeter` used by `MasterSection`. Bass-heavy programme measures several dB louder than its true integrated loudness, so normalizing to −23 lands several dB off while the undo entry is labelled with the exact LUFS value.
  ```csharp
  // fix
  var meter = new LoudnessMeter();
  meter.Configure(doc.SampleRate, data.Length);
  double currentLufs = meter.MeasureIntegrated(data);   // BS.1770 K-weighting + gating
  ```

### Audio/AudioDocument.cs

- **Every edit allocates a redundant full copy of the inserted data** — `AudioDocument.cs:140`. `Splice` already copies `insert[c]` into freshly allocated destination arrays, so the document never aliases `newData`; the extra `CloneData(newData)` is a second full-size allocation on top of `oldData` and the spliced document. All 14 call sites pass a private buffer they never touch again — a whole-document `Normalize` on a 60-minute stereo file allocates ~1.3 GB of pure waste per operation.
  ```csharp
  // fix — Splice copies out of newData, so the Edit can retain the caller's array
  var edit = new Edit(opName, start, oldData, newData, false, beforeStateId, afterStateId);
  ```

### Audio/ChannelTools.cs

- **`MonoMixdown` re-reads live document state once per sample on a background thread** — `ChannelTools.cs:46`. `doc.Length`, `doc.ChannelCount` and `doc.Channels` each `Volatile.Read` `_channels` per sample. `RunGeneratedDocumentTool` (`MainWindow.xaml.cs:663`) runs this inside `Task.Run` against the live document, so a splice publishing a longer array mid-loop grows the loop bound while `mono[0]` stays sized from line 45 — `IndexOutOfRangeException`, the exact failure the project's snapshot-ref rule exists to prevent.
  ```csharp
  // fix
  float[][] source = doc.Channels.ToArray();   // point-in-time snapshot
  int length = source[0].Length, channelCount = source.Length;
  var mono = new float[1][] { new float[length] };
  for (int i = 0; i < length; i++)
  {
      float v = 0;
      for (int c = 0; c < channelCount; c++) v += source[c][i];
      mono[0][i] = v / channelCount;
  }
  ```

### Audio/AudioImporter.cs

- **Decoded block list stays rooted while the deinterleaved buffers are allocated** — `AudioImporter.cs:68`. `blocks` holds the entire decoded stream, and lines 64–65 allocate the full deinterleaved arrays before a single block is released; the `foreach` keeps every block reachable until the loop ends. Importing a 60-minute MP3 peaks at ~2× the final document size (~2.6 GB stereo) and can OOM on files that would otherwise fit.
  ```csharp
  // fix
  for (int b = 0; b < blocks.Count; b++)
  {
      cancellationToken.ThrowIfCancellationRequested();
      float[] block = blocks[b];
      blocks[b] = null!;                     // release each block as it is consumed
      for (int i = 0; i < block.Length; i++)
  ```

### Audio/WavCodec.cs · Audio/AiffCodec.cs

- **Whole data chunk buffered as bytes before decoding, doubling peak memory** — `WavCodec.cs:91` (identical at `AiffCodec.cs:131`). `ReadBytes` reads in 1 MB blocks but assembles them into one `byte[]` sized to the whole chunk, which coexists with the `float[][]` target during `Decode`. A 1.5 GB 24-bit WAV holds 1.5 GB of bytes plus 2 GB of floats simultaneously, so files that fit the documented in-memory model still OOM.
  ```csharp
  // fix — read and decode one whole-frame block at a time so only the block buffer is live
  DecodeStreaming(br, chunkStart, (int)chunkSize, format, bits, channels,
      frameCount, channelData, cancellationToken);
  ```

### Audio/CdAudioService.cs

- **Read retry has no backoff — all three attempts hit the drive within microseconds** — `CdAudioService.cs:273`. The filter retries `ERROR_BUSY` / `ERROR_SEM_TIMEOUT` / `ERROR_IO_DEVICE` but the only delay is `Thread.Yield()`, which returns immediately when nothing else is runnable. A drive that needs time to re-seek and re-attempt CIRC correction — the stated rationale in the comment — is re-issued the identical read three times back-to-back, so a recoverable scratch fails the whole extraction.
  ```csharp
  // fix
  cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50 * attempt));
  cancellationToken.ThrowIfCancellationRequested();
  ```

### Audio/WindowsCdAudioPlatform.cs

- **Three `AllocHGlobal` blocks and a kernel event object created and destroyed on every 16-sector read** — `WindowsCdAudioPlatform.cs:210`. `InvokeIoControl` allocates the input struct, a 37 632-byte output buffer and an `OVERLAPPED` block, plus a real `EventWaitHandle`, per call. One 74-minute disc issues ~20 800 reads → ~20 800 kernel handles and ~62 000 unmanaged allocations, plus a 37 KB `Marshal.Copy` per batch.
  ```csharp
  // fix — keep a per-device scratch arena and one manual-reset event alive for the handle's lifetime
  var scratch = RentScratch(inputSize, destinationLength, overlappedSize);
  inputBuffer = scratch.Input; outputBuffer = scratch.Output; overlappedBuffer = scratch.Overlapped;
  completionEvent = scratch.CompletionEvent;
  completionEvent.Reset();
  ```

### Audio/CdTransfer.cs

- **Track/disc titles written into the .cue file without stripping line breaks** — `CdTransfer.cs:291`. `CueEscape` replaces only the double-quote character and `Trim()` removes only leading/trailing whitespace. A region named `Intro\nTRACK 02 AUDIO` (region names are free-form user text) injects extra command lines, so the cue sheet describes a different track layout than the WAV files written.
  ```csharp
  // fix
  private static string CueEscape(string? value)
  {
      string text = string.IsNullOrWhiteSpace(value) ? "Audio CD" : value;
      var builder = new StringBuilder(text.Length);
      foreach (char c in text) builder.Append(c == '"' ? '\'' : char.IsControl(c) ? ' ' : c);
      return builder.ToString().Trim();
  }
  ```

- **`SafeName` does not reject Windows reserved device names** — `CdTransfer.cs:419`. It strips invalid *characters* but leaves `CON`, `NUL`, `COM1`… intact, and the cue file name is the disc title verbatim (line 296). Naming a disc `NUL` makes `File.WriteAllText` silently write to the null device and the subsequent `File.Move` fail with `FileNotFoundException`, rolling back an export that already re-encoded the whole programme.
  ```csharp
  // fix — after the existing sanitisation
  private static readonly string[] ReservedDeviceNames =
      ["CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
       "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"];
  if (ReservedDeviceNames.Contains(value, StringComparer.OrdinalIgnoreCase)) value = "_" + value;
  ```

### Audio/Dsp/Restoration.Advanced.cs

- **Levinson-Durbin clones the coefficient array on every order step** — `Restoration.Advanced.cs:854`. `TryAutoregressivePrediction` is called twice per repaired impulse from `InterpolateImpulse` (live click-repair path), allocating up to 48 `double[49]` arrays per call. A side with 20 000 clicks allocates ~2 M short-lived arrays in one repair pass.
  ```csharp
  // fix — hoist `var updated = new double[order + 1];` above the loop, then
  Array.Copy(coefficients, updated, order + 1);
  for (int index = 1; index < currentOrder; index++)
      updated[index] = coefficients[index] + reflection * coefficients[currentOrder - index];
  updated[currentOrder] = reflection;
  (coefficients, updated) = (updated, coefficients);
  ```

### Audio/Dsp/Restoration.cs

- **`DetectMainsFrequency` can never return 50 Hz, and its product overflows `int`** — `Restoration.cs:413`. `detectedFreq` is half the broadband zero-crossing rate, so any real programme yields thousands of Hz, making `dist50 > dist60` unconditionally — `RemoveHumAdvanced(adaptiveFrequency: true)` silently overrides a user's 50 Hz choice with 60 Hz. Separately, `zeroCrossings * sampleRate` is evaluated in `int`: at 96 kHz a 2-second window exceeds 2.1e9 and wraps negative.
  ```csharp
  // fix — measure the two candidates directly
  double power50 = GoertzelPower(samples, start, count, 50, sampleRate);
  double power60 = GoertzelPower(samples, start, count, 60, sampleRate);
  return power50 > power60 ? 50 : 60;
  ```

### Audio/Dsp/Fft.cs

- **FFT scratch buffers reallocated on every `MagnitudeDb` call** — `Fft.cs:57`. `SpectrumView.Update` calls this at 30 Hz on the dispatcher with n=4096 — 32 KB of garbage per frame, ~1 MB/s on the UI thread — and `SpectrogramView` calls it once per output column. `windowSum` is also recomputed each call for a constant window.
  ```csharp
  // fix
  [ThreadStatic] private static float[]? _re, _im;
  ...
  if (_re is null || _re.Length < n) { _re = new float[n]; _im = new float[n]; }
  float[] re = _re, im = _im;
  Array.Clear(im, 0, n);
  ```

### Audio/Dsp/RecordingLevelAnalyzer.cs

- **Up to 6 MB of block history copied while holding the capture lock** — `RecordingLevelAnalyzer.cs:266`. `CaptureAnalysisStateLocked` runs inside `lock (_sync)` — the lock `Process` takes for every capture buffer. In `FullDurationScanEnabled` mode `_blocks` holds up to 72 000 88-byte structs, so this `ToArray()` is a multi-megabyte LOH allocation plus copy, executed once per second. The capture callback blocks for the duration.
  ```csharp
  // fix
  CopyBlocksLocked()   // reuses a geometrically-grown scratch array; returns the same
                       // array when _blocks.Count is unchanged since the last capture
  ```

### Audio/Dsp/Limiter.cs

- **The "2× oversampling" ISP detector is mathematically incapable of finding an inter-sample peak** — `Limiter.cs:137`. `vMid`, `vQ1` and `vQ3` are convex combinations of `sample` and `prevSample`, so `|t·a + (1−t)·b| ≤ max(|a|,|b|)` — and both endpoints are already folded into `framePeak` (this frame and the previous). The `oversample` branch yields exactly the same `framePeak` as the cheap branch: `Oversample = true` costs CPU and delivers no true-peak protection, contradicting the class summary.
  ```csharp
  // fix — interpolation must be band-limited to overshoot; reuse the BS.1770 polyphase FIR
  //       already present in LoudnessMeter.TruePeakPhaseCoefficients
  for (int phase = 0; phase < TruePeakPhases.Length; phase++)
  {
      double interpolated = 0;
      for (int tap = 0; tap < TruePeakTaps; tap++)
          interpolated += TruePeakPhases[phase][tap] * _ispDelay[c][tap];
      float a = (float)Math.Abs(interpolated * drive);
      if (a > framePeak) framePeak = a;
  }
  ```

### Audio/Dsp/TempoDetect.cs

- **`Average()` on an empty envelope throws for inputs shorter than one hop** — `TempoDetect.cs:35`. When `n < 512`, `maxFrames` is 0, so `onset` is zero-length and `onset.Average()` throws `InvalidOperationException`. The `lagMax >= maxFrames / 2` bail-out sits six lines too late. The only in-app caller pre-checks for 5 seconds of audio, so this is a latent crash on the public API.
  ```csharp
  // fix — before the mean
  if (maxFrames < 4) return (0, 0);
  ```

### Audio/Dsp/StudioEq.cs

- **Every gain change discards the biquad delay state → click per slider tick** — `StudioEq.cs:31`. The gain setters call `Rebuild()`, which allocates a fresh `Biquad[3][]` and assigns brand-new structs, zeroing `_z1`/`_z2` mid-stream. `Biquad.CopyCoefficientsFrom` exists for exactly this and is used by `EqEffect.cs:64` / `FilterEffect.cs:64`; `StudioEq` doesn't. Also: `Process` dereferences `_filters[b][c]` guarded only by `_channels <= 0`, so `Configure(0, 2)` leaves `_filters` empty and throws `IndexOutOfRangeException`. (Class currently unreferenced — latent.)
  ```csharp
  // fix
  if (_filters.Length != 3 || _filters[0].Length != _channels)
  {
      _filters = new Biquad[3][];
      for (int b = 0; b < 3; b++) _filters[b] = new Biquad[_channels];
  }
  for (int b = 0; b < 3; b++)
  {
      Biquad proto = b switch
      {
          0 => Biquad.LowShelf(_sampleRate, LowFreq, _lowDb),
          1 => Biquad.Peaking(_sampleRate, MidFreq, MidQ, _midDb),
          _ => Biquad.HighShelf(_sampleRate, HighFreq, _highDb),
      };
      for (int c = 0; c < _channels; c++) _filters[b][c].CopyCoefficientsFrom(proto);
  }
  ```

### Audio/Effects/StereoWidthEffect.cs

- **`SPLIT FREQ` / `lowWidth` band-split filters are built and reset but never used** — `StereoWidthEffect.cs:52`. `_splitLowPass` / `_splitHighPass` are rebuilt on every parameter change and reset in `ResetState`, but `Process` never calls them — the actual band split is the one-pole driven by `monoBass`. The advertised 100–2000 Hz `SPLIT FREQ` knob has no audible effect at all. Either use the filters in `Process` or delete them and the `splitFreq` `EffectParam` so the UI stops offering a dead control.

- **`foreach` over a `Biquad[]` cannot reset it** — `StereoWidthEffect.cs:68-69`. Struct-copy defect (see the CompressorEffect entry). Moot only because these filters are unused.

### Audio/Effects/ChannelBalanceEffect.cs

- **M/S MODE is an algebraic identity — the mode switch does nothing** — `ChannelBalanceEffect.cs:122`. `mid + side == outL` and `mid - side == outR` exactly, so the branch writes back what it was given. Selecting M/S changes no sample; no mid or side gain is ever applied.
  ```csharp
  // fix — apply the balance to mid/side instead of to L/R before the conversion
  double mid = (left + right) * 0.5 * _leftGain;    // BALANCE drives M
  double side = (left - right) * 0.5 * _rightGain;  // BALANCE drives S
  buffer[index] = (float)(mid + side);
  buffer[index + 1] = (float)(mid - side);
  ```

- **Auto-align runs a ~550k-operation cross-correlation inside one sample iteration on the audio thread** — `ChannelBalanceEffect.cs:164`. At 48 kHz the sweep is 241 lags × a 2400-sample window, executed in the single frame where `_correlationWindowPos` wraps. That burst lands inside one `Read()` callback every 50 ms and will overrun the WASAPI buffer.
  ```csharp
  // fix — hand the filled window to a background worker and publish the result
  if (_correlationWindowPos == 0 && Interlocked.Exchange(ref _analysisBusy, 1) == 0)
      Task.Run(() => { AnalyseWindowSnapshot(); Volatile.Write(ref _analysisBusy, 0); });
  // the audio thread only reads _bestAlignment / _alignmentConfidence
  ```

### Audio/Effects/HumRemovalEffect.cs

- **Every parameter change reallocates the whole notch bank with zeroed state** — `HumRemovalEffect.cs:58`. `OnParamsChanged` → `Rebuild()` builds a fresh `Biquad[ChannelCount][]`, so dragging the AMOUNT slider — which affects no coefficient — discards all notch delay state mid-stream and allocates `ChannelCount × active` biquads per tick.
  ```csharp
  // fix — reuse the bank and copy coefficients in, preserving state
  for (int harmonic = 1; harmonic <= active; harmonic++)
  {
      Biquad proto = Biquad.Notch(SampleRate, effectiveFreq * harmonic, q);
      for (int channel = 0; channel < ChannelCount; channel++)
          bank[channel][harmonic - 1].CopyCoefficientsFrom(proto);
  }
  ```

### Audio/Effects/CompressorEffect.cs

- **Any parameter change wipes the sidechain filter state** — `CompressorEffect.cs:69`. `OnParamsChanged` calls `RebuildSidechain` unconditionally and overwrites the whole `Biquad` struct including `_z1`/`_z2`. Nudging MIX or MAKEUP zeroes the detector filter, producing a gain-reduction glitch unrelated to the knob that moved. **Same pattern at:** `GateEffect.cs:50`, `DelayEffect.cs:56`, `StereoWidthEffect.cs:52`, `TrimEffect.cs:61`.
  ```csharp
  // fix
  Biquad proto = hpfFreq > 25 ? Biquad.FirstOrderHighPass(SampleRate, hpfFreq) : Biquad.Identity();
  for (int c = 0; c < ChannelCount; c++) _sidechainHpf[c].CopyCoefficientsFrom(proto);
  ```

- **`Math.Exp` per sample with AUTO REL on, and two `Math.Pow` per sample always** — `CompressorEffect.cs:150,186`. The auto-release coefficient is recomputed from scratch every frame, and the makeup term is block-invariant.
  ```csharp
  // fix — hoist makeup out of the loop; interpolate the release coeff between precomputed ends
  double makeupLin = Math.Pow(10, makeupDb / 20.0);
  double relFast = Math.Exp(-1.0 / (SampleRate * releaseMs * 0.3 / 1000.0)), relSlow = relCoeff;
  ...
  relCoeffEff = relFast + (relSlow - relFast) * Math.Clamp(crestFactor / 12.0, 0, 1);
  float gain = (float)(Math.Pow(10, (_autoMakeupDb - grDb) / 20.0) * makeupLin);
  ```

### Audio/Effects/LevelNormalizerEffect.cs

- **True-peak limiting only sees 1 frame in 32** — `LevelNormalizerEffect.cs:160`. `framePeak` is recomputed every frame but read only inside the `_controlCountdown` block, so 31 of every 32 peaks — including the actual inter-sample maxima — are discarded before the ceiling test. A transient landing off the control grid overshoots `truePeakLimit`.
  ```csharp
  // fix — accumulate across the control interval and consume it at the update
  if (framePeak > _intervalPeak) _intervalPeak = framePeak;
  if (_controlCountdown-- <= 0)
  {
      if (_intervalPeak > 0) { double peakDb = 20 * Math.Log10(_intervalPeak); ... }
      _intervalPeak = 0;
  ```

- **`Math.Pow` per frame for a loop-invariant ratio** — `LevelNormalizerEffect.cs:173`. `maxGainChangePerFrame` is computed once at line 100, yet its exponential is re-evaluated every frame. Hoist `maxRatio` / `minRatio` above the frame loop.

### Audio/Effects/DelayEffect.cs

- **Feedback filter state destroyed on every parameter change** — `DelayEffect.cs:56`. `OnParamsChanged` rebuilds all `_fbFilters` whole-struct. Moving MIX or TIME — parameters the feedback filter doesn't depend on — hard-resets a filter *inside* the feedback loop, so running repeats step discontinuously. The class doc's "slew-smoothed delay-time changes that glide instead of clicking" is defeated by its own parameter handler.
  ```csharp
  // fix
  Biquad proto = filterType switch
  {
      1 => Biquad.LowPass12Db(SampleRate, freq),
      2 => Biquad.HighPass12Db(SampleRate, freq),
      _ => Biquad.Identity(),
  };
  for (int c = 0; c < ChannelCount; c++) _fbFilters[c].CopyCoefficientsFrom(proto);
  ```

### Audio/Effects/TrimEffect.cs

- **GAIN, polarity and phase rotation silently dropped in M/S and SWAP modes** — `TrimEffect.cs:98`. The three modes are mutually exclusive branches, so SWAP or M/S bypasses `_currentGain`, `invertL/invertR` and `_phaseAllPass` entirely. A preset like `State("trim", ("gain", -1.5))` loses its headroom reservation the moment the mode changes, and because the all-pass stops being fed, switching back resumes from a stale delay line — the click `_currentGain` smoothing exists to prevent.
  ```csharp
  // fix — make the mode a routing stage, then always run the common gain stage
  if (mode == 2 && ChannelCount >= 2) { /* swap */ }
  else if (mode == 1 && ChannelCount >= 2) { /* mid/side gains */ }
  float gain = (float)_currentGain;
  for (int channel = 0; channel < ChannelCount; channel++)
  {
      float sample = buffer[index + channel] * gain;
      if (channel == 0 && invertL) sample = -sample;
      if (channel == 1 && invertR) sample = -sample;
      buffer[index + channel] = _phaseAllPass[channel].Process(sample);
  }
  ```

### Audio/Effects/NoiseReductionEffect.cs

- **`ResetState` erases the learned noise profile, contrary to the `IAudioEffect` contract** — `NoiseReductionEffect.cs:131`. `ResetSpectralState` clears `_noiseProfile` and zeroes `_noiseProfileFrames`, but `ResetState` is documented as "clear processing state … without touching parameters" (`IAudioEffect.cs:32`) and `MasterSection` calls it on every transport start, rack enable/disable and M/S toggle. The user's captured fingerprint disappears, `spectralActive` goes false, SPECTRAL NR silently becomes a no-op — and `LatencySamples` drops from 2560 to 0 mid-render, shifting the offline alignment.
  ```csharp
  // fix — split streaming state from the learned profile
  private void ResetStreamState() { /* rings, OLA, positions, pipeline delay only */ }
  public override void ResetState()
  {
      ...
      if (_specIn.Length == ChannelCount) ResetStreamState();   // profile survives
  }
  // OnConfigure keeps calling the full ResetSpectralState (rate/channel change invalidates the profile)
  ```

- **Two `Math.Pow` and two `Math.Log10` per sample in the broadband path** — `NoiseReductionEffect.cs:188,204`. `targetNoiseGain` / `targetHissGain` are pure functions of `depth`, and the `Math.Log10` exists only to feed the once-per-block `_reductionReadout`.
  ```csharp
  // fix — precompute the fully-reduced gains once per block; track the linear min for the readout
  double fullNoiseGain = Math.Pow(10, -noiseReduction / 20.0);
  double fullHissGain = Math.Pow(10, -hissReduction / 20.0);
  ...
  double targetNoiseGain = 1 + (fullNoiseGain - 1) * depth;
  double targetHissGain = 1 + (fullHissGain - 1) * depth;
  if (_noiseGain < minGain) minGain = _noiseGain;
  // after the loop: _reductionReadout = -20 * Math.Log10(Math.Max(1e-9, minGain));
  ```

### Audio/Effects/ChorusEffect.cs

- **Per-voice detune table allocated on every `Process` call** — `ChorusEffect.cs:69`. A four-element `double[]` heap-allocated on the audio thread per buffer (~100/s at a 10 ms period), for a compile-time constant.
  ```csharp
  // fix
  private static readonly double[] Detunes = [0, 0.15, -0.12, 0.18];
  ```

- **`Math.Sin` evaluated once per voice per channel per sample** — `ChorusEffect.cs:94`. At 4 voices in stereo that's 8 transcendental calls per frame for LFOs whose phase advances by a fixed increment. Hoist to one evaluation per voice per frame outside the channel loop and apply the constant `c * π/2 * spread` offset via the angle-sum identity.

### Audio/Effects/ReverbEffect.cs

- **Eight `Math.Sin` calls per sample for the line modulation** — `ReverbEffect.cs:194`. Evaluated inside the per-line loop for every frame, though all eight lines share one phase with fixed offsets `i * π/8`.
  ```csharp
  // fix — one sin/cos per frame, then the angle-sum identity with a static offset table
  double sp = Math.Sin(_modPhase), cp = Math.Cos(_modPhase);
  ...
  double mod = (sp * OffsCos[i] + cp * OffsSin[i]) * modDepthSamples * rtScale;
  ```

- **Hadamard sign pattern recomputed with 64 `PopCount` calls per sample** — `ReverbEffect.cs:217`. The 8×8 sign matrix is a compile-time constant.
  ```csharp
  // fix
  private static readonly float[][] Hadamard = BuildHadamard();   // built once
  sum += fdnOut[j] * Hadamard[i][j];
  ```

### Audio/Effects/EqEffect.cs

- **The audio thread takes a lock inside `Process`** — `EqEffect.cs:93`. `Process` holds `_lock` for the whole buffer while `OnParamsChanged` acquires the same lock from the UI thread on every `SetParam`. A monitor is not priority-inheriting, so a preempted UI thread stalls the WASAPI callback for a full scheduler quantum — a dropout caused by moving an EQ knob. `MasterSection` already serialises `Process` under `_chainLock`, so the extra lock only buys the coefficient update.
  ```csharp
  // fix — publish an immutable band-coefficient snapshot; no lock on the real-time path
  private Biquad[] _bandCoeffs = [];                       // one prototype per band
  // OnParamsChanged(): Volatile.Write(ref _bandCoeffs, protos);
  // Process():         var protos = Volatile.Read(ref _bandCoeffs);
  //                    if (protos.Length == BandCount && _coeffVersion != published)
  //                        for each band/channel: _filters[b][c].CopyCoefficientsFrom(protos[b]);
  ```

### Audio/Effects/GateEffect.cs

- **Sidechain filters neither reset by `ResetState` nor state-preserved across parameter changes** — `GateEffect.cs:50,67`. Both defects at once: `foreach` mutates struct copies so SC filter memory outlives a reset, and `RebuildSidechain` replaces the whole struct on *any* parameter change, so moving THRESH zeroes the SC filter and the detector sees a step.

### Audio/Effects/LimiterEffect.cs

- **`ResetState` reallocates every limiter buffer instead of clearing them** — `LimiterEffect.cs:38`. `Limiter.Configure` allocates a jagged `float[channels][lookahead]` plus `_delayDrive`, `_prevInput`, `_maxValues`, `_maxFrames` and three oversampling buffers — ~40 KB at 48 kHz stereo — on every reset. `MasterSection` calls `ResetState` on transport start, rack enable/disable and M/S toggle, so routine UI actions churn the heap while audio streams.
  ```csharp
  // fix — add Limiter.Reset() that clears the existing arrays; keep Configure() for real rate/channel changes
  public override void ResetState() => _limiter.Reset();
  ```

### Audio/Effects/MonoToStereoEffect.cs

- **`ResetState` does not clear the all-pass decorrelation network** — `MonoToStereoEffect.cs:73`. Struct-copy defect. With ALGORITHM = ALL-PASS the first frames after a transport restart get the previous take's tail injected into the synthetic side signal.

### ViewModels/MainViewModel.cs

- **Rack-topology restart silently un-bypasses a preview audition** — `MainViewModel.cs:1139`. `RestartMonoPlaybackForTopologyChange` captures `position`, `loop`, `document` and `preview` but not `_previewRackRestoreState`. `ReleasePlayback` calls `RestorePreviewRackOverride()`, which writes the saved value back into `Engine.Master.RackEnabled` and nulls the field; the preview then restarts with no bypass. Trigger: audition a `bypassRack: true` preview from `CleanupAnalysisDialog`, then apply a rack containing `mono-stereo` — the A/B "dry" preview restarts wet, and `StopPreview` no longer restores the user's rack state.
  ```csharp
  // fix
  var preview = _previewDocument;
  bool? previewRackOverride = _previewRackRestoreState;
  ReleasePlayback(updatePosition: false);
  if (preview != null && previewRackOverride.HasValue)
  {
      _previewRackRestoreState = Engine.Master.RackEnabled;
      Engine.Master.RackEnabled = false;
  }
  ```

- **Unarmed Record opens the capture dialog without releasing playback** — `MainViewModel.cs:928`. The armed branch four lines below explicitly does `if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();`, but the unarmed branch fires `RequestRecordDialog` straight through. `MainWindow.ShowRecordDialog` then constructs a second `RecordingEngine` and starts capturing while WASAPI output is still streaming — with software playthrough on, the take records the playback monitor path. `RecordSetupCommand.CanExecute` (line 158) likewise doesn't require a stopped transport.
  ```csharp
  // fix
  if (!IsRecordArmed)
  {
      if (Engine.IsPlaying || Engine.IsPaused) ReleasePlayback();
      RequestRecordDialog?.Invoke();
      return;
  }
  ```

- **Every selection update re-queries all 33 commands three times** — `MainViewModel.cs:1310`. `OnActiveDocumentPropertyChanged` calls `RefreshEditCommandStates()` for `HasSelection`, `SelStart` *and* `SelEnd`, and `DocumentViewModel.SetSelection` raises all three per call. A mouse-drag selection (`WaveformView` calls `SetSelection` per mouse-move) fires ~100 × 3 × 33 ≈ 10 000 `CanExecuteChanged` events per second on the UI thread.
  ```csharp
  // fix — SelStart/SelEnd never change without HasSelection being re-raised by RaiseSelection()
  if (e.PropertyName is nameof(DocumentViewModel.HasSelection)
      or nameof(DocumentViewModel.MarkersVersion))
      RefreshEditCommandStates();
  ```

- **Master meter ticks at 30 Hz even when nothing is playing** — `MainViewModel.cs:1688`. `OnTick` calls `Master.Tick(0.033, IsPlaying)` unconditionally from the 33 ms `DispatcherTimer`, so `MasterSectionViewModel.Tick` keeps raising its seven string-formatting properties forever while the app sits idle with meters pinned at −60.
  ```csharp
  // fix
  if (IsPlaying || IsTransportRecording || Master.NeedsDecay) Master.Tick(0.033, IsPlaying);
  ```

### ViewModels/RecordViewModel.cs

- **30 Hz `ObservableCollection.RemoveAt(0)` on a 900-element history** — `RecordViewModel.cs:930`. Once `LevelHistory` hits capacity, every 33 ms tick does an O(n) `List` shift plus a `CollectionChanged(Remove, 0)` *and* an `Add`, forcing the bound items panel to re-generate and re-layout twice per tick for a whole 30-minute LP side.
  ```csharp
  // fix — write into a preallocated ring, raise one version property the strip binds to
  _levelHistoryRing[_levelHistoryPos] = Math.Max(rmsDbL, rmsDbR);
  _levelHistoryPos = (_levelHistoryPos + 1) % LevelHistoryCapacity;
  if (_levelHistoryCount < LevelHistoryCapacity) _levelHistoryCount++;
  Raise(nameof(LevelHistoryVersion));
  ```

- **`_disposed` read from the capture thread without a memory barrier** — `RecordViewModel.cs:62`. `Dispose` writes the plain `bool` on the UI thread (line 1207) while `CaptureStopped` (82), `NeedleDropTriggered` (113) and `AutoStopTriggered` (129) read it on NAudio's callback threads. Every other cross-thread flag in this path uses `Interlocked`/`Volatile`; this one can be cached in a register, so a callback can miss the disposal and dispatch work against a disposed engine.
  ```csharp
  // fix
  private volatile bool _disposed;
  ```

### ViewModels/MasterSectionViewModel.cs

- **Seven formatted-string properties re-raised every 33 ms regardless of change** — `MasterSectionViewModel.cs:310`. `Tick` unconditionally raises `LufsMText`, `LufsSText`, `TruePeakText`, `PeakLrText`, `CorrelationText`, `Correlation` and `BalanceDb`; each getter allocates a fresh interpolated string (`PeakLrText` allocates three), plus a `PropertyChangedEventArgs` per raise — ~300 short-lived allocations per second, sustained, with the transport stopped and every value unchanged.
  ```csharp
  // fix — only notify when the rendered value actually moved
  RaiseIfChanged(ref _lufsMCache, LufsMText, nameof(LufsMText));
  RaiseIfChanged(ref _truePeakCache, TruePeakText, nameof(TruePeakText));
  // ...etc
  ```

### Views/MainWindow.xaml.cs

- **`OnChannelBalance` re-reads `Doc` with `!` after the modal dialog** — `MainWindow.xaml.cs:686`. The `ChannelCount > 1` check at line 681 is made against the document active *before* `ShowDialog()`; if the active document changes while the dialog is open, the gain is applied to a different file and `Balance` silently no-ops on a mono one. `Doc!` also dereferences without a null check.
  ```csharp
  // fix
  if (Doc is not { Doc.ChannelCount: > 1 } document) return;
  var dlg = new ParamDialog("Channel Balance", ...) { Owner = this };
  if (dlg.ShowDialog() != true) return;
  if (!_vm.Documents.Contains(document) || document.Doc.ChannelCount < 2) return;
  _vm.PrepareForDocumentEdit(document);
  ChannelTools.Balance(document.Doc, dlg.Values[0], dlg.Values[1]);
  ```

### Views/CleanupAnalysisDialog.xaml.cs

- **`BuildSelectedPreset` called outside the `try` of an `async void` handler** — `CleanupAnalysisDialog.xaml.cs:258`. The same call is defensively wrapped in `try/catch` in `OnApply` (lines 380–391), but in `OnPreview` it runs before the `try` at line 272. With no `DispatcherUnhandledException` handler, a duplicate `TypeId` in the recommended chain (`ToDictionary` throws `ArgumentException`) takes down the whole application instead of showing "Preview failed".
  ```csharp
  // fix
  EffectFactory.ChainPreset? preset;
  try { preset = recommended ? _analysis.BuildSelectedPreset(selectedTypeIds) : null; }
  catch (Exception ex)
  {
      statusText.Text = "The A/B preview could not be prepared.";
      MessageBox.Show(this, ex.Message, "Preview failed", MessageBoxButton.OK, MessageBoxImage.Warning);
      return;
  }
  ```

### Views/MarkersDialog.xaml.cs

- **Modeless marker panel never subscribes to document marker changes** — `MarkersDialog.xaml.cs:21`. `MainWindow.OnManageMarkers` opens this with `Show()` (non-modal), and `Refresh()` runs only in the constructor and after rename/delete. After any edit in the main window, `DocumentViewModel.OnDocChanged` re-anchors marker positions and can `Regions.RemoveAt` collapsed regions — the list keeps showing old times while `Jump` uses the live object, and Delete on an already-removed region is a no-op that still triggers a sidecar rewrite.
  ```csharp
  // fix
  PropertyChangedEventHandler onDocChanged = (_, e) =>
  {
      if (e.PropertyName == nameof(DocumentViewModel.MarkersVersion)) Refresh();
  };
  _doc.PropertyChanged += onDocChanged;
  Closed += (_, _) => _doc.PropertyChanged -= onDocChanged;
  ```

### Views/RecordDialog.xaml.cs

- **The metronome keeps clicking when a take is ended by an unexpected device stop** — `RecordDialog.xaml.cs:291`. `OnAutoStopped` stops the click track first thing (line 446) and `FinalizeAndCloseAsync` does too (line 271), but `OnUnexpectedStopCompleted` never does. When the input device drops mid-take with the metronome on and finalization fails, `PrepareAfterFinalizationFailure()` leaves the dialog open with the WASAPI click playing over the error dialog.
  ```csharp
  // fix
  private void OnUnexpectedStopCompleted(RecordingStoppedInfo info, Exception? failure)
  {
      if (!IsVisible) return;
      try { _click.Stop(); } catch { }
      if (failure != null)
  ```

### Views/CdTransferDialog.xaml.cs

- **A close request made during a long operation is cancelled and never resumed** — `CdTransferDialog.xaml.cs:557`. Clicking X during a CD package export sets `e.Cancel = true` and cancels the token, but nothing re-issues the close once the awaited work unwinds — the window stays open and the user has to click X again. **Same hole at:** `CdImportDialog.xaml.cs:55`, `RestorationWorkbenchDialog.xaml.cs:1075`. (`ExportDialog` / `BatchConvertDialog` handle it correctly with a `_closeWhenFinished` flag.)
  ```csharp
  // fix — add `private bool _closeWhenIdle;`
  e.Cancel = true;
  _closeWhenIdle = true;
  _operation?.Cancel();
  // ...and in SetBusy(bool busy, string status), after the existing body:
  if (!busy && _closeWhenIdle) Close();
  ```

- **`PlayPreview`'s failure result is discarded, so the status line claims audio is playing when none is** — `CdTransferDialog.xaml.cs:394`. `MainViewModel.PlayPreview` returns `false` when a transport recording is active/pending or awaiting recovery; `CleanupAnalysisDialog` handles that (line 301), but here the return value is dropped and the dialog reports "Previewing …" over silence.
  ```csharp
  // fix
  if (!_main.PlayPreview(preview, loop: false, bypassRack: true))
      statusText.Text = "Preview is unavailable while recording audio is active or awaiting recovery.";
  else
      statusText.Text = renderRack ? ... : ...;
  ```

### Views/RestorationWorkbenchDialog.xaml.cs

- **`PlayPreview`'s failure result is discarded, so the A/B status lies about what is audible** — `RestorationWorkbenchDialog.xaml.cs:368`. Same defect: with a recording in progress `PlayPreview` returns `false`, yet the dialog prints "Live preview · playing … at N% restored" — the user A/Bs against silence and may commit settings they never heard.

### Views/Controls/LoudnessHistoryView.cs

- **`Pen` and two `SolidColorBrush` instances allocated inside `OnRender`, defeating the `FormattedText` cache** — `LoudnessHistoryView.cs:49-53`. The dashed target pen and both label brushes are constant but rebuilt on every tick of the 100 ms timer. Worse, the unfrozen brush at line 53 is passed to `WaveTheme.Text`, whose cache is keyed by brush *identity* and skips unfrozen brushes entirely (`WaveTheme.cs:66`) — so a fresh glyph layout is produced 10×/s for a static string, exactly the case the cache comment warns against.
  ```csharp
  // fix — frozen statics alongside MomentaryPen/ShortTermPen
  private static readonly Pen TargetPen = FreezePen(
      new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xB4, 0x54)), 1) { DashStyle = DashStyles.Dash });
  private static readonly Brush TargetLabel = FreezeBrush(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xB4, 0x54)));
  ```

### Views/Controls/AmplitudeRuler.cs

- **Invalidate posted at `Normal` dispatcher priority, contrary to the module's invalidation model** — `AmplitudeRuler.cs:41`. Every other view-following control here (`WaveformView.cs:130`, `TimeRuler.cs:50`, `OverviewBar.cs:71`) invalidates synchronously when already on the UI thread, precisely because a `Normal`-priority `BeginInvoke` lands in the *next* render pass and outranks the `Render`-priority meter/spectrum timers and `Input`-priority wheel events. A Ctrl+wheel burst queues one Normal-priority item per notch ahead of the wheel messages that produced them. (Fix folded into the HIGH entry above.)

### Views/Controls/PhaseView.cs

- **Per-frame `SolidColorBrush` for the correlation readout bypasses the `FormattedText` cache** — `PhaseView.cs:91`. Both branches construct a new brush every render, so `WaveTheme.Text` takes the uncached path and lays out glyphs from scratch 20×/s. Every other brush in this file is a frozen static (lines 16–20), so this is an oversight.
  ```csharp
  // fix
  private static readonly Brush CorrPositive = Make(WaveTheme.Accent);
  private static readonly Brush CorrNegative = Make(Color.FromRgb(0xFF, 0x5C, 0x5C));
  ```

### Util/TimeFormat.cs

- **`Position` has no negative guard and emits malformed timecode** — `TimeFormat.cs:6`. `Compact` clamps negatives at line 20; `Position` doesn't. `Position(-48_000, 48_000)` returns `"00:00:-1.000"` (the `00` specifier doesn't pad a negative), and any small negative sample count rounds the fractional part up to 1000 ms and carries into a full positive second — `Position(-1, 44_100)` yields `"00:00:01.000"`. Every readout in the app funnels through this one method.
  ```csharp
  // fix
  if (sampleRate <= 0 || samples < 0) return "00:00:00.000";
  ```

### tests/WaveLab.Tests/CleanupAnalyzerTests.cs

- **The "legacy preset is migrated" half of the test cannot fail** — `CleanupAnalyzerTests.cs:393`. The legacy file is written with `limiter.Params["thresh"] = 0` (line 380), which is already the `LimiterEffect` default, and the migration for `"Default"` only flips `limiter.Enabled` from `true` to `false`. Asserting `thresh == 0` passes identically whether the upgrade ran, was skipped, or the file was untouched; the discriminating assertion is missing.
  ```csharp
  // fix
  Assert.False(State(migrated, "limiter").Enabled);   // the value the migration actually changes
  Assert.Equal(0, Param(State(migrated, "limiter"), "thresh"), 10);
  ```

### tests/WaveLab.Tests/PeakStoreTests.cs

- **Wall-clock performance assertion makes the test order/load dependent** — `PeakStoreTests.cs:100`. Asserts 7 200 `Query` calls finish in under 40 ms of stopwatch time. xUnit runs collections in parallel and this assembly holds multi-second DSP tests plus a 2 000 000-sample `Rebuild` in this same test, so on a loaded CI agent or during a GC pause the identical, correct code fails.
  ```csharp
  // fix — assert the algorithmic property, not the clock
  Assert.True(pyramidSampleReads < rawSampleReads / 10,
      $"Query read {pyramidSampleReads} samples; the pyramid should read far fewer than {rawSampleReads}.");
  ```

### tests/WaveLab.Tests/AppSettingsAudioHardwareTests.cs

- **`Assert.Equal(60, settings.BufferMs)` asserts a constant** — `AppSettingsAudioHardwareTests.cs:181`. The object initializer never perturbs `BufferMs`, so it's still the constructor default 60 before `RestoreDefaults()` is called; the assertion passes even if `RestoreDefaults` stopped resetting `BufferMs` entirely — despite the test being named "ResetsEveryAdvancedAudioHardwareSetting".
  ```csharp
  // fix
  var settings = new AppSettings
  {
      BufferMs = 7,          // perturb it first, so the reset assertion has teeth
      CaptureBufferMs = 7,
  ```

---

## Cross-cutting patterns

Four defects repeat across many files — worth fixing as a sweep rather than one at a time:

1. **`foreach` over a `Biquad[]` to reset it.** `Biquad` is a `struct`; `foreach` binds a copy and the write is discarded. 8 sites: `CompressorEffect.cs:84`, `LevelNormalizerEffect.cs:77,78`, `StereoWidthEffect.cs:68,69`, `MonoToStereoEffect.cs:73`, `DelayEffect.cs:70`, `TrimEffect.cs:79`, `GateEffect.cs:67`. A Roslyn analyzer rule (or making `Biquad` a `sealed class`) prevents recurrence.
2. **Whole-struct filter rebuild on any parameter change**, discarding `_z1`/`_z2` mid-stream. 5 sites: `CompressorEffect.cs:69`, `GateEffect.cs:50`, `DelayEffect.cs:56`, `StereoWidthEffect.cs:52`, `TrimEffect.cs:61`, plus `HumRemovalEffect.cs:58` and `StudioEq.cs:31`. `Biquad.CopyCoefficientsFrom` already exists and is used correctly by `EqEffect` and `FilterEffect`.
3. **UI thread blocking without a timeout on work that needs the UI thread to complete.** `RecordingEngine.cs:1083,1131`, `PlaybackEngine.cs:378`. Every cross-thread wait reachable from a command handler needs a bounded timeout.
4. **Modal-dialog reentrancy: state captured before `ShowDialog()`, re-read after.** `MainWindow.xaml.cs:365,686`. `async void` continuations pump on the modal dispatcher frame, so `ActiveDocument` can change underneath. Capture the target once and re-validate membership after the dialog returns.

---

## Files reviewed with no in-scope findings

**Audio:** AudioHardware.cs, RunOutDetector.cs, NeedleDropDetector.cs, PeakStore.cs, AudioExporter.cs, Resampler.cs, Markers.cs, ClickTrack.cs, MasterSection.cs, CdAudioModels.cs, CdAudioAbstractions.cs
**Dsp:** Restoration.Streaming.cs, RestorationModels.cs, RestorationPreview.cs, RestorationRecommendations.cs, CleanupAnalyzer.cs, CleanupAnalysisModels.cs, Biquad.cs, TimeStretch.cs, PitchDetect.cs, Dither.cs
**Effects:** IAudioEffect.cs, EffectFactory.cs (every preset key/value in `CreateFactoryPreset` matches an existing `EffectParam` and falls inside its declared range; `Instantiate`/`Create` guard unknown type ids)
**ViewModels:** DocumentViewModel.cs, EffectViewModel.cs, PlayheadSeekRequest.cs
**Views:** SettingsDialog.xaml.cs, ExportDialog.xaml.cs, CdImportDialog.xaml.cs, StatisticsDialog.xaml.cs, ParamDialog.xaml.cs, CommandPalette.xaml.cs, HelpDialog.xaml.cs, InfoDialog.xaml.cs, TextPromptDialog.xaml.cs
**Controls:** WaveformView.cs, OverviewBar.cs, SpectrumView.cs, TimeRuler.cs, EqCurve.cs, LevelHistoryGraph.cs, WaveTheme.cs
**Util/Help:** AppSettings.cs, AutosaveService.cs, RelayCommand.cs, ObservableObject.cs, HelpCatalog.cs
**Tests:** AiffCodecTests, AmplitudeRulerTests, AudioDocumentTests, ClickAnalysisTests, DocumentViewModelTests, EffectFactoryPresetTests, HelpCatalogTests, HelpDialogTests, NeedleDropDetectorTests, PlaybackEngineTests, PlaybackOwnershipTests, RackEffectQualityTests, RecordingEngineBoundaryTests, RecordingLevelAnalyzerTests, RestorationPreviewTests, RestorationRecommendationsTests, RunOutDetectorTests, WaveformViewTests

**All 127 files were reviewed. None were sampled or skipped.**
