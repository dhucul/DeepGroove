# WaveLab — project notes

WaveLab-style audio editor for Windows. C# / WPF / .NET 10 (`net10.0-windows`), single project `src/WaveLab`.

## Decisions

- C# over C++: WPF is .NET-native; MVVM + NAudio ecosystem gives the more sophisticated app.
- Audio stored deinterleaved 32-bit float in `AudioDocument`; 16/24-bit sources load losslessly into float.
- Custom RIFF and AIFF codecs (`WavCodec` / `AiffCodec`) provide exact 16/24/32-bit control + TPDF dither on 16-bit export. AIFF-C import accepts uncompressed `NONE`/`twos`/`sowt`/`raw `/`in24`/`in32` PCM and `fl32`/`fl64`; compressed media import uses MediaFoundationReader.
- All edits go through `AudioDocument.ReplaceRange` (region splice) → undo/redo + change events for free.
- Waveform drawing reads `PeakStore` (min/max/RMS pyramid, base bin 64, ×4 per level); rebuilt synchronously on every edit. The base bin also sets where `Query` stops falling back to a per-sample scan (below `BaseBin * 2` samples/pixel) — keep it small enough that intermediate zoom levels stay on the pyramid.
- The dBFS scale beside the wave is an adaptive ladder (`AmplitudeRuler.BuildScale`), not a fixed level list: a level survives when the step that fits at its own offset divides it, walking the step chain 1│3│6│12│24 dB, so the ruler coarsens toward the centre line as the linear scale compresses. The labelling pass re-runs that same rule at the wider label gap, which is what keeps the numbering regular (no lone −1 stranded between 0 and −3); each step dividing the next is load-bearing, because it makes the step chosen for a label a multiple of the step chosen for a tick at the same offset, so every level the label rule asks for is already on the ladder. A level so deep that not even a 24 dB step clears the label gap is the end of the ladder and stays eligible, so the innermost number never drops out. The centre line is a reserved label slot for −∞. `MarkerLevelsDb` stays a short fixed list because `WaveformView` draws guide lines from it and the full ladder would be hatching.
- Playback: NAudio `WasapiOut` shared mode ← `MasterSection` (StudioEq → Limiter → meters/LUFS/FFT ring) ← `DocumentProvider`.
- Loudness: BS.1770 K-weighting (highshelf 1681.97 Hz +4 dB, highpass 38.13 Hz), 100 ms sub-blocks, gated integrated; true peak is the BS.1770 Annex 2 order-48 four-phase polyphase FIR. `FlushTruePeak` rings out the final taps and is non-destructive (it works on a clone), but it must only be called when the measurement is finished — on a live path it interpolates the current tail against zeros and reads high.
- Spectral foundation (`Audio/Dsp/Fft.cs` + `FftPlan.cs` + `Stft.cs` + `WindowFunctions.cs`). The FFT is Stockham autosort radix-4/2 over a double-precision twiddle table cached per size, with a real-input transform, an explicit inverse and Bluestein for non-power-of-two lengths. The arithmetic is **double** even though every caller passes float — spectral subtraction and inpainting difference nearly-equal magnitudes, which is where a float transform's lost bits surface. Convention: forward unnormalized, inverse carries the full 1/N (what the old hand-rolled conjugate-trick inversions assumed). `Fft.HannWindow` keeps its original *symmetric* definition because the restoration and cleanup code is tuned around it; new overlap-add code takes the *periodic* form from `WindowFunctions`, which is the one that satisfies COLA. `Stft` is weighted overlap-add defaulting to √Hann analysis + √Hann synthesis at 75%; it supports aliased input/output by holding each frame's input in a lookahead and zeroing the output a hop at a time, so a caller can edit a channel in place without a full-length copy.
- `Stft` policies. Two policies exist because the restoration passes need the non-default one: `StftLeadIn` (frames padded before sample zero, or starting at it) and `StftNormalization` (divide by the COLA constant, or by the weight actually accumulated on each output sample). `ReduceNoise` runs on `None` + `RunningSum` with the symmetric Hann it was tuned around — that combination is what lets the opening samples, which sit under a window value of zero, pass through untouched instead of being divided by nothing. (`ReduceNoiseAdvanced` ran on the same pair until it was measured against `ReduceNoise` and deleted.) `RestorationWolaGoldenTests` pins that pass's output to 1e-6 (about thirty times finer than a 16-bit LSB); changing either policy makes it fail, which is the point.
- `Effects/NoiseReductionEffect` still carries its own overlap-add. It is the real-time one — per-channel rings, a reported pipeline latency, and no allocation on the audio thread — and `Stft` has no streaming API yet. Migrate it when a second streaming consumer needs one, not before.
- Loudness range follows EBU Tech 3342: **3 s short-term blocks at a 1 s hop**, absolute gate −70 LUFS, then a **−20 LU relative gate**, then the 10th-to-95th percentile spread. It is deliberately *not* computed from the 400 ms blocks that feed integrated loudness — those track the envelope closely enough to count a fade or a run-out as programme, which reported a range several LU too wide.
- The undo budget counts **both** stacks. Undoing moves an edit to the redo stack rather than releasing it, so accounting that looked only at `_undo` let a long undo run grow memory without limit. Redo is trimmed first (from index 0, the furthest-future edit) since it is only reachable once the user has stepped backwards. The budget is per-document: N open tabs each hold up to that much.
- Long operations report through `ViewModels/ProgressHost` (+ `OperationProgress`, `Views/Controls/ProgressOverlay.xaml`). Workers only *store* their progress; the bound text is recomputed on a 10 Hz `DispatcherTimer` at `Render` priority — marshalling each individual report through `Progress<T>` would post tens of thousands of dispatcher callbacks per render and starve the meters and playhead. Nothing appears for the first 400 ms, and that delay is evaluated inside `Tick()` against an injectable clock rather than by waiting, which is what makes the whole policy testable without real time passing. `MainViewModel.RunBlocking` is the seam: the no-argument overload gives any existing caller an indeterminate overlay for free, the three-argument one threads a real token and `IProgress<double>` into the DSP. **The window is deliberately no longer disabled during an operation** — `IsEnabled = false` would take the overlay's own Cancel button with it — so the overlay spans rows 1–5, covering the menu bar, and the close handler asks the running operation to cancel instead of consulting `IsEnabled`. The only surviving `IsEnabled = false` is the final shutdown path; if you add one to an operation you have re-introduced a window whose Cancel button does not work. Every long path now runs through the host: open, save, render, apply chain, and everything behind `MainWindow.RunRangeTool` (noise, clicks, hum, stretch, pitch, balance) plus sample-rate convert, noise-profile learning, silence detection, the generated-document tools, tuner and BPM. Stretch, pitch, sample-rate convert, render, apply-chain and save report a real percentage; the rest are indeterminate because the DSP beneath them has no total to report.
- `Audio/Dsp/Janssen.cs` is iterative autoregressive interpolation of missing samples (Janssen, Veldhuis & Vries 1986) — the gap-filling kernel behind professional de-click, and the basis for spectral repair. It differs from `TryBidirectionalLinearPrediction` in `Restoration.Advanced.cs` by fitting the model to the signal *including* the samples being estimated and refining both together, rather than extrapolating inward from each side and cross-fading two guesses that disagree in the middle. The normal equations reduce to `Σ_j A[m-j]·x[j] = 0` over the missing positions, where `A` is the autocorrelation of the prediction-error filter — one small symmetric positive-definite system per gap, solved by Cholesky. Measured against synthetic damage it beats a linear bridge by 20-35 dB. It is now the **primary** method in `InterpolateImpulse`, with the bidirectional predictor and then the cubic bridge as fallbacks when the geometry or the audio will not support a fit. Measured on synthetic damage over the spans the repairer actually replaces, the bidirectional method managed -12.2 → -0.8 dB and Janssen manages -12.2 → 2.0 dB; `ClickRepairQualityTests` asserts bounds just below the latter, so a silent regression to either fallback fails. **The cost is roughly 35×**: a busy 22-minute side (a click every 30 ms) goes from about 2.5 s to about 80 s of repair. That is offline work with a progress bar and a cancel button, but it is the reason the fallbacks stay. Per-channel parallelism is now done: `RepairClicksInPlace` repairs each channel's run of the plan concurrently, measured at **1.96× on a stereo side** (5.9 s → 3.0 s for ~2000 clicks over 60 s), bit-identical to the serial order — `RepairingChannelsInParallelMatchesDoingThemInTurn` pins it against an internal `maxDegreeOfParallelism: 1` reference. It is safe because `CreateClickRepairPlan` is channel-major with overlaps merged per channel, so each channel's events are one contiguous run over an array no other run touches; the `previousEnd` clamp inside the worker is a backstop, since the plan's merge pass owns the disjointness invariant.
- Do not tune `JanssenOptions.For` against pure synthetic tones. On perfectly periodic material the measured SNR swings by 20 dB for a 12% change in context length, because the fit becomes sensitive to how many whole periods land in the analysis window. That is an artefact of the test signal; the noisy-material tests in `JanssenTests` are the trustworthy ones. Cost is 0.08 ms for a short click and 3.9 ms for a 5 ms pop, so a whole side is seconds, not minutes.
- `Audio/Dsp/Spade.cs` is A-SPADE sparse declipping (Kitić, Bertin & Gribonval 2015): alternate between keeping the largest frequency coefficients and forcing the frame back onto the set the observation allows (reliable samples as recorded, railed samples at least as far out as the rail). The sparsity budget starts small and relaxes, which is what stops it from simply reproducing the flat top — that is itself perfectly consistent with the constraints. The transform is orthogonal up to a scale, so the x-update is just an inverse transform followed by the projection; no inner solve. Measured gains on the clipped samples *against the clipped input*: +15.6 dB at 28% clipped, +7.2 at 48%, +2.9 at 60%, +1.9 at 74%. **Those numbers were never the comparison that mattered** — see the entry below, which measures it against the reconstruction it would replace and reaches the opposite conclusion about where it belongs.
- **A-SPADE is wired into `Declip`/`RepairClipping`, and the note above had it backwards.** Measured head to head against the Hermite peak reconstruction on the samples clipping destroyed, A-SPADE wins on **lightly** clipped material and loses on heavily crushed material — +11.3 dB on tonal programme at 37% clipped, −3.6 at 48% — where this repo had recorded it as "the method for heavily crushed material [the incumbent] cannot address". The old claim compared A-SPADE against the damage rather than against the incumbent, which is a comparison that can only flatter it. That is the whole reason the wiring took a measurement first.
- **The crossover is not one number, so `DeclipMethodChooser` takes two.** Tonal programme flips at about 42% of samples clipped and a dense harmonic stack over a noise bed at about 22%, because A-SPADE infers from a model with few significant components and material that genuinely has few survives more damage before the model runs out of evidence. `DeclippingOptions.Method` is `Automatic` by default (`PeakReconstruction` and `Sparse` force it), and the choice is **per channel** — a stereo pair need not be the same material. The two methods never both touch one channel: A-SPADE picks its own frame boundaries, and drawing an arch through its output afterwards replaces the waveform it reconstructed with the shape the other method assumed.
- **Sparsity was the wrong quantity, and three measures of it were tried before that was clear.** Spectral flatness reads 0.0130 for a dense stack and 0.0131 for sustained struck notes while A-SPADE loses on the first above 20% clipping and wins on the second to 60%; linear prediction gain fails the same pair the same way, 16.3 dB against 15.5. Bins-to-98%-of-frame-energy does separate them (42.6 against 13.0) and was shipped for two calibrations — and still measured **worse than a bare damage floor** once enough materials were on the table. It is gone, along with the bank of FFTs per channel it cost. Keep the negative result: these three are measured dead ends, not untried ideas.
- **The declip decision is one curve in damage and mean plateau length, and it is a hump.** Tolerated damage is near zero at plateaus of one or two samples, peaks near 85% at runs of ten to twenty, and returns to zero past about a hundred. Both ends belong to the arch and for different reasons: a two-sample plateau is bracketed exactly by its neighbours so a frame solve adds nothing, and a hundred-and-fifty-sample one is a wide smooth span with almost nothing reliable left in the frame to fit. **There is no damage floor** — two calibrations shipped one on the obvious cost argument, and it is wrong: barely-clipped real programme has long plateaus, which is where A-SPADE wins, so a guard at 0.02% of samples costs 19.8 dB. A-SPADE skips undamaged frames anyway, so trivial damage is trivially cheap.
- **There is no third decision variable, and that is a measured finding rather than a gap.** Stationarity, periodicity, high-frequency share, event density, shoulder trust and run-length-over-period were each tried. The residual they would have to explain is sharply characterised — stationary (frame-RMS spread 0.04), strongly periodic (autocorrelation 0.95), long plateaus at 20–50% clipped — and an exception aimed at exactly that signature improves the fit from 129.8 to 119.3 dB while making cross-validation *worse*, 143.8 to 145.8. The best two-feature rule does it more dramatically: 143.4 fitted against 185.6 held out. They memorise materials. **Cost is roughly 700×** — 0.3 ms against 210 ms for 0.9 s of audio — but it scales with damage rather than length, because undamaged frames fall straight through.
- **`ReconstructClippedPeak` used to come back under the rail, and that was the whole of the "declip is worse than doing nothing" finding.** A railed sample was *at least* the rail — the one thing clipping actually tells us — but the arch is drawn between two shoulders that both sit below it, so away from the centre, where the restoring sin² bump has died away, it dipped back under: 0.672 and 0.696 against a 0.700 plateau. That is not merely inaccurate, it is inconsistent with the observation, and it lands further from the truth than leaving the sample alone. Clamping each reconstructed sample to the recorded magnitude is **provably non-worsening** — the truth is on the far side of the rail by definition — and it is the same constraint A-SPADE's projection has always enforced. Measured across four materials and eight severities it **improved 27 of 32 cells and regressed none**, by up to 5.1 dB; tonal is untouched because its arches never dipped. `TheRepairOnlyEverPushesClippedSamplesOutward` pins the guarantee for both methods.
- **The arch extrapolates only as far as the shoulder is smooth enough to extrapolate from** (`ShoulderReliability`). Its height comes from the boundary slope carried across the gap, and on dense material that slope is mostly high harmonics and noise rather than the underlying arc — so the estimate read a rough shoulder as a steep climb and built a peak nothing supported. Measured by position inside the plateau, the reconstruction beat leaving the rail alone over the outer fifths and **lost by a factor of two across the middle**, overshooting the truth on 8,166 samples against 4,901 under. The rail is a proven lower bound and the overshoot is a guess, so an unreliable guess shrinks back towards the bound. Worth +2.6 to +3.7 dB on dense material at 20–48% clipped, across four variants of it (12/24/40 partials, two noise beds), so the finding is the material rather than one test signal.
- **The doubt is weighted by how far the arch has to reach, and without that it was a bad trade.** Shrinking every plateau took 1–2 dB off percussive and sustained material at every severity, because a two-sample gap is barely an extrapolation, its shoulders bracket it closely, and a rough shoulder there is a genuine attack rather than noise. Scaling the shrinkage by `span / 2·predictionSamples` keeps every dense gain and hands percussive back its 2 dB.
- **The dense mid band is A-SPADE's sparsity budget starving it, and that is measured — but exploiting it makes real programme worse, so it is not wired in.** `SpadeOptions.SparsityStep` defaults to 4, which grows the model far too slowly for a stack of two dozen partials over a noise bed: on that material at 20–50% clipped the default scores 5.9–10.3 dB where a step of sixteen scores 15.0–18.2, and one cell moves **6.6 to 22.2**. The band was never a limit of the method; it was the method being given a dozen coefficients to describe forty.
- **`SpadeOptions.For` sizes the budget from the material** — about an eighth of the bins holding 98% of a frame's energy, floored so simple material keeps the small step it wants. A flat larger step is not the answer (sixteen costs sparse synthetic 4.7 dB a cell, 24.7 at worst). Over 105 cells it beats the flat default on synthetic (818.6 against 743.5) *and* on real audio (1077.3 against 1072.1), and beats the best flat step held out across 31 groups.
- **The adaptive sparsity budget was measured against all three real corpora and is a large regression, so it stays off — and the escape hatch the old note left open is closed.** Over 272 cells the shipped chain scores **+9.77 dB and the adaptive budget +7.19**, a loss of **701.7 dB in total, 226 cells worse against 26 better, including every one of the 84 shellac cells**. The mechanism is visible: `EffectiveSparsity` reads a median of **57** significant bins on real programme and up to **306**, nothing like the two dozen partials it was fitted to, so the step saturates at `MaximumStep` on **96 of 272 cells** and the model grows fast enough to fit the clipping artefacts. Split by the step chosen, **every step above the default of four loses** — 5 costs 1.9 dB a cell, 8 costs 5.0, 15 costs 7.8 — so no ceiling rescues it. And **refitting the chooser cannot help**, which is what the earlier note proposed: the chooser already sends every real cell to A-SPADE, so there is nothing left to reroute. The dense-synthetic gain does not transfer to real programme at all. The old figures here (76 of 76 cells, +6.20 against 5.74) came from the corpus that included the deleted internet WAVs and are superseded by these.
- **Validating against `demo_track.wav` condemned every rule fitted to synthetic material alone, and that is the most useful thing measured here.** The previous rule — a straight line over 310 synthetic cells, cross-validated — scores **99.1 dB of shortfall on real audio, worse than always choosing A-SPADE (23.0)** and close to never choosing it (96.2), wrong in 34 of 52 real cells. The cause is a regime the synthetic set never contained: **at light damage, synthetic materials have a median plateau of 3.1 samples and real programme has 57.3.** Real music is low-frequency dominated, so even 0.2% clipping makes long plateaus, and the synthetic fit had calibrated that corner on data that does not occur. The curve now fitted to both scores 7.0 on real and 226.1 on synthetic, against 99.1 and 235.2 — better on both.
- **Five calibrations of this threshold have been wrong and the lesson never changed: the material set was too narrow.** Reasoning from A-SPADE's assumptions got the direction backwards; 32 cells was too few; ten materials *with* leave-one-out cross-validation still produced a rule worse than doing nothing clever; 24 materials missed simple tonal programme because none was in the set; and 31 synthetic materials, cross-validated, was still 99.1 dB out on real audio. **Hold out materials, never severities; include real programme; treat a synthetic-only result as untested.** `RealAudioDeclipTests` keeps a real recording in the suite for exactly this reason — including a test that pins the long-plateau property, so a future synthetic-only recalibration cannot quietly reintroduce the assumption that broke the last one.
- **Validated across 9 record transfers (36 cells, AIFF, 44.1 kHz).** Re-measured against the shipped code after the corpus was cut to record transfers only: the chain beats leaving the damage alone in **36 of 36 cells, mean +6.40 dB, worst +0.23**, every file loads, and one genuinely clipped track is detected as such while the other eight report nothing, which is the false-positive check — the damage is on **`Loving You Baby.aiff` channel 1**, and since the harness measures channel 0 its reference is still clean. The chooser sends **all 36 cells to A-SPADE** and costs 14.0 dB of regret against per-cell oracle choice, where always-arch would cost 62.7 — the curve earns its keep on synthetic material and by routing cheap cases to the cheap method, not by beating A-SPADE on real audio. **The regret and refit figures previously recorded here (34.4 dB; 278.0 against 287.3 on a three-way refit) were computed on a 76-cell corpus that included the internet WAVs and do not apply to this one.**
- **The WAV half of corpus 1 is gone and nothing may be calculated from it.** David's collection split by extension and the extension was never a format detail: the **AIFFs are transfers from records**; the **WAVs were recorded and streamed off the internet**, badly, and were not what this workbench is for. They have been **deleted**. Corpus 1 is the nine record transfers, every corpus-1 figure in this file is AIFF-only, and **they must not be reintroduced into any average**. Two things did not survive the change. The earlier split table — record transfers at +7.16 dB with one cell at −1.15, arch winning 14% — **does not reproduce**: a fresh run disagrees on `raw`, the SNR of the damaged file before any repair runs, which no code change can move, while corpus 2 reproduces to the decimal. The AIFFs measured then are not the AIFFs on disk now, so **prefer a fresh measurement to any corpus-1 number quoted from before this line**. And the **five bad cells** below were four internet WAVs and one AIFF: on the current three corpora, **272 cells, not one loses to leaving the damage alone**.
- **A short-plateau exception was fitted, validated three ways, shipped — and a second real corpus destroyed it.** Real music with plateaus under eight samples and 0.03%–3% clipped went to the arch despite the curve. On the corpus it was fitted to it halved regret (34.4 → 15.8 dB) and fixed the only five cells where the chain scored worse than leaving the damage alone. It passed the three checks that had killed every earlier exception: it improved the synthetic set it was **never fitted to** (226.1 → 194.1), halved held-out regret under leave-one-recording-out, and picked identical parameters in 18 of 19 folds. On 152 cells of a second real corpus it costs **668.7 dB** — 292.5 → 961.2, worse than always choosing A-SPADE and nearly as bad as never choosing it. Refitting the exception across all three datasets selects **no exception at all, in 87 of 88 folds**. It is gone.
- **The lesson is not that exceptions are bad; it is that transfer to synthetic material is not evidence of transfer to real material.** Both real corpora contain short plateaus at modest damage: in one the arch wins them, in the other A-SPADE does, and nothing measured tells the two apart. **Removing the exception is worth +2.85 dB averaged over all 228 real cells** — corpus 1 goes 6.20 → 5.96 dB mean and loses five cells back to below-do-nothing (worst −3.87), while corpus 2 goes **8.81 → 13.21 dB mean**. Those five cells were a real, characterised defect at the time. **They were moot for a while**: four were internet WAVs that no longer exist, the fifth does not reproduce, and across 464 cells of five corpora nothing scored below do-nothing. **A sixth corpus brought the defect back, in the same corner and on entirely different material** — see the corpus-6 entry. The lesson stands, the exception stays out, and the defect is characterised rather than fixed.
- **Second corpus: 38 files from `C:\Windows\Media`, 152 cells** — a different production origin entirely, including 22.05 kHz material the first corpus had none of, peaks from −10 to −26 dBFS, 1.2–12.8 s. Every file loads, none is falsely reported as clipped, and the chain beats leaving the damage alone in **152 of 152 cells, mean +13.21 dB, worst +1.58**. Two independent real corpora now cover the chain; a third would still be worth having, since each of the first two overturned something the other had established.
- **Third corpus: 21 public-domain 78rpm transfers from the Internet Archive's Great 78 Project (84 cells), and it is the closest material measured here to what the restoration workbench is for** — real transfer chains, surface noise, **788–6457 detected clicks a side**. The chain beats leaving the damage alone in **84 of 84 cells, mean +4.42 dB, worst +1.88**, and the chooser scores **0.0 dB regret, 0 wrong of 84**: A-SPADE wins every cell at every damage band, and always-arch would cost 274.2. It also independently confirms removing the short-plateau exception — that rule would have diverted 35 of these cells to the arch and cost 117.6 dB. Corpora and method are listed in `docs/validation-corpora.md` so the measurements can be reproduced without vendoring the audio.
- **A cap on A-SPADE's reconstruction shipped and was withdrawn by a fifth corpus. Read this before trying again.** The solver is told only that the clipped samples reached the rail, so nothing stops it inventing a peak far above one; capping it below a mean claimed overshoot of 15% (`Restoration.ShouldersBoundTheReconstruction`) gained **+46.5 dB over the first 272 cells** and survived leave-one-corpus-out on three corpora with no fold negative. **It does not survive five.** Spoken word cost it 5.2 dB and classical another 33.4: over **464 cells it is worth +7.9 dB, which is 0.017 dB a cell**, and held out a corpus at a time it scores **−38.6**, with two of the five folds selecting no cap at all. It is out of the signal path; the method and its tests remain, called by nothing.
- **The mechanism is why it failed, and it is the thing to carry forward.** The shoulders claim little overshoot *precisely when the material is sparse*, because a sparse tonal signal approaches the rail smoothly — so the gate fired hardest on the material whose reconstruction least needed capping. The variable was never "the shoulders are trustworthy"; it is closer to "this material is sparse", and I read the correlation backwards. **A-SPADE's overshoot on light damage is still real**, so this is worth attacking again — but only with something that **separates dense material from sparse**, and only against five corpora. Five refinements to this cap have now been measured; four were declined before shipping and the fifth was declined after. **Four of the five passed leave-one-recording-out first**, which is why that test is not a gate here: 84 recordings across five populations means recording-out is nearly free to pass.
- **Sixth corpus: 32 Creative Commons netlabel tracks, and it is the first population here that is loud.** Rock, hip hop, techno and drum & bass, one track per release across many labels, from the Internet Archive `netlabels` collection. It was chosen for a measured property rather than a genre: **crest factor**, peak over RMS, which is what mastering compression takes away. The six populations separate cleanly — shellac 30.3 dB median, classical and speech 21.1, record transfers 16.3, Windows Media 15.2, and **corpus 6 at 12.9, with its densest track at 8.9**. Clipping at the same relative level destroys **1.46% of its samples against 0.01–0.82%** everywhere else. The open A-SPADE question was whether the behaviour at light damage is about sparsity; answering it needs a population that is measurably not sparse, and this is one.
- **Half of it arrives already clipped, which is the finding and not the inconvenience.** **15 of 32 files are excluded by the already-clipped screen**, one of them carrying **3138 clipping events before any damage is applied**. Over the first five corpora that screen had fired twice in 464 cells, both Schubert piano sonatas. Loud mastering is not a description of style here — it is half a corpus with no clean reference left to score a repair against, and the strongest argument yet for keeping the screen: without it those fifteen would have contributed nonsense to every average quietly.
- **It broke the standing claim, and the mechanism is the one already on record.** The 17 usable files give 68 cells: **mean +4.96 dB, worst −13.87, four cells below leaving the damage alone**, where the previous five corpora had beaten do-nothing in all 464. All four losses are at **0.70, the mildest severity**, with **0.01% to 1.04% clipped and mean plateaus of 6.8 to 8.2 samples** — A-SPADE asked to rebuild programme that was very nearly intact. Forcing the other method proves it is routing and not repair: **the arch wins three of the four outright** (+0.11, +0.81, +1.58 against −2.52, −1.10, −1.97) and loses less on the fourth. Away from them the corpus is unremarkable: 64 of 68 cells gain, up to +13.67, and the chooser's regret is 11.9 dB, in line with corpora 1, 4 and 5.
- **Both rules that would divert exactly those four cells are measured dead ends, so the defect is recorded rather than bought.** A damage floor was shipped twice and costs 19.8 dB; the short-plateau exception was fitted, validated three ways, shipped, and destroyed by a second corpus at 668.7 dB. Nothing in corpus 6 reopens either — the arch is still 4.0 dB a cell worse than A-SPADE on corpus 2 (13.21 against 9.19), which is the corpus that killed the exception. **`DeclipCorpusTests.TheChainBeatsLeavingTheDamageAlone` was weakened to what is still true**: where there is real damage the repair never loses (every cell at 0.50 and below, all six corpora), every population gains by a wide margin, and the losses stay rare and stay at the mildest severity. A test that asserted every cell would now have to be deleted or fitted to the exceptions; this one still fails if the corner spreads.
- **Every other tool came through the sixth corpus clean.** Click repair **40 cells, +12.82 dB, worst +2.33, 96% of planted clicks found**; crackle **40 cells, +16.44 dB, worst +4.15**; spectral heal **30 cells, +12.50 dB, worst +1.19**. Over all six corpora the chain now measures **532 cells at +9.55 dB**, click repair **256 cells at +18.60**, crackle **260 at +21.11**, spectral heal **195 at +12.04**.
- **It is the worst false-positive material the click detector has met, and it moves the trade that was recorded.** Corpus 6 is digital-born, so every click reported in it is false: it reads **1.45 events a second median and 11.8 at worst**, above the 10.7 of Windows Media which had been the worst in the set. And the trend-relative recovery gate — whose cost was recorded as falling on speech — takes this corpus from **0.35 to 1.45 a second median and 3.1 to 11.8 worst**, and the record transfers from **1.21 to 2.56**. Dense percussion is click-shaped, none of the first five corpora had much of it, and the honest reading of that trade is now that it costs music as well as speech.
- **Wow measured over six corpora adds timing error at the bottom of its range, which the five-corpus run only hinted at.** Residual shift, uncorrected against corrected: **269 → 225** samples at 2.4% planted, **168 → 154** at 1.2%, **96 → 98** at 0.6% and **55 → 78** at 0.3%. So the correction earns its keep on a gross wow and is a wash to a mild loss below about 1%, where the estimator is reading its own floor. `CorrectingPlantedWowRemovesTimingErrorRatherThanAddingIt` asserts the two upper severities and reports the lower two, because nothing has been fitted to gate them.
- **Click repair now has the same real-audio measurement declip has, and it found two things immediately** (`ClickCorpus`, `ClickCorpusTests`; 232 cells, 1.4 min because clicks need no A-SPADE). Clicks are planted rather than found — a repair can only be scored against a clean reference — at a severity measured as **decibels above the local RMS**, because a click is audible by standing out from what surrounds it and a fixed amplitude would be a catastrophe in a quiet passage and inaudible in a loud one. **Where detection works the repair is excellent**: 24 dB above local gives **+28.6 dB** and 18 dB gives **+21.3**, both at **100% recall**. **Recall then falls off a cliff**: 12 dB finds 68% for +6.0 dB, and 6 dB finds **15%** for +0.6. That is a detection threshold rather than a repair failure, and no synthetic test would show it, because a planted click in a generator is always obvious.
- **The click detector had two defects and both were mechanism, not calibration.** First, `hfScore` — weighted highest at 0.35 and commented as the best discriminator — measured the *material* rather than the event: it took the high-frequency ratio over a window around the candidate, so a chime, a cymbal or a sibilant scores high everywhere, click or no click. It now compares the ratio **inside** the candidate against the audio either side of it (`ClickAnalysisOptions.LocalHighFrequencyContrast`), which asks whether this event moves faster than its surroundings — a question about the event. Second, **the return-to-baseline test was skipped entirely for spans of three samples or fewer**, which is exactly the shape a sharp musical attack makes; short candidates are now held to it too, a little more loosely.
- **The worst offenders came from a second detection path nobody had looked at.** The amplitude-outlier pass takes its reference envelope from the **median of |sample| over a 25 ms block**, which is a good envelope for continuous music but **collapses towards zero when there is silence between the notes** — and then every note onset reads as an enormous outlier. That is the whole explanation for the corpus split: continuous classical scored 0.05 events a second and record transfers 0.22, against 3.7 for speech and **45.9 for one Windows alarm, all false**. The envelope is now floored at half the block RMS, which cannot bind on continuous material where the median already exceeds it.
- **Measured over five corpora, both false positives and recall improved, and the one apparent regression is not one.** On material that is digital-born and therefore cannot contain a real click: Windows Media **1.81 → 0.53 a second median and 45.9 → 9.7 worst**, speech **3.72 → 2.59 and 10.9 → 6.5**, classical **0.05 → 0.01**. Recall on planted clicks rose from **70% to 81%** at 12 dB above the local level and **15% to 39%** at 6 dB, worth +5.95 → +8.44 dB and +0.45 → +2.25. **Record transfers went 0.22 → 0.93 a second, and that is the reading to be careful with**: corpus 1 is analogue transfers, which genuinely carry surface clicks, so a rise there is consistent with the recall improvement rather than against it. The corpora that can settle it — the digital ones, where every detection is false by construction — all improved.
- **Quiet-click recall was limited by the detection statistic, and a knob sweep proved it before anything was changed.** Curvature is the error of a one-sample linear predictor and music has plenty of it, so a click 6 dB above the local level does not stand out. **Dropping the confidence floor from 0.60 to 0.30 changed recall by nothing at all, at any severity** — candidates that reach the acceptance tests already pass them, and the ones that are missed never become candidates. Maximum sensitivity only reached 59% while over-detecting by 1.31x. So a **prediction residual** was added as a second nomination pass (`PredictiveDetection`, order 24), sharing `Decrackle.FitPredictor` rather than carrying a second copy: the music is largely predicted away and what is left is mostly defect. Candidates from it face the same acceptance tests as any other, and one that overlaps an existing detection is dropped rather than reported twice.
- **Then it was limited by the return-to-baseline gate, which was asking the wrong question.** Counting rejections by gate found it outright: at 6 dB above the local level that gate threw out **14,053 of 36,685 candidates against 770 at 24 dB**. The score divided the post-event deviation by the *candidate's own amplitude* — fine for a loud click, where the music around it is negligible, and wrong for a quiet one, where **the music is the deviation** and the ratio approaches one however cleanly the waveform recovered. It now compares the audio after the event against the audio before it (`TrendRelativeRecovery`), which is the question the test was always meant to ask and does not depend on how loud the event was. **Recall at 6 dB goes 52% → 82%** and at 12 dB **87% → 94%**, worth +3.99 → +8.50 dB and +10.88 → +14.19.
- **That gate had been suppressing false alarms as a side effect of being too strict, so removing the strictness needed a replacement — and the replacement is the confidence floor, which until now did nothing.** Dropping it from 0.60 to 0.30 had moved recall by *not one point at any severity*, because the recovery term feeding it was near zero for every quiet event whatever it did. With recovery measured against the trend the score separates events again and the floor bites: swept, it trades clean-material false alarms against quiet-end recall where before it traded nothing. **It sits at 0.65, not higher, because 0.70 loses pop detection outright** — the two synthetic pop tests fail there and pass at 0.68, and a threshold a hundredth from a cliff is not a threshold.
- **The cost is on speech and it is stated rather than buried.** Against this morning's detector, false detections on undamaged audio: **classical unchanged at 0.06 a second**, Windows Media 0.55 → 0.88 median with the worst case 9.9 → 10.7, and **speech 2.75 → 4.51** — plosives are click-shaped and this finds more of them. Classical staying flat is the reassuring number, being the closest digital proxy for music; speech is the price. Weighed against **+30 points of recall where clicks are hardest to find**, on a tool whose job is removing them from records, that is the right way round — but it is a trade, not a free win.
- **`PredictiveSigma` is a false-alarm control, not a recall control, which is the opposite of what a threshold usually is.** Swept from 8 to 14 it moved recall at 6 dB from 52% to 51% and the repair gain not at all, while false detections on clean digital material fell by two thirds — 0.22 events a second to 0.06 on classical. It is set to **12** for that reason. What limits recall is in the acceptance tests, not the nomination threshold, and that is where anything further has to look.
- **Where the click detector now stands against where it started.** Recall on planted clicks: **15% → 51%** at 6 dB above the local level and **68% → 86%** at 12 dB, worth **+0.61 → +3.80 dB** and **+6.03 → +10.36**. False detections on digital-born material, which cannot contain a real click: Windows Media **1.81 → 0.55 a second median and 45.9 → 9.9 worst**, speech **3.72 → 2.75 and 10.9 → 8.5**, classical **0.05 → 0.06**. Record transfers read **0.22 → 2.56**, and that number still cannot be settled from these measurements — analogue transfers genuinely carry surface clicks, so it is consistent with the recall gain rather than against it.
- **The invented damage model was flattering the detector, and a library of real clicks says by how much.** `RealClickLibrary` lifts defects off the shellac transfers — 82 of them from 17 sides, 4 to 11 samples long — using `Janssen` to reconstruct what the music was doing underneath, so what is kept is the defect and not a scrap of the record it came from. Planted into clean material at known positions, **real clicks are harder to find than the ones I designed**: re-measured after the recovery-gate fix and with stable seeds, recall is **83% against 94%** at 12 dB above the local level and **63% against 82%** at 6 dB, gain +9.22 against +13.30. **So every click recall figure quoted from the synthetic model is optimistic by eleven points at 12 dB and nineteen at 6.** At the loud end real clicks repair *better* — +31.3 against +28.8 at 24 dB — which fits their being shorter, so there is less to reconstruct. Figures re-measured over six corpora; the five-corpus run gave 84/65 and the difference is the sixth population, not a change to the detector.
- **Two ways of getting real ground truth were tried first and both failed, which is why the library is a substitute rather than the thing asked for.** Corpus 3 holds two independent transfers of the same performance, so differencing them would isolate copy-specific damage: their envelopes match at **0.885**, confirming the same take, but sample-level correlation is **0.05 to 0.08** with local alignment wandering ±2300 samples — different pressings, styli and EQ never agree waveform to waveform. And a mono 78 is lateral-cut, so the difference between groove walls should be music-free: measured, it sits only **1 to 12 dB** below the sum with channel correlation 0.14 to 0.89, and it is irrelevant anyway because corpus 1 is stereo LPs where L−R is real music. **Whether the detector's rate on a real transfer is right remains unanswerable here**; it needs a recording with its clicks marked, and marking them needs ears.
- **Crackle repair is the one tool that came through its first real measurement clean.** 232 cells, `RestorationCorpus.PlantCrackle` planting many short quiet grains rather than the few loud impulses the click harness plants — the difference between the two damage models is the difference between the two tools, so it has to be there or the measurement says nothing about which is right for which defect. **Mean +22.1 dB, worst cell +2.13, nothing below do-nothing at any severity**, from 12 dB above the local RMS down to 6 dB below it. It over-reports freely — 220 planted, 556 found — and still gains, which is the opposite trade from the click detector.
- **Spectral heal emptied cells it had no model for, and now weighs how anomalous they are first.** A mask says *where the user pointed*, not *what is wrong there*. Where `ContinuePartials` finds a partial to carry across it reconstructs the cell and replacing is the repair working — but where it **refuses**, it writes silence and the mask honours it, so a cell that held music rather than defect is simply emptied. At the local level it usually held both, which is why a burst 12 dB above local healed to +14.4 dB with every cell positive while one *at* the level scored +5.8 with **13 of 61 cells worse, down to −4.5 dB**. Refused cells are now scaled by their **excess over what the same bin does either side of the selection** (`SpectralRepairOptions.ExcessWeighted`): level with its surroundings it is left alone, three times above it the mask is honoured in full.
- **Every corpus figure before this point had a random component, from `string.GetHashCode`.** The damage seeds were derived from the recording's path, and **.NET randomises string hashing per process** — including the `StringComparison.Ordinal` overload. The corpus, the cell count and the code could all be identical and the numbers would still move: it surfaced when the same spectral-heal run gave a worst cell of −0.85 dB and then −1.46 with nothing changed, and `Heal` itself proved bit-identical over five trials. `DeclipCorpus.StableHash` (FNV-1a) replaces it everywhere. **Figures quoted before this are reproducible only to about ±0.5 dB on the worst cell**; the ones after are exact, and two consecutive runs now agree to the digit.
- **Re-measured with stable seeds, everything still passes and two sets of figures had to be corrected.** The click and crackle numbers moved by about a point, which is the size of the randomness that was in them. The **real-versus-invented comparison moved much more** — real recall at 6 dB from 40% to 65% — and that was not the seed: those figures were taken *before* the recovery-gate fix, so they measured an older detector. The finding they support is unchanged and the numbers behind it are now current. **Wow is unaffected either way**, its damage being computed from the severity alone with no seed in it.
- **Measured, it improves every severity, which is rare enough to be worth stating.** At the local level **14 of 55 cells worse becomes 2 and the worst goes −4.60 → −1.33 dB**, mean +5.84 → +8.95. Above it, 6 dB goes +10.16 → **+12.08** and 12 dB +14.00 → **+14.84**, both with nothing behind. **Refused and reconstructed cells are held to different bars, and that split is the whole of it**: a refused cell needs to stand **three times** above its surroundings before the mask is honoured in full, a reconstructed one only **1.6 times**. Treating them alike either way fails — one bar for both at the strict end cost a wide synthetic burst 2.5 dB, and leaving reconstructed cells alone entirely left two tonal orchestral cells losing, because a continuation invents confidently where there was nothing to continue.
- **It must not apply when the drift radius is zero**, which is an instruction to empty the selection rather than rebuild it; `ClosingTheGateCompletelyIsExactlyRemovingTheSelection` holds that `Heal` and `Attenuate` agree exactly there, and weighing anomalies would leave the unremarkable cells behind and quietly change what *remove* means in the UI.
- **Wow and flutter measured velocity and integrated it, which injected the very drift it existed to remove. It now measures position.** `SmoothTrajectory` median-filtered the per-block *derivative* and the result was integrated: a median does not preserve the integral of what it smooths, so amplitude was lost, and whatever noise survived integrated into a random walk — the walk the code's own comment recorded and added that smoothing to contain. Measured on real recordings the correction left **220 to 290 samples of residual drift whatever was planted**, turning **51 samples of error into 238** at a 0.3% wow. `ReferenceSeconds` now matches each block against the average of its neighbours, which measures *where the spectrum sits* rather than how fast it is moving: nothing to integrate, no walk, and the quantity measured is ten times larger. Residual drift falls to **227 / 153 / 98 / 75** samples at 2.4 / 1.2 / 0.6 / 0.3% against **288 / 249 / 237 / 238** before, and now falls with the damage instead of sitting flat.
- **It is worse on the stationary synthetic test, and that is the clearest case all day of why real material is the gate.** `WowFlutterTests.Programme()` is a sustained tone where consecutive frames are nearly identical, so frame-to-frame correlation is extremely precise there — **+7.5 dB against +1.1** for the method that replaced it. Real music is not stationary, the frame-to-frame shift is noisy, and the integration turns that into drift. **The synthetic expectations were lowered deliberately**; the numbers got worse and the tool got better, and a test on a stationary tone was the instrument that could not see it.
- **Three metrics were needed to see any of this, and two of them are traps.** Measuring the wow, correcting, and measuring again grades the correction with the estimator that drove it — by that metric the broken version *worked*. Signal to noise against the original is nearly all-or-nothing at these drifts: **a perfect correction scores +34 to +42 dB and every real one scores about zero**, so it cannot separate a half-fix from no fix. **Residual timing error** (`ResidualShiftSamples`) is linear in what was recovered and is the one the claim rests on. Also: **the reported figure and the correction quality pull in opposite directions** — a 3 s reference reads a 2.4% wow almost exactly where 0.75 s reads 0.53%, but corrects far worse, because the extra amplitude is the estimator reading musical variation as speed. 0.75 s is set for the correction, and the reported number is still low by about 2.5x.
- **What it reports is now compensated for the measurement's own filter, which is derived rather than fitted.** Matching each block against the mean of its neighbours subtracts a smoothed copy of the wow from itself: a component at *f* comes back multiplied by **1 − sinc(W·f)** for a reference of width *W*. At 0.75 s and 0.7 Hz that is **0.394**, and the estimator read **0.534% where 1.342% was planted — a ratio of 0.398**. The model accounts for the error to three digits, so it is divided out in the frequency domain rather than calibrated away. Reported deviation goes from **36% of the truth to 70%** at 2.4% planted, 41% → 78% at 1.2%, and 52% → **93%** at 0.6%.
- **Only the report is compensated; the resampling still uses the raw curve, and that is deliberate.** Dividing by a small number restores whatever noise sits at that frequency along with the signal, and admitting more low-frequency content was already measured to correct *worse* — widening the reference to 3 s left **293 samples of residual drift against 215**. With the split, residual drift is unchanged at 230/163/104/80 samples while the numbers a user reads roughly double. **The cost is at the bottom of the range**: a 0.3% planted wow now reads 0.254% against 0.168% true, because there the estimator is measuring its own floor and the compensation amplifies that too. Under-reading a bad transfer by 2.5× is the worse error of the two, since 0.25% is around the spec of a decent turntable and reads as nothing much either way.
- **Gating that cap per event rather than per channel was measured and is worse, which says something about what the channel mean is for.** The one cell the rule costs (`Windows Notify Messaging @0.70`, −2.11 dB) is not the discriminator misfiring: its shoulders are the best calibrated in the corpus, claiming 7.20% against an actual 7.33%, and **capping that cell at the true peak would score +1.57 dB over not capping it**. The loss is one plateau — 51 samples, 29% of the file's damage, holding the file's absolute peak — where the conservative 0.20 parabolic factor under-claims 35.8% against 42.9%. Exempting such events looks obvious and fails: gating each event on its own claim scores **−116.7 dB over 272 cells, 150 of them worse**, because inside a hard-clipped channel most individual plateaus have small claims and only a few deep ones carry the mean, so per-event gating caps the channels that most need leaving alone. Exempting only events claiming more than **4×** the channel mean fixes the cell (+2.12) for **+1.25 dB in total, 1 cell better and 5 worse**, and the parameter is unstable — 3× scores −12.6. **Held out a corpus at a time, refitting the variant loses 1.29 dB against simply keeping the per-channel rule.** So the channel mean is not a proxy for per-event confidence; it is a judgement about the material, and it is the right level to make this decision at.
- **`EstimatedTruePeak` was measured against the truth on 52,298 real plateaus and is not worth refitting: its error is scatter, not bias.** Median predicted/actual is **0.912** and it under-claims on 57.8% of events, but **rms(log) is 1.03 — a typical error of a factor of 2.7** — and every reparameterisation lands between 0.997 and 1.10. The best refit findable, `0.48·slope·n^0.75`, improves rms by **1.4%**. A sinusoidal model, which is exact for a clipped tone and reduces to the parabola for small overshoot, is no better than the parabola. There *is* structure — the `(length+2)` term over-inflates short plateaus, and median bias runs 1.14 at lengths 4–6 down to 0.75 at 81+ — but the constant is not where the error lives. **Recentring it is worth +8.7 dB in sample and fails held out**: lifting the cap 10% (equivalently `0.20 → 0.2193`, and the two agree to within a decimal, which is a real convergence since one was fitted on estimator accuracy and the other on SNR) gains **+11.25 dB on Windows Media, −2.53 on the record transfers, nothing on 78s**, and leave-one-corpus-out scores **−2.53 against changing nothing**, with the fold that holds out Windows Media picking no change at all. 8 cells better, 9 worse. **So the estimator stays as it is**, and the arch — which targets it on every plateau — is not disturbed for a corpus-specific gain.
- **A shellac transfer's loudest sample is a surface click, not the music, and measuring against it is a trap the app already knows about.** Clipping these files relative to their absolute peak produced 26 usable cells out of 84 because the peak sat **15.6 dB above the programme** (−4.3 dBFS against −19.9), so the clip level barely touched the music. Taking the level from a click-resistant 99.95th-percentile peak instead gives 83. `RecordingLevelAnalyzer` guards the same trap for the same reason — see `narrowArtifactOwnsAbsolutePeak`.
- **Two chooser tests were over-specified and had to be rewritten, both for the same reason.** One asserted the automatic choice was within 0.5 dB of the better method in each of ten named cells; the other asserted which method a particular cell would pick. No rule here wins every cell, so both passed on which cells happened to be listed and failed on recalibrations that were clear net improvements. They now assert what is actually true and load-bearing: the aggregate shortfall the rule is fitted to, and that the reported choice is the pass that ran.
- **Fixing the incumbent invalidated the chooser's shallow end, which had been calibrated against the broken one.** With the arch no longer dipping it is now the better method below about 1% clipped — by 8.8 dB on sustained, 6.3 on dense, 2.4 on percussive — because short runs leave excellent shoulders while a frame-level sparse model has almost no damage to justify its assumptions, and it costs 700× for the privilege. That produced `MinimumClippedFraction`, a floor of 1% below which the arch was used. **The name is gone from the code and this paragraph is kept for the history**: the floor was shipped twice, and both times a wider corpus said it was wrong — barely-clipped real programme has long plateaus, which is where A-SPADE wins, so a guard at 0.02% of samples costs 19.8 dB. What survives is `DeclipMethodChooser.ToleratedClippedFraction`, which has no floor. **The four cells corpus 6 loses are the price of that**, and they sit at 0.01–1.04% clipped — exactly where the floor would have caught them, and exactly where it was measured to cost more than it saves.
- The workbench declip card carries a **Method** switch (Automatic / Sparse / Peaks, `SegmentButton`) and a readout beneath it (design: `docs/design/declip_method.png`). **The readout is the point, not the switch**: at 700× a side that suddenly takes minutes needs an explanation that can be checked, so it states the two numbers the choice was made from — "Chose peaks · 49.4% clipped, 9 bins". It comes from `Restoration.DescribeDeclipChoices`, which is the same call `SelectSparseChannels` selects with, because a report computed separately could disagree with what actually ran. **When the channels disagree the line keeps both sets of numbers** (design: `docs/design/declip_readout.png`) — "Chose sparse on 1 (4.2%, runs 17), peaks on 2 (31%, runs 90)." The line this replaced dropped them, on the stated grounds that two channels' worth would not fit; **rendering the real control says otherwise** — the readout has **365 px** at the dialog's 860 px minimum and that sentence wants **308**, or **346** with both channels pinned at the bounded worst case. A split is the surprising case, and the one where the evidence is most worth having: it is when one channel is about to take minutes and the other a second. Both figures are bounded (100%, runs 999+) so one crushed channel cannot push the sentence off the card. Past two channels it genuinely does not fit — three wants 507 px — so channels group by method, and past eight they are counted rather than listed because sixteen want 371. `DescribeChoices` is a pure function so the wording is unit-tested without a window. Segments are `ToggleButton`s so the exclusion is hand-written, and clicking the checked one leaves it checked — "no method" is not a state the repair has.
- Rendering it offscreen at 1280 and 1024 found no layout fault this time, which is worth recording as the exception: `SegmentButton` already fixed the `ToolButton` width trap, and Automatic/Sparse/Peaks fit at both widths. What the render *did* earn was the end-to-end check — analysis to chooser to readout, reporting 49.4% clipped at 9 bins and picking peaks, which is the measured crossover landing correctly in the actual UI rather than in a test harness.
- Two things about `Spade` that are measured rather than assumed, and were both wrong on first principles. **Frame size 1024 is correct at 44.1 kHz**: the papers use 1024 at 16 kHz (64 ms) and matching that *duration* rather than the sample count is worse at every severity — 1024 beats 2048/4096/8192 monotonically, by 7.6 dB at the mildest setting. And **the rail test needs a tolerance**: a file clipped at 0.35 stores the float 0.34999999, so a naive `>=` against the double 0.35 detects nothing at all as clipped.
- `Audio/Dsp/Spectrogram.cs` is the analysis engine for the spectral editor (design: `docs/design/spectral_editor.png`): selectable FFT size, window and hop, level-calibrated so a full-scale tone reads 0 dB whichever window is chosen, and optional **time-frequency reassignment**. Reassignment relocates each cell's energy to where it actually sits, computed from two extra transforms per frame — one with the window multiplied by time, one with the window's derivative. Measured: a partial lying between two bins narrows from 6 bins to 2, and an impulse collapses from 3 frames to 1. It costs 2.1× (10 s of audio at 2048/512 takes 55 ms reassigned versus 26 ms plain), which is why it is on by default. Energy is *accumulated* into the target grid rather than assigned, because reassignment moves it between cells.
- `SpectrogramSettings.Default` is spelled out field by field rather than written `new()`. On a **record struct** the parameterless form zero-initialises instead of applying the primary constructor's declared defaults — here that meant a zero FFT size and a 0 dB floor, so every level clamped to zero and every analysis threw. The same trap applies to `SpadeOptions`, `JanssenOptions` and any future options struct.
- `Audio/Dsp/SpectralMask.cs` is the selection geometry for the spectral editor: rectangle, lasso (even-odd point-in-polygon, so concave outlines are followed rather than filled in), magic wand (four-connected growth through cells within a tolerance of the seed, bounded), and harmonic (a fundamental and its partials as separate narrow bands — a buzz is a comb, and selecting it as a rectangle would take the music between the teeth). Masks carry **weights, not booleans**: cutting a hard-edged rectangle out of a spectrum multiplies by a step function, and a step in frequency is a sinc in time, heard as a short chirp around the repair. The taper is applied by eroding then smoothing, so it runs **inward** — weight reaches 1 inside the outline and 0 at it, never beyond. A taper spreading outward would quietly modify audio outside what was drawn, which is the one thing a spectral edit must not do.
- `Audio/Dsp/SpectralRepair.cs` applies an edit through a mask. **Attenuate** scales the selected cells and resynthesises — exact, and a unity-gain edit round-trips to within 2e-7. **Heal** replaces them: per-bin sinusoidal interpolation in the manner of McAulay & Quatieri, magnitude interpolated between the two edges and phase advanced from the left edge at the rate measured there, with the discrepancy at the far edge spread evenly across the gap so the trajectory *arrives in phase* rather than beating against the audio it is spliced to. Measured against synthetic damage over the span actually replaced: emptying the selection gives 5.3 dB, healing gives 18.5 dB, and the gain holds from a 93 ms selection (15.0 dB) out to 372 ms (19.0 dB). One analysis and one synthesis — a 372 ms repair takes 4 ms.
- **The gate is what makes `Heal` safe, and it is the whole design.** A bin is continued only if the two edges of the gap agree on what frequency they see, within `PartialDriftRadians`. A sustained tone gives the same answer on each side; noise gives a phase advance that is uniformly distributed, so the two answers differ by about 1.5 rad. Measured on modulated material with a noise bed and a transient inside the selection, an open gate (π) costs 1.9 dB against simply emptying the selection, while the 0.10 default costs 0.3 dB — and on tonal material both gain 14 dB. Setting it to zero makes `Heal` bit-identical to attenuating to silence, which is asserted; that equivalence is the check that the two paths share one framing.
- Three things about `SpectralRepair` that measurement decided against my design, all recorded because rebuilding them would waste the same days. **Iterative thresholding with social-sparsity shrinkage does not work here.** The plan called for FISTA with windowed-group-lasso and persistent-empirical-Wiener shrinkage (Kowalski, Siedenburg & Dörfler); it was built, measured across ~80 configurations of threshold start, decay range and iteration count on two signals, and was *worse at every single one* — 16.5 dB at its best against 19.5 dB for the continuation alone, degrading monotonically with iterations. The cause is not the shrinkage: with the threshold set so far down that shrinkage is a no-op, the synthesis/re-analysis projection **alone** costs 2.7 dB. That is intrinsic rather than a bug — the round trip redistributes energy from the reconstructed cells into observed ones, which the data-consistency step then overwrites, so the scheme can only help a *poor* continuation and degrades a good one. It was removed rather than left switchable, because a knob that is worse at every measured setting is unfinished work, not a feature.
- The other two. **A one-pole "bandpass" is not a test signal**: most of its energy lands outside the stated edges, so the mask correctly leaves it behind and the measurement reads as though the repair had failed — build a defect from a dense sum of sinusoids inside the band instead. And **the band the mask covers has to contain real music**, or the measurement only ever rewards emptying the selection and says nothing about reconstruction; with two quiet partials in-band the optimum was "remove everything, never rebuild".
- `SpectralMask.Feather` clamps its radius to `(min(frames, bins) - 1) / 2`. Eroding a two-frame selection by two cells left nothing at all, so a small repair was a silent no-op — the taper gives way to the region rather than the other way round. `SpectralRepair` also counts **any** weight above 1e-3 as masked when it looks for runs to fill: a half-and-half test would leave the outer half of every feather holding its observed value, so the taper would hand back to audio the user asked to have repaired instead of to the repair.
- The wand keeps `visited` and `selected` as separate arrays. `visited` stops a cell being queued twice; `selected` is what reaches the mask. Filling the mask from everything ever *queued* ignores the cell limit entirely, because the queue holds far more than the limit allows.
- `Views/Controls/SpectrogramImage.cs` maps analysed spectra to pixels: log frequency up the side, time across, level through a perceptually uniform ramp (viridis, magma, the house teal, or grey). Uniform ramps are not decoration — the usual heat maps have uneven lightness and so invent visible contours where the data is smooth, which matters in a tool whose job is deciding whether a faint thing is really there. Every ramp is tested for monotone lightness. **The reduction when many bins fall into one row is a maximum, not a mean**: on a log axis most rows cover many bins, and averaging buries a one-bin partial in the floor either side of it, which is exactly the detail the view exists to show.
- Reassignment sharpens partials into lines and transients into strokes, but it makes a noise floor look *speckled* — it concentrates broadband energy into scattered points rather than a smooth wash. That is why it is a toggle rather than always on: it is the better view for tonal and impulsive content and the worse one for judging hiss.
- `Views/Controls/SpectralEditorView.cs` is the editing surface from `docs/design/spectral_editor.png`. It is a **separate control from `SpectrogramView`**, which remains the read-only image in the analysis tab; the editor belongs in the main editor area on the waveform's time axis. It follows the `WaveformView` discipline — bitmap repainted only when the paint key changes, selection and playhead drawn as vector overlays so they never trigger a re-analysis — which matters more here than for the waveform, because a screenful costs tens of milliseconds against a peak-pyramid read of well under one. Analysis therefore runs on the pool with the previous bitmap left on screen until the new one lands, the same bargain the waveform makes with peak rebuilds. Channel refs are captured on the UI thread before handing off, per the usual snapshot rule.
- Two things about that control worth keeping. `DocumentViewModel.ViewStart` is **clamped against the visible width**, so setting it before `SamplesPerPixel` silently does nothing at a zoom where there is nowhere to scroll — zoom first, then scroll. And a spectral selection uses `WaveTheme.SelectionOverlay`, not `SelectionFill`: the 8% wash that reads clearly over a near-black waveform disappears over a bright spectrogram.
- The editor area is a **Waveform / Split / Spectrogram** switch (`MainViewModel.EditorView`), with `WaveformView` and `SpectralEditorView` stacked in one nested grid inside the existing editor column. They share the outer grid's time axis, so the overview bar, time ruler, transport and playhead keep working unchanged whichever mode shows. The gutter follows: `AmplitudeRuler` beside the waveform, `FrequencyRuler` beside the spectrogram. Row heights are set in `MainWindow.ApplyEditorViewMode` rather than bound, because the split carries a `GridSplitter` — once the user has dragged it the star weights are theirs, and a binding would overwrite them on the next mode change. Waveform is the default and hides the spectral controls entirely, so the app costs exactly what it did before until the spectrogram is asked for.
- **`Audio/Dsp/SparseInpaint` is the plan's sparse Gabor inpainting — FISTA with social shrinkage — and it is built, correct, tested and NOT the default, because it measures worse.** Keep it that way unless a listening test says otherwise. The whole thing turns on `SpectralRepair.Frame.Project`, `T = A∘S`: synthesise, then re-analyse, which maps any grid of numbers onto the ones a real signal could actually have produced. Iterating that against the observation is Papoulis–Gerchberg; replacing the hard projection with a **proximal step** is the step to here, so each iteration makes the estimate consistent *and* sparse. Shrinkage is Kowalski/Siedenburg **social** — windowed group lasso and persistent empirical Wiener, where a coefficient's threshold is set by its neighbourhood's RMS but only the coefficient is shrunk, so a quiet cell inside a partial survives and a loud isolated one does not. The threshold **descends** across the iterations (a homotopy: strongest structure first, detail later) from a quantile of the material's own levels, so nothing depends on how loud the file is.
- **Measured against the per-bin continuation it was meant to replace**: tonal programme with a 512-sample selection **15.7 → 19.0 dB** (a real win); every selection 4096 samples and wider, parity; noisy 11.3 → 2.4 and percussive 11.1 → −0.3. Two structural reasons and neither is tuning. **Reach** — it reconstructs from evidence within about a window of the selection's edge, and a selection a user actually draws is 100–400 ms wide, so its middle sags towards silence; continuation extrapolates a *model* (a sinusoid whose frequency was measured at the edge), which holds at any width. **Predictability** — where continuation refuses a cell it writes silence, and on noise and transients silence is the better estimate, because that content genuinely cannot be predicted from neighbouring frames and a plausible fill uncorrelated with the truth scores worse than a hole. The solver is therefore pointed **only at the cells continuation refused**, where silence is the floor it competes against.
- **Two bugs, and the test that found both was the degenerate one — with the penalty off, the solver must reproduce its own input exactly.** A quality number can only say something is worse, never what. (1) `Project` divided by one COLA constant, which is right in the interior and wrong at the block's outermost frames, where no neighbours complete the window sum: an 11% drift on a grid that was already consistent, so not a projection and so no basis for a step size. It now divides by the **accumulated window sum position by position**. (2) With the data term written on `Tα` and weighted differently inside the selection from outside, the fixed point is a *weighted* projection of the estimate, which is not the estimate — so the solver could not reproduce its own starting point however hard it was held to it, and quietly lost 2 dB. Both now read 0.00.
- Spectral repair reaches the UI through a **second toolbar bar** under the transport (`ShowsSpectrogram`), carrying Heal, Attenuate… and a selection/band readout, per `docs/design/spectral_editor.png`. Both bars sit in one `StackPanel` in grid row 2 so the bar can appear and disappear without renumbering every row below it; that row is `Auto`, and the transport keeps its own `Height="62"`. The selection is `MainViewModel.SpectralSelection`, bound **two-way** to `SpectralEditorView.Selection`: dragging fills the readout and clearing the region erases the drawn rectangle, so the two cannot disagree. It is cleared when `ActiveDocument` changes, because a time-frequency region belongs to the file it was drawn on. `MainWindow.RunSpectralRepair` is deliberately *not* `RunRangeTool` — that splices the time selection, while a spectral repair decides its own span (a mask needs a window of context either side to resynthesise) and reports back where its result belongs.
- `Audio/Dsp/SpectralPattern.cs` is **Learn pattern**: a noise signature learned from a time-frequency *region*, then suppressed elsewhere. This is not `Restoration.LearnNoiseProfile`, which learns from a span of **time** and so needs a passage where the noise plays alone. Here the signature comes from whatever the mask covers — select a buzz's partials with the harmonic tool and only those bins are learned and only those bins are ever touched. **A bin the selection never covered has no signature and is passed through untouched**, which is what makes it safe to run over a whole side. Measured on a 120 Hz comb under music: 12 of 1025 bins learned over 108–754 Hz, the buzz down 15.7 dB, and music at 900 / 1650 / 2400 Hz moved 0.00 dB.
- Suppression is **MMSE log-spectral amplitude** (Ephraim & Malah 1985) with **presence-probability gating** (Cohen's OM-LSA), which is a step beyond the MMSE-STSA that used to sit in `ReduceNoiseAdvanced` — a different tool rather than a replacement, and it survived the measurement that deleted that one because it is pointed at a *learned pattern in specific bins* rather than at a broadband floor. LSA minimises the error in the *log* spectrum: the same numerical error is far more audible in a quiet bin than a loud one, and minimising it where it is audible is what makes it quieter. The gate is `G = G_LSA^p · G_min^(1−p)` — interpolating in the *exponent*, which pins a bin holding only the pattern at the floor instead of leaving it hovering a few dB above, audible as a residual whisper of whatever was removed. Measured: gating takes an empty bin from 0.106 to 0.011 against a floor of 0.010.
- `SpectralPattern.ExponentialIntegral` is E₁, series below one and a continued fraction above, agreeing with reference values to 1e-8 and with each other at the join to 3e-9. **Abridged tables are not a test**: A&S table 5.1 carries seven significant figures, which cannot tell a correct implementation from one that is merely close — the first version of this test failed against my own correct code because the constants I had typed were truncated.
- **The resolution limit is measured on purpose, not hidden.** At 2048 points a bin is 21.5 Hz and a tone's main lobe spans about four of them, so a musical partial within roughly two bins of a learned band shares those bins and loses some of itself: measured at −2.9 dB for a 330 Hz tone beside a 360 Hz learned partial, against 0.0 dB for one clear of the comb. No gain rule can separate them. Keep that case in its own test — folding it into the main signal lets a genuine defect hide behind a limit, which is exactly what the first version of these tests did.
- Three spectral actions. **Heal** rebuilds the selection, **Attenuate…** turns it down, **Gain…** moves it either way. Attenuate carries the mode that makes it more than a signed gain: `SpectralRepair.AttenuateToSurroundings` reduces each bin to what that same bin carried just before and just after the selection, never below it and never past the reduction limit, scaling magnitude only and leaving phase alone. That is what separates it from `Attenuate`'s fixed multiply — a fixed reduction takes the music down with the defect by the same amount everywhere, so it can only be right at one programme level. **Measured across a 20 dB spread of programme level, matching the surroundings lands within 0.0 dB of the clean tone at every level while a fixed −18 dB is out by 14 dB across the same spread.**
- Anchors for that mode must **carry signal**, not merely exist (`Frame.CarriesSignal`). The frame range is padded past the mask so the overlap-add reconstructs, and those extra frames read zeros beyond the ends of the file; taken as anchors, a selection covering the whole file matched itself to the silence off the end and scooped everything to nothing. With no usable anchor on either side the audio is left alone, because inventing a target would be a fixed reduction wearing a different name.
- `SpectralRepair.ForEachRun` is the one definition of "a masked run and the observed frames either side of it", shared by the continuation and by attenuation. `MaskedWeight` (1e-3) is the threshold, for the reason recorded above.
- Four selection tools (`SpectralTool`): rectangle, lasso, magic wand, harmonic. **The selection is the mask, not a rectangle** (`ViewModels/SpectralSelection`): only one of the four draws something a rectangle can describe, so carrying bounds and rebuilding a mask later would discard everything a lasso or wand had drawn. `Bounds` exists for the toolbar readout and nothing else. The mask is always in the **repair** grid — anchored at sample zero, at the hop spectral edits use — never in the display's grid, whose hop tracks the zoom and reaches the transform length when zoomed out, where nothing can be resynthesised cleanly.
- The wand analyses **its own six-second window** rather than reusing the display's, for two reasons: the display grid moves with the zoom, and the display is reassigned, which scatters a noise floor into isolated points and breaks the connectivity growth depends on. `SpectralEditorView.PerformGesture` is the seam the gesture tests drive; the mouse handlers are thin wrappers over the same call, so the tests exercise the real path rather than a parallel copy.
- **The wand needs a floor as well as a tolerance.** Seeded on the noise floor every cell is within tolerance of every other, so it swallowed the whole analysed window — measured at 195 000 cells from one click on silence, stopped only by the cell cap. A seed must now be `FloorMarginDb` above the quietest cell present, and growth will not reach below that either. Measured after: a click on a real defect grows 1 576 cells over 211 456–228 864 samples and 1443–2950 Hz, against a burst planted at 210 000–230 000 and 1500–2880 Hz; a click on silence grows nothing.
- **Harmonic bands are written with an analytic flat-top profile, not eroded and smoothed.** The general feather erodes first, which a three-bin band cannot survive — it came back at half weight, so the comb applied at half strength however hard it was asked for. Widening the bands to give the erosion room is worse: partials of a low fundamental sit a handful of bins apart, so wider bands merge into a solid block and take the music between the teeth, which is the one thing the tool exists to avoid. The profile is flat within the core and cosine to the edges, because a profile peaking only at the exact centre under-applies whenever the partial falls between bins — which is almost always. Time is tapered separately by `TaperFrames`.
- **The drag-too-small guard is in pixels, not samples.** It is what stops a slip becoming a repair, and the user's hand works in pixels: at a hundred-odd samples per pixel a sample-based guard is cleared by a single-pixel twitch.
- The spectral selection **dims its surround rather than tinting itself** (`WaveTheme.SelectionScrim`). Two things forced this. The overlay geometry is one figure per cell run, so stroking it outlines every run rather than the region — thousands of 75%-alpha edges a pixel apart, painting the selection solid. And no tint works: the spectrogram spans the whole colour ramp, so a wash light enough to keep the detail readable is invisible against the bright end, and one heavy enough to read obscures exactly the audio about to be repaired. Only the rectangle gets an edge and corner handles, because only for a rectangle are the bounds the shape.
- **`MainWindow` can now be rendered offscreen**, which it could not before, and doing it found three bugs no test could. The blocker was that `Icon="/Assets/wavelab.ico"` and the font URIs in `Theme.xaml` were *bare* pack paths, which resolve against `Application.ResourceAssembly` — whatever host happens to be running — so they silently failed outside the app itself. Both now name the assembly (`pack://application:,,,/WaveLab;component/...`). To render: set nothing, construct an `Application`, merge `Themes/Theme.xaml` into its resources by absolute pack URI, `Show()` the window offscreen, pump the dispatcher, then `RenderTargetBitmap`. **Do this before claiming any UI layout works.**
- **An offscreen render writes to the real settings file, and that once made the app look broken.** `MainWindow`'s ordinary close path saves its position, so a probe that parks the window at `-4000, -4000` and closes it leaves that in `%AppData%\WaveLab\settings.json` — and the next real launch opens where no monitor reaches. The window is there, focused, in the task bar, and invisible; it reads as a program that will not start, and because the position outlives the process it survives a restart *and* reinstalling an older build. `Util/WindowPlacement.IsReachable` now gates the restore **and** the save. The test is on the **caption band**, not the window: a window whose body is on screen but whose title bar is above it cannot be dragged back, so body overlap is not what makes a placement recoverable. It is measured against the *virtual screen*, not against zero — a monitor to the left puts the desktop's origin at a negative x, so negative is not by itself wrong.
- What that render found, none of it visible to a unit test — and no count belongs in that sentence, because a number here only rots and the point is that no quantity of them would have caught these. **`SegmentButton` inherited `Width="38"` from `ToolButton`** — a 38×38 icon square — so the view switch shipped in the previous commit read `Wa | Sp | Sp`. **The transport row could not fit the spectral actions**, which is why they moved to their own bar. And **the `ToolButton` template does not bind `Padding`**, so setting it on a text button does nothing; size those with `MinWidth`. (`AccentButton` had the same fault and was fixed when the convolution reverb row needed a button sized to its own text; `ThemeTemplateTests` now pins both — the one that honours padding and the one that does not.)
- `SpectralEditorView.HopFor` matches the analysis hop to what a column of pixels covers, in **both** directions. The configured hop is a starting point, not a ceiling: it used to be held at 512 however far the view was zoomed out, so a fit-to-window view of a whole side analysed tens of thousands of frames to fill a 1400-pixel image and showed nothing for **twelve seconds** — the feature looked broken at exactly the zoom a freshly opened file sits at. Halving and doubling from the configured hop keeps it a divisor of the transform length, which `Spectrogram.Analyze` requires.
## Mastering tier

- `Audio/Dsp/Crossover.cs` — Linkwitz-Riley, 24 or 48 dB/oct. The bands must **add back up**, which is the only thing that makes a multiband processor usable: a Butterworth pair sums to +3 dB at the crossing, an LR pair to unity. With three or more bands the naive tree stops summing flat because the low band leaves after the first split and never picks up the phase rotation the others do — each band is passed through an **all-pass matching every crossover it skipped**. Measured: 2–5 bands at both slopes sum flat within **0.09 dB**, each half is −6.02 dB at its crossing, rejection an octave out is 24.7 / 48.5 dB.
- `Audio/Dsp/Oversampler.cs` — polyphase windowed-sinc, 2/4/8×. Replaces `SaturationEffect`'s `mid = (x + prev) * 0.5`, which is not band-limited and therefore leaves images for the non-linearity to fold back down. Measured on the fifth harmonic of a 7 kHz tone (folding to 9.1 kHz): none −19.4 dB, the midpoint trick −29.8, this −135.0. **Two fractional-delay traps**: an even-length kernel centred at `(L−1)/2` has its symmetry point *between* samples, so centre on `L/2`; and the decimator must anchor on the **first** sample of each group, not the last, which otherwise adds `(factor−1)/factor` of a base sample. Together they had the round trip at 5.6 dB with latency reported 32 and measured 31; fixed it is 81.4 dB.
- `Audio/Dsp/Dither.cs` — the quantiser owns the rounding, because noise shaping feeds the quantiser's error back and there is no error until the rounding has happened. **The shaping curve is designed, not quoted**: the target magnitude is the ear's threshold of hearing (Terhardt), normalised so the mean of its log is zero — Gerzon-Craven says a monic shaper cannot do better, and a curve integrating to anything else asks total noise power to change — and the filter is its minimum-phase factorisation. A first version used coefficients recalled from the literature with the sign that seemed natural and **raised** 1–5 kHz by 2.7 dB. Measured: the strong curve takes 1.3–1.9 dB out of every band below 10 kHz and returns +9.7 (10–15 kHz) and +15.5 (above); wideband rises 11.3 dB, as it must.
- `Audio/Dsp/MinimumPhase.cs` — the cepstral factorisation, shared by the disc equalisation curves and the dither shaper.
- `Audio/Dsp/StateVariableFilter.cs` — TPT topology, for filters that are **modulated**. RBJ biquads are right when set once and wrong when moved: their delay-line state does not mean the same thing under new coefficients, so changing them while audio flows clicks. Measured: retuning across three octaves *every sample* gives a worst step of 0.077, and all eight modes are stable at Q 20. `MagnitudeDb` runs a tone through a copy of the filter rather than modelling it — a hand-derived transfer function is a second implementation to keep in step, and when the two disagree the drawn curve is the one believed.
- `Audio/Dsp/PartitionedConvolver.cs` — Gardner's uniformly-partitioned overlap-save: latency of one *block* rather than one kernel. Matches a direct convolution to 2.4e-7. It declares **zero** latency of its own — given a block it returns that block's output, and the buffering delay belongs to whoever accumulates blocks.
- Processors: **multiband compressor** (four LR bands, program-dependent release scaled by each band's crest factor — steady material gets the slow release that stays inaudible, percussive material the fast one that recovers between hits), **dynamic EQ** (SVF, detector band-passed at the band's own frequency so energy elsewhere cannot trigger it), **transient shaper**, **de-esser** (spectral, per-bin, gated on energy share *and* zero-crossing rate — either alone confuses a bright vowel with an "s"), **linear-phase EQ**. Measured: multiband ducks a bass note 14.5 dB while a 6 kHz tone moves 0.0; the dynamic band moves −6.5 dB on loud material in its range and 0.0 on quiet; a loud 5 kHz tone moves a 300 Hz band by 0.0.
- **The transient shaper's fast follower rises instantly and the slow one does not.** If both jump on the way up they are equal at every attack, the difference is zero exactly where the attack is, and the effect does nothing — measured, it moved a struck note by a factor of 1.00. The difference is taken in dB so a transient at −40 is shaped like one at −6, which is the whole point.
- `FilterEffect`'s 24 dB mode is now a proper fourth-order Butterworth — two sections at Q 0.5412 and 1.3066, which are pole positions and not a tuning choice. It previously scaled the user's Q by 1.3 and one stage by a further 0.8, which is flat at no setting. Measured: 12.1 and 24.7 dB/oct, and maximally flat at default resonance.
- **The stereo widener's two width controls are divided by `SPLIT FREQ` and by nothing else, which is worth stating because it was not always so.** The audit found `SPLIT FREQ` built and never used while `MONO BASS` doubled as the crossover, so a Stereo Width preset saved before that fix means something different now. `StereoWidthQualityTests` pins both halves: moving the split alone moves a 300 Hz tone between the bands by **14 dB**, and mono bass produces the same collapse at either end of the split's whole range. Wiring the crossover back to `MONO BASS` fails two of the tests.
- **The high band is the residual of the low one rather than a second filter**, so when both widths agree the pair reduces to a plain scaling and nothing at all happens at the crossover — measured flat to within 1% at the split frequency itself. A filtered low plus a filtered high would notch there, and a notch in the side signal is a hole in the stereo image at one frequency. Taking the full band as the high part instead fails six tests.
- **Haas mode is a comb, so it has nulls, and at the default 5 ms delay every multiple of 200 Hz is one** — 1 kHz among them, where the mode does nothing whatever `WIDTH` is set to. The side it makes is `mid − delayed(mid)`, which is what returns the mono sum exactly as it went in, and equally why a stereo source's own side is **discarded** rather than widened. None of that is visible in the UI, and it is the reason a quality test written at 1 kHz failed against perfectly correct code.
- Two smaller properties of the same effect. **At its defaults it does not write to the buffer at all**, so unity width is bit-exact rather than nearly so — worth keeping, since an effect that costs a rounding error per sample while doing nothing cannot be left in a chain. And **`PHASE SAFE` at zero is not a bypass**: it relaxes the side-to-mid energy bound from 0.95 to 8 rather than removing it.

## Vinyl restoration tier

- `Audio/Dsp/RecordingCurves.cs` — RIAA and the curves before it, as exact analytic transfer functions from their time constants, applied either way round. The filter is **designed to the curve**, not transformed from the analog prototype: a plain bilinear transform is wrong by a visible margin near Nyquist at 44.1 kHz because the map compresses the axis where the last time constant is still working. Measured against the analytic curve over 20 Hz–20 kHz, **minimum phase is out by 0.000 dB at worst** and linear phase by 0.021; record then playback returns the audio at 133 dB. Minimum phase is the default because the disc's curve was imposed by a minimum-phase network — built by folding the real cepstrum. Historical triples are the commonly published ones and the figures they derive confirm them (Columbia LP −15.6 dB at 10 kHz against a published −16; AES a 399.9 Hz turnover against 400).
- `Audio/Dsp/Azimuth.cs` — GCC-PHAT, whitening the cross-spectrum so every frequency contributes its phase alone; plain correlation is dominated by the bass, which carries the least timing information. Reduced across windows by **median**. **Do not refine the peak with a parabola**: whitening makes the correlation a sinc, and a parabola fits its crown badly — measured, it read a planted 0.25 samples as 0.135 and 0.40 as 0.268 while landing exactly on whole and half samples. Evaluating the true band-limited correlation between samples removes that; planted delays 0–12.75 samples come back within 0.035, and ~1.1 µs survives a correction. The correction splits the shift between channels so the programme does not slide in time.
- `Audio/Dsp/Interpolation.cs` — shared windowed-sinc reader behind azimuth and wow correction. Measured against analytic tones: −108 to −143 dB across the band, 5.6e-16 of DC ripple, and **~75,000× more accurate than linear** at 12 kHz.
- `Audio/Dsp/Decrackle.cs` — crackle differs from clicks in kind, not degree: a click is loud enough for a curvature test to see, crackle sits at or below the music. What separates it is that **music is predictable and dirt is not**, so detection runs on the high-order AR prediction residual. The threshold is in robust deviations of that residual per block; robust because the outliers being hunted would otherwise inflate the deviation used to find them. **A run longer than the limit is left alone deliberately** — a sustained departure from the model is a transient, and replacing it is what makes an aggressive de-crackler sound dull. Repair is Janssen. Measured: 39.1 → 64.6 dB at 0.05 amplitude, 100% of crackle found at 26.7 dB below the programme peak, two false detections per five seconds on clean material, transient intact at 38.1 dB.
- `Audio/Dsp/HumTracker.cs` — follows a drifting fundamental and **subtracts** an estimate of each partial rather than notching, so music at those frequencies survives (a note at 100.4 Hz moves 0.6 dB while the hum at 100.0 moves 14.8). **Two passes, not a tracking loop**: a causal loop lags a ramp, and the residue is exactly what stops the subtraction cancelling — a first-order one followed a drifting hum well enough to report the drift while leaving most of it in the audio, and adding an integral term made it hunt on steady material, taking 62 dB down to 20. Offline, measure the trajectory against a fixed reference, smooth it with **centred** filters that have no lag, then subtract along it. Measured per drift rate: steady 14.9 → 73.6 dB, 0.2 Hz of drift → 37.7, 0.6 Hz → 28.8, 1 Hz in ten seconds → 25.4.
- **Four things had to be right before that fundamental could be found at all**, each of which looked like something else. **Prominence is a ratio**, so in a quiet region the local median is tiny and noise clears any threshold — a 58.21 Hz candidate passed the "has a fundamental" gate on noise, then won by explaining a 233 Hz note as its fourth harmonic and 349 as its sixth; the background is now floored by the whole spectrum's noise. **A hum has consecutive low partials** — requiring the first *two* kills the subharmonic-of-music candidate. **A real partial is a local maximum**; the skirt of a loud neighbour is not, and leakage from a 349 Hz note was declaring a seventh harmonic at 350 that did not exist. And **the refinement searched one bin** where the coarse estimate can be four out, so it confirmed its own error. Both the initial and per-block estimates reduce by **median**, for the same reason azimuth does.
- **`HumRemovalEffect`'s AUTO DETECT gated on share of total energy, so it worked on hum and not on records.** A mains line 22 dB under programme holds well under a percent of the signal, and the gate wanted 2% — so it never fired on a real transfer, while hum with nothing over it cleared it instantly, which is what every test fed it. Measured on `demo_track.wav` with 50 Hz planted at −40 dBFS and MAINS left at its 60 default: **1.2 dB removed during playback and nothing at all on render**. It decides by **prominence** now — each candidate's fundamental and first two partials against three frequencies that are harmonics of neither mains, which is what the music alone is doing — the same conclusion `HumTracker` reached above, for the same reason. Same material: **24 dB removed on playback, 42 dB on render**, and the floor is a hum **52 dB under the programme**.
- **The analysis window is a fixed quarter of a second and not the caller's block, which was the other half of it.** Resolution is one over the window length, so a 10 ms buffer cannot separate 50 Hz from 60 at all, and the 1.37 s block `MasterSection.ProcessOffline` uses yields too few windows for anything to converge: **preview and render were different processors**, reporting 50.5 Hz and 60.0 Hz for identical settings. The probes accumulate a sample at a time, so no buffer is held and no cost arrives in a burst on the audio thread.
- **The answer is voted, never smoothed.** Moving the frequency 10% toward whichever candidate won each block put the notch bank at **54.4 Hz** — a frequency no mains supply runs at, and between the only two it was choosing from, so the notches sat where neither the hum nor anything else was. Confidence is smoothed; the frequency is one of the two candidates or the manual setting.
- **Only the fundamental has to be prominent, and that is a deliberate departure from `HumTracker`**, which requires the first two partials. That rule is right where it is — a whole spectrum of candidates, so a subharmonic of the music has to be excluded — and wrong here, where there are two candidates and magnetically induced hum is often very nearly a sine. The partials still count toward the strength, so a comb beats a lone line; what guards against a sustained bass note near a mains frequency is that a candidate must hold for about three quarters of a second before confidence reaches the tuning, and hum runs for a whole side where a note does not. `CoefficientPublishingTests.HumRemovalRetunesWhenTheDetectorLocks` is what caught the stricter rule — it feeds a pure 60 Hz tone, which the two-partial version refused.
- `Audio/Dsp/WowFlutter.cs` — speed variation is **multiplicative**, and on a log-frequency axis a multiplication is a *translation*, so the whole spectrum slides and the slide between two frames is the speed ratio. Tracking individual partials — what the plan called for — needs partials sustained across the side, and music does not oblige; sliding the whole spectrum needs no sustained partial at all. Frames that do not correlate (a note change, a splice) are interpolated rather than believed.
- **The band and block length for wow are the uncertainty principle in plain sight.** Seeing a frequency move by a third of a percent takes ~300 cycles, which at 400 Hz is three quarters of a second — but wow is a few hertz, and such a window averages across most of a cycle of it. Measured, a 0.37 s window returned a planted variation at 0.55 of its depth at 0.8 Hz, 0.29 at 1.5 and 0.14 at 2: a low-pass, not noise. The escape is to measure **high** — 300 cycles at 4 kHz is 75 ms — hence a 1 kHz–8 kHz band and a short block. Also: **smooth the per-block shifts before integrating**, because integrating measurement noise is a random walk, and a walk in the position map is a slow speed error introduced by the tool meant to remove one (it took steady material to 2.9 dB; smoothing restored 27.0).
- **A time-base tool cannot be tested by a whole-file waveform residual.** Correcting a time base integrates a rate, so the constant of integration is arbitrary and what remains of the measurement noise leaves it slowly wandering; a good repair reads as a total failure. Measure in short windows, each aligned on its own, and take the median — and assert the remaining *wow* as the primary claim.
## Professional delivery

- `Audio/RiffChunks.cs` — the chunk model that makes a WAV survive a round trip. Import used to keep `fmt `/`data` and **silently discard everything else**, which is why `RequiresSaveAs` exists: a file carrying a broadcast timestamp, a producer's notes or another program's markers came back stripped. Unknown chunks are now retained verbatim on `AudioDocument.Riff` and written back in order. `Owned` is only `fmt `, `data` and `fact` — the three the codec regenerates. **`cue ` and `LIST` are deliberately not owned** even though this app writes them: a carried chunk has to survive being *read* as well, or a file's markers could never be imported at all, and `Set()` replaces it when markers are written. A single chunk is capped at 16 MB so a damaged length field cannot ask for an allocation the file cannot contain.
- `Audio/BroadcastMetadata.cs` — BWF `bext` (the fixed 602 bytes plus coding history), `LIST/INFO` tags, and `cue `+`adtl`/`labl`. `TimeReference` is **64-bit** and stored as two 32-bit halves; reading it as one 32-bit value wraps after 27 hours at 44.1 kHz, which is a working day of a tape's timeline. Markers now embed in the file, so they reach other programs; `MarkerStore.FromRiff` reads them back when there is no sidecar. **The sidecar still wins where both exist** — it carries regions and CD track order, which cue points cannot express, so preferring the file's own marks would silently drop the richer set.
- `Audio/Dsp/LoudnessCompliance.cs` — measure a document against EBU R128, ATSC A/85, Apple, streaming or CD targets and report pass/fail per criterion. The suggested gain is `min(what the target wants, what the true-peak ceiling allows)`: a quiet master that needs +9 LU but has only 4 dB of headroom gets 4, because a report that recommends clipping is worse than one that says the target is unreachable.
- `Audio/DdpImage.cs` — DDP 2.00 image sets: `IMAGE.DAT`, `DDPID`, `DDPMS`, `PQDESCR`, `CDTEXT.BIN` and the image's MD5. A cue sheet plus WAVs is what a *duplicator* takes; this is what a *pressing plant* takes, and the difference is formality rather than quality — PQ offsets, catalogue numbers and CD-TEXT in files the plant's systems read, plus a checksum that travels with the audio.
- **A DDP image is big-endian**, which is the opposite of a WAV's byte order. Getting it backwards produces a file of exactly the right length, full of noise, that no length check and no checksum would catch — only listening. `TheImageIsBigEndian` plants a known value and reads the bytes.
- Two more DDP invariants worth keeping. **Every track is padded up to a whole CD frame** (588 samples): a track that does not fill its last frame pushes every track after it off the grid, and a CD cannot represent that. And **PQ offsets are stated from the two-second lead-in, not from zero** — that is where the plant's timeline begins, so a sheet starting at 00:00:00 puts every track two seconds early.
- `CdTransfer` resamples **once** into one continuous CD-rate stereo programme and cuts both deliverables from it. Converting each track separately would give every boundary its own filter transient, which is exactly the gapless transition the sector alignment exists to protect. Both exporters stage into a uniquely-named subfolder and move files into place only when the whole set is complete, so an interrupted export cannot leave a folder that looks like a deliverable.
- `CdTrackPlan` carries performer, songwriter, ISRC and pre-emphasis for the DDP path, and the PQ sheet editor below fills all four (`CdTransferDialog.TrackRow.ToPlan`). **What the user has not typed stays absent rather than becoming something invented** — a blank performer is written as blank, not defaulted from the disc — because a plant reads the sheet as a statement of fact about the release.
- **A drive reports where each track starts and says nothing about where any of them ends, so every end in `CdAudioService.CreateDisc` is inferred — and one of those inferences is a heuristic that can be wrong.** An audio track followed by a data track is the session boundary of a CD-Extra disc: session 1's lead-out (90 s), session 2's lead-in (60 s) and the following pregap (2 s) lie between them, **11,400 sectors** of which not one is audio. Charged to the audio track they make `ExtractTrack` read past the programme, and the drive refuses the read. **The heuristic is that every audio-to-data transition is that boundary**, and a single-session disc whose data track sits last — rare, but legal — presents an identical table of contents, so it loses **2 min 32 s** off the track before the data. Telling the two apart needs the full-TOC session data `ICdAudioDevice` does not expose; `ADataTrackLastOnASingleSessionDiscCostsTheAudioBeforeIt` states the cost rather than leaving it to be found.
- **The lead-out carries a control field like any other descriptor, and on some discs its data bit is set.** Read as an audio-to-data transition it would take the same two and a half minutes off the last track of a perfectly ordinary album, which is why the rule tests `!next.IsLeadOut`.
- **`ICdAudioDevice` is the seam the CD code is testable through, and `CdTableOfContentsTests` is the first thing to use it** — a synthetic TOC, and sectors filled from their own addresses so a decoded document can be traced back to the sectors it should have come from. That is what makes the extraction test worth more than a length check: the reads have to be contiguous, in order, stop at the boundary, and carry the right audio. Both halves were checked by mutation rather than assumed — removing the subtraction fails four of the six tests, dropping the lead-out guard fails one. **What no synthetic TOC reaches is `WindowsCdAudioPlatform`** — the reusable unmanaged scratch buffers and completion event in `InvokeIoControl` sit below the seam — and **a full disc rip has now been done and it works**, so they are exercised rather than merely written. That is the only evidence there will be for that layer: it is real `DeviceIoControl` against a real drive, so nothing in the suite reaches it and nothing can regress-test it. Treat a change to `InvokeIoControl` as needing another rip.
- **RF64/BW64** (`WavContainer`) lifts the 2 GB ceiling. Plain RIFF states every size in 32 bits; RF64 writes `0xFFFFFFFF` in those fields and puts the real values in a `ds64` chunk that must come **first in the form**, because every escaped size after it is meaningless until it has been read. Only `data` has a field of its own there — anything else that escapes has to be named in ds64's table, and the reader refuses a file that escapes a size without stating it. The step-up happens at **2 GB, not 4**: many readers take a RIFF size as *signed*, so that is where a plain WAV stops being safely readable, and `WavContainer.Automatic` steps up exactly there. A ds64 table claiming more entries than the chunk can hold is refused rather than walked, or the reader reads sizes out of the audio.
- `RiffMetadata` now serves both containers. AIFF is the same shape — four-character id, length, even-padded payload — with big-endian lengths and different audio chunks, so `ForAiff()` sets the byte order and the owned set (`COMM`/`SSND`/`FVER`; `FVER` is owned because it declares an AIFF-C version and the writer emits classic AIFF). **A codec carries chunks only from its own kind** (`IsAiff`): a WAV's `cue ` is not an AIFF chunk. Marks still cross between the two, because they are rebuilt from the marker list rather than copied as bytes. Writing an AIFF from a WAV's metadata object was a real bug — it wrote little-endian lengths into a big-endian file, and every chunk after the audio became unreadable.
- **A classic AIFF no longer sets `RequiresSaveAs`.** Its chunks survive, so it can be written back over itself. AIFF-C still does: the writer produces classic AIFF PCM, so saving in place would replace the container as well as the audio.
- `Audio/AiffMetadata.cs` — `MARK`, `COMT` and the text chunks. A mark's name is a **Pascal string**: a leading count byte, with the count and characters padded together to an even length. A C-string reader over one takes the count for the first character and then runs to whatever zero it finds; `MarkNamesArePascalStringsWhateverTheirLength` asserts the *following* mark is still found, which is what breaks when the padding rule is wrong.
- **`SnapshotDoc` shares `Riff` with the document, and the save paths pass a marker snapshot.** Neither was true when the chunk model first landed, so nothing it preserved ever reached an actual Save — the codecs were correct and the UI never handed them anything. The codecs clone before touching a chunk, so sharing rather than copying is safe.
- **`LIST` is not one chunk.** A WAV's information tags and its cue-point labels are both `LIST` chunks, told apart only by the four-character type in their first four bytes (`INFO` / `adtl`). A store keyed on the chunk id can hold one of them, so writing a file's markers would silently delete its tags and writing its tags would delete its marker labels. `FindList`/`SetList`/`RemoveList` are the only way anything touches a `LIST`.
- `Audio/FileTags.cs` — the one tag model behind all three containers. AIFF is the poorest of them: title, author and free annotation, with nowhere for an album, a track number or a genre, so those are folded into `ANNO` as `Album: …` lines rather than dropped. A round trip through a weaker container should lose formatting, not facts.
- `Views/FileInfoDialog` (File ▸ File Information…, Ctrl+I) — Description / Broadcast / Chunks. **The third tab is a read-out, not an editor**: the claim that a file's metadata survives is one a user otherwise has no way to check, and listing what will be written turns it from something to be believed into something to be looked at. The Broadcast tab is disabled rather than hidden on an AIFF document, so the reason it does not apply is visible. Editing marks the document with `AudioDocument.MarkMetadataChanged`, which sets `Dirty` and bumps `EditVersion` but deliberately does **not** raise `Changed` — no sample moved, so the peak pyramid and marker anchors have nothing to do. Nothing is written to disk by the dialog; the chunks go out with the next Save through the ordinary codec path.
- The CD transfer dialog is now the **PQ sheet editor** (design: `docs/design/delivery_metadata.png`): disc performer and UPC beside the title, a deliverable switch that replaced the separate export prompt, per-track performer/songwriter/ISRC/pre-emphasis, and a CD-timecode length read-out from `CdTransfer.PqSheet`. **Built with SOURCE IN/OUT kept and CD LENGTH added**, rather than the mockup's derived START and LENGTH columns — the mockup would have removed typed boundary editing, which the dialog already had. `PqSheet` and the image writer agree by construction and a test asserts it, because a sheet describing a different disc from `IMAGE.DAT` is the one error a plant cannot catch.
- **Rendering those two dialogs offscreen found three bugs, as it did for the spectral editor.** `SOURCE IN`/`SOURCE OUT` at 92 px cut `00:03:20.000` to `00:03:20.`; the footer at 230 px cut `Export DDP Image Set…` to `…Set.`; and — the app-wide one — **the `TextBox` style had no disabled state at all**, so the catalogue fields in WAV+CUE mode looked exactly as live as the editable ones. A disabled field that looks live is a UI that lies. Render before claiming a layout works.
- ISRCs are normalised on commit, not only on export. A user typing `GB-AAA-24-00001` is typing the same code; leaving the punctuation on screen while writing the bare twelve characters into the file shows something the deliverable does not say. `Isrc.Advance` moves only the last five digits — the designation code, the only part that changes between tracks on one release — and **refuses to roll past 99999** rather than carrying into the year of reference and numbering somebody else's releases.
- `Audio/Id3v2.cs` — ID3v2.4 on MP3 export, prepended to the encoder's output on the *staged* file so a failure leaves the destination untouched. Sizes are **synchsafe** (seven bits a byte, top bit always clear) in the header *and* every frame — 2.3 used plain 32-bit frame sizes, and a reader assuming the older form finds every frame past the first at the wrong offset. Text is UTF-8, which is the reason to write 2.4 rather than 2.3 at all. An existing tag is **replaced, not stacked on**: the first tag's length carries a decoder over the second, so the second becomes audio. The tags come from the document's own `FileTags` — File ▸ File Information… edits them and `ExportDialog` reads them back out of `doc.Riff` — so what an MP3 says about itself is what the WAV said, carried across. **Only the title falls back to the output file name**, and only when the document has none, so an untagged file still produces an MP3 that identifies itself. The writer emits nine frames; `FileTags.ToId3` supplies seven of them, and `TPE2`/`TCOM` (album artist, composer) have nowhere to come from because the dialog has no field for either.

## Audio montage

- `Audio/Montage/` — a **single-lane** clip timeline: sources, clips, a renderer, a JSON store. Not the multitrack rewrite `docs/ROADMAP.md` declined; no mixer, no bus, no send, no automation. What one lane buys is that **an overlap is unambiguously a crossfade**, which is what makes the join measurable.
- **The crossfade law is the whole of it.** Between two unrelated pieces the powers add, so the gain pair must satisfy `a²+b²=1` or the join dips 3 dB; between two takes of the same thing the amplitudes add, so it must satisfy `a+b=1` or the join bumps 3 dB. Both are one equation with the correlation left in — the sum's power is `P(a² + b² + 2abρ)` — so the incoming shape is chosen freely and the partner is the root of a quadratic: `b = −ρa + √(1 − a²(1 − ρ²))`. Equal power at ρ=0, equal gain at ρ=1, **exact in between rather than an interpolation between the two familiar answers.** Measured: −9.03 dB before a join, −9.02 through it, −9.03 after, for unrelated material *and* for two takes of the same recording; a fixed law is 2.3–3.0 dB out.
- **The classic 3 dB assumes a symmetric pair.** Here only the *partner* is solved and the incoming curve keeps the user's shape, so a sine incoming gives 2.3 dB where a linear one gives the textbook 3.01. Both are the same phenomenon; the tests assert both.
- **`MeasureCorrelation` floors at zero; `MeasureSignedCorrelation` does not, and the difference is load-bearing.** The law cannot use a negative value — the compensation it asks for does not reach zero at the end of the overlap, so it stops being a fade — but a clamped zero means *either* "unrelated", the ordinary case, *or* "these cancel", which needs a polarity fix. Reporting them as one number **flagged every good join between two unrelated pieces as a polarity fault**, which is what the offscreen render showed.
- **A straight line in decibels never reaches zero**, so no fade curve is both exactly dB-linear and exactly silent. `DecibelLinear` picks silence and tracks the line to within 0.5 dB down to −35 dB (`Fades.DecibelLinearTo`), diving faster below that. A piecewise curve could hold the line further, but a kink partway down a fade is worse than a tail that accelerates smoothly.
- **Do not measure crossfade flatness on noise.** A 512-sample window of noise wanders ~0.2 dB on its own and about a dB peak-to-trough over a hundred windows — the same size as the error being hunted. The first version of that test measured the noise and reported it as the law's; the flatness claims rest on tones.
- Sources are brought onto the montage's rate and channel count **once, at load**. Import normalises neither anywhere else in the app, and a clip used twice would otherwise be resampled twice. A mono source is copied to both sides; a stereo one is **averaged, not summed**, into a mono montage, or the first stereo file dropped in would clip on arrival.
- An overlap **overrides the clips' own fades**, which describe free edges. Three-way overlaps are warned about and resolved pairwise rather than guessed at. The render's peak is **reported, not clamped** — overlapping clips can sum past full scale and silently limiting hides the one thing to fix.
- The montage file holds **decisions, not audio** — 2584 kB of audio described by 486 bytes — with paths relative where a relative form exists. A source that has gone missing is named and **still holds its place in the index**, or every clip after it would silently re-point at the wrong audio.
- `ViewModels/TabViewModel` — three members wide (`Title`, `IsDirty`, `Kind`), because a document and a montage have almost nothing in common. `MainViewModel.Documents` is now `ObservableCollection<TabViewModel>`; **`ActiveDocument` stays typed to a document and is null whenever the active tab is not one**, which is what keeps forty-odd audio commands from having to ask what sort of tab they are looking at — they simply become unavailable. `AudioDocuments` is the "every open file" projection.
- Four traps in that widening, all found before they shipped. The tab strip must bind **`ActiveTab`, not `ActiveDocument`**: `SelectedItem` is typed `object`, so selecting a montage would push a value the document setter cannot take, the binding would fail silently, and the editor would keep operating on the tab just left. `CloseTabCommand` must be `RelayCommand<TabViewModel>`, because `RelayCommand<T>` hard-casts its parameter and would throw on **every requery**, not merely on click. The close-tab reselection walks *tab* order, so closing a file lands on its neighbour rather than skipping a montage. And `DocumentViewModel.Title` no longer appends `" •"` — the strip already draws an amber dot from `IsDirty`, and it was said twice.
- `Views/Controls/MontageLaneView` follows the `WaveformView` discipline — waveforms in a `WriteableBitmap` behind a paint key, everything that moves under a drag as vectors. A montage repaints on every pixel of a clip drag, so waveforms in the moving layer would repaint the whole side at mouse rate. **The crossfade zone is drawn between the clips, not on them**, and its label sits *under* the lane: at the top it lands on the clip header and hides the name of the clip it belongs to. Clips are tinted with their **source's** colour, or two clips cut from the same take butt into what looks like one clip and the boundary about to be dragged is invisible.
- The zero-crossing snap is applied **when the edge is let go, not during the drag** — snapping live makes the clip jump between crossings under the pointer, which reads as the drag fighting the hand. It looks for a *rising* crossing specifically; any near-zero sample would do in a quiet passage and the edge would wander.
- Rendering to CD or DDP is nearly free: a montage **is** an ordered set of ranges, which is what Phase 3's packager takes. One CD track per clip, and a crossfaded boundary belongs to the track it leads into — where a listener would say the next track begins.

## VST3 hosting

- `Audio/Vst3/` — hand-written interop, **no new dependency**. VST3 is a C++ vtable ABI shaped like COM: an `FUnknown` with the same three slots and signatures as `IUnknown`. It is dispatched through `delegate* unmanaged[Stdcall]` function pointers rather than CLR COM interop, so the layouts are written down in one place where they can be checked, and no RCW lifetime rule sits between this app and a plugin — an RCW finalised on a background thread would call `release` from somewhere a plugin has no reason to expect.
- **Windows only, which settles one thing**: a `TUID` is sixteen raw bytes, and on Windows the SDK lays them out to match the COM `GUID` struct while elsewhere it uses a flat big-endian order. The identifiers in `Vst3Abi` are the Windows form.
- **Vtable slots are counted, not guessed, and getting one wrong is an access violation rather than a wrong answer.** `getClassInfo2` is slot 7 on `IPluginFactory2` — three `FUnknown` slots plus the four inherited from `IPluginFactory`. Slot 6 is `createInstance`, and calling that with an index where a pointer belongs dereferences the index. That was the first thing the probe found.
- **A struct laid out wrongly is memory corruption.** `AudioBusBuffers` has a 32-bit field followed by a 64-bit one, so the compiler puts the second at offset 8 and the channel-buffer pointer at 16; packing it tightly shifts that pointer by four bytes and hands the plugin an address that is not one. `Vst3Tests` asserts every structure's size and the two offsets that matter.
- `kResultTrue` is **0** and `kResultFalse` is **1**, so a plugin answering "no" returns 1 rather than an error. Testing a result for non-zero reads a legitimate refusal as a failure; everything goes through `Vst3Abi.Ok`.
- `Vst3HostContext` and `Vst3MemoryStream` are managed objects exposed **to** native code — a vtable of `[UnmanagedCallersOnly]` statics, and for the stream a two-pointer object carrying a `GCHandle` so the callbacks can find their way back. The stream is what plugin state persistence needs, and what carries the component-to-controller handover.
- **The handover is a host obligation.** VST3 splits a plugin so processor and controller can live in different processes, and they start knowing nothing about each other; the host reads the component's state and gives it to the controller. Skipping it leaves a controller showing defaults for a processor that is not at its defaults.
- **Scanning runs out of process** — this same executable re-run with `--vst3-scan`, so one binary and the scan exercises the code the host will use rather than a parallel copy. A plugin that faults during initialisation, which is exactly when a broken install does, would otherwise take the app and every unsaved document with it. Results are cached against the binary's timestamp, and a plugin that crashed the scanner is **not retried** until it is reinstalled — retrying on every start makes one bad plugin a permanent tax on launching. `Program.Main` runs ahead of WPF because `StartupUri` refuses null and a scanner wants no message loop for a plugin to post to.
- **Report the Win32 code, not a guess.** "Wrong architecture" for what is actually a missing dependency (error 126 against 193) sends a user looking for a 32-bit build that does not exist. Adobe's two internal plugins fail exactly this way outside Audition.
- **Measured against 22 real plugins**: all 22 load, enumerate their classes, instantiate, configure at 44.1 kHz stereo and process a block finitely. Latencies read 0 to 5120 samples, and a 512-sample block through a plugin reporting 5120 correctly comes back silent — that is the latency, not a fault.
- **All 22 report zero host-visible parameters *and* no editor, and that is the plugins, not the host.** Five hypotheses were eliminated in order: a wrong `IEditController` identifier (disproved — the factory matched it and returned an object), a wrong vtable slot (disproved — the controller class id read from `getControllerClassId` matches the factory's own listing exactly), a null host context (disproved — a real `IHostApplication` changed nothing), a missing component-to-controller state handover (disproved — `setComponentState` answers `kNotImplemented`), and a missing `IComponentHandler` (disproved — accepted, and still nothing). These controllers implement `initialize` and essentially nothing else; the plugins can only be operated from the software they shipped with. **The editor and parameter code was therefore written but unverifiable here** — it needed a plugin that publishes something, and it now has one: see the VST3 parameter section below, where a synthetic plugin built in-process closes it. It is still exercised end to end as far as it can be: all 22 open as rack effects, configure, process and round-trip their state, and each one's card carries the note explaining that there is nothing to adjust.
- `Vst3PlugView` + `Vst3PlugFrame` + `Views/Controls/Vst3EditorHost` — the editor path. A VST3 editor is a native window wanting an `HWND` parent, so `HwndHost` is the seam; a plain `STATIC` child window is created rather than a class of this app's own, because the plugin owns everything inside it and a custom class would mean a window procedure with nothing to do. **The order on the way out is load-bearing**: detach the view, *then* destroy the window. A plugin whose window disappears underneath it is drawing into a handle that no longer exists and will not find out until it faults. `IPlugFrame` is not optional either — a plugin with a collapsible panel resizes itself while open, and without a frame it has nowhere to ask.
- `Vst3ComponentHandler` is a host obligation, not a courtesy: `performEdit` is the only way a host hears that a user moved something in a plugin's own editor, and `restartComponent` is the plugin saying its latency has changed. **Nothing raised on a host callback may throw** — the exception would unwind through a C++ frame that has no idea what a managed exception is — so every one catches and answers `kResultFalse` instead.
- **A plugin with no editor is not an error, and the window says so.** Showing an empty frame would read as a broken host; the fallback names the situation and points out that the audio still processes.
- **`Audio/Effects/Vst3Effect` puts a plugin in the rack as an ordinary effect** — numbered card, bypass LED, move, reset, remove, saved into chain presets with the built-ins. It **cannot derive from `EffectBase`**, whose constructor reads `Params` to size its value store, because a plugin's parameters are not known until it has been loaded and asked; so there is no local copy of the values at all — `GetParam` reads the controller and `SetParam` writes it, which is the only way to stay in step with a user turning knobs in the plugin's own editor. Type id is `vst3:<path>`, so a preset naming a plugin this machine does not have loses that slot and keeps the rest of the chain.
- **Setting only the controller is the failure that looks like success.** `setParamNormalized` moves the plugin's own display and nothing else; the processor hears a parameter only through an `IParameterChanges` carried into `process`. Without `Vst3ParameterChanges` a rack slider moves, the plugin's editor agrees with it, and the audio does not change. The list is **coalesced per parameter, not queued** — a block is a moment as far as a parameter is concerned — which is what lets the whole structure be fixed at construction, one slot per parameter filled in place, so the audio thread allocates nothing and takes no lock. Its native objects are plain memory with a vtable rather than managed objects behind a `GCHandle`, because everything a plugin can ask a value queue is a field.
- **A plugin's parameters are not its state.** Anything with an impulse response or a wavetable keeps far more than a host can see, and the plugins here publish no parameters at all — so `EffectState.PluginState` (base64 of `component->getState`) is the only thing a preset carries for them. Restored **before** the individual parameter values, not after, or the state undoes every one of them; and immediately rather than deferred, except for an A/B clone, whose deferred state would otherwise be written into the live plugin playing the other side of the comparison.
- **A/B snapshots share the plugin and count references** (`Vst3PluginRef`). Cloning a plugin the ordinary way would load the binary again, allocate its buffers again and pay its CPU twice for a snapshot that is never played; sharing gives A/B what it actually wants, which is a snapshot of the settings. `MasterSection` now **retires** effects that leave the chain — outside `_chainLock`, because releasing a plugin terminates somebody else's code and the audio callback would be waiting on it.
- **An editor window must be closed before the rack lets go of its plugin.** `MasterSectionViewModel` raises `EffectRemoving`/`ChainReplacing` while the effect is still whole; afterwards the window would be holding a released controller. Removal, preset load, rack reset and app exit all go through them.
- **The scanner is proved before any plugin is judged by it.** `ScannerPath()` is not simply `Environment.ProcessPath` — under `dotnet WaveLab.dll`, or a probe, or a test host, that is not this app and does not understand `--vst3-scan`. A scanner that cannot run fails every plugin identically and indistinguishably from a plugin fault, which is recorded as a crash and *never retried*: one bad refresh permanently condemned all 22 plugins on this machine. `ScannerRespondsAsync` asks once per refresh, on a path that cannot exist, and a refresh that fails it records nothing at all.
- **Rendering the manager and the rack offscreen found three things**, as it always does. A plugin names itself and some names are long, so the rack card's name ran under the power LED and was cut mid-glyph — it now trims with an ellipsis and carries the full name as a tool tip. `AccentButton`'s template dropped `Padding` on the floor, so a button sized to its text came out cramped. And the scanner's "no host-visible parameters" note was drawn in amber on a plugin that works perfectly — colouring a fact like a fault teaches the user to distrust the colour.

- Theme: dark studio, teal accent #3FD6C2, Inter + JetBrains Mono embedded from `Assets/Fonts` (referenced as `#Inter` / `#JetBrains Mono`).
- **The type scale is six steps and the dialogs are snapped to it**: `LabelSize` 9 · `SecondarySize` 10.5 · `BodySize` 12.5 · `TitleSize` 14 · `SectionSize` 18 · `DisplaySize` 26, declared as `sys:Double` in `Theme.xaml`. Before this the dialogs used **twenty** sizes on a half-point continuum where 10.5, 11 and 11.5 were all in heavy use and none was distinguishable from the others; a step is only worth having if two of them are obviously different. New markup picks a step, and does not invent 11.
- **There are two neutral ramps and both are named.** `Text`/`TextDim`/`Muted`/`Faint` are the true greys; `TealBright`/`TealText`/`TealMuted`/`TealFaint` are the green-tinted set the record and restoration surfaces sit on. That second ramp is deliberate — those panels have a teal ground and a pure grey reads cold against it — so it is a token set rather than something to flatten into the grey it is not. `TextDim` is the *one* step between `Text` and `Muted`; the dialogs had grown eight unnamed greys across that gap, which is why "secondary text" did not match between two of them.
- **`UpperLabel` and `Card` live in `Theme.xaml` and nowhere else.** Both had been duplicated: `UpperLabel` was declared byte-identically in two dialogs and written out inline as three separate attributes in 57 more places, and the card was three styles under three names (`AnalysisCard`, `SectionCard`, `HardwareCard`) differing only by a pixel of radius or padding. Use the style; do not re-type `FontSize`+`FontWeight`+`Foreground` for a field label. `HardwareCard` survives only as `BasedOn="{StaticResource Card}"` plus the margin that stacks them.
- **Dialog chrome: header bar `Padding="20,15"`, footer bar `Padding="20,14"`.** These were the majority values among fourteen structurally identical headers (which used 14, 15 and 16) and twelve footers (which used five different paddings). Neither is on a 4 px grid and neither was worth churning 639 spacing declarations to fix — but new work should reach for a 4 px step. The `14,8` / `14,7` / `14,6` bars are item-template **row separators**, a different component; leave them alone.
- **Colour goes through a token.** After the audit 0 literals restate a colour the theme already names, and the dialogs are at 12% literal (from 29%). What remains is genuinely one-off — an amber warning well, a couple of gradient stops. A `Color="…"` attribute needs a `Color` resource (`PanelColor`), not a brush; only brush-valued attributes take `{StaticResource Panel}`.
- Custom window chrome (`WindowChrome`, CaptionHeight 40); menus/combos/sliders fully re-templated in `Themes/Theme.xaml`.
- David wants to SEE UI mockups as pictures before UI changes are coded — render HTML mockups (docs/design/) and get approval first.
- **`Audio/Dsp/ConstantQ` is a constant-Q analysis — every octave gets the same number of bins** (design: `docs/design/constant_q.png`). The log *axis* already shipped and is the default; this is the other half, a genuinely different transform rather than a redrawing. Computed the **Brown–Puckette** way: each bin's windowed exponential is transformed once up front and thresholded to a handful of significant values, so one FFT of the frame multiplies against every bin's sparse kernel at once and the cost is one FFT plus a few thousand multiplies per frame however many bins there are. **The window is capped at 16384 and that is the honest limit**: true constant-Q at 36 per octave wants Q·fs/f, which at 30 Hz is 1.7 seconds for one row of one frame, so below about 140 Hz it degrades to a fixed-window analysis — which still resolves 2.7 Hz, so 50 Hz and 60 Hz mains and every harmonic of either stay separated. Measured: full scale on a bin centre reads **0.00 dB**, worst scalloping **1.39 dB** against Hann's theoretical 1.42, and 50 against 60 Hz shows a **22.8 dB** valley between them.
- **Only the picture switches.** The magic wand and every repair keep analysing linearly, because `SpectralMask` lives in the *repair* grid rather than in whatever the display was drawn from — which is exactly what lets the display change without the edit changing. `SpectrogramData.BinFrequencies` (null for the linear analysis) and `BinForFrequency` are what let a geometric analysis travel through the same image code: it asks the data where a frequency falls rather than assuming a bins-per-hertz constant.
- **The scale switch is docked right, with the readout, not left with the actions.** Rendering it found the left of the spectral bar had run out of room at 1800 px — the whole toolbar overflowed and the selection readout fell off the end. It also describes the picture rather than acting on it, so the right is where it belonged anyway.
- **Reflection onto an internal constructor is a trap this repo already stepped in.** `SpectrogramImageTests` built `SpectrogramData` by `GetConstructors(...).Invoke(...)` despite the test project seeing internals; the first time that constructor gained an optional parameter, thirteen tests failed with a parameter count mismatch over a change that could not break a direct call. Call it directly.
- **`Audio/Dsp/RobustPca` is principal component pursuit — the plan's sparse + low-rank de-crackle refinement — and it is NOT wired into de-crackle, because measured against the autoregressive residual detector it loses badly.** Scored per event on tonal programme with 400 known ticks: the AR residual caught **95.0%** of them while flagging 6.3% of the timeline; the RPCA sparse layer caught **44.5%** while flagging 34.8%. Less than half the events for five and a half times the collateral. Two reasons, both structural. The decomposition separates **sustained from transient**, which is not the same question as clean from damaged — a struck note is brief and broadband too and lands in the sparse layer beside the crackle. And a spectrogram frame is 256 samples where a tick is one to six, so the frame is two orders of magnitude too coarse to localise one. The plan's own hedge was right; the measurement says *behind* the AR+Janssen path and not helping it.
- **The decomposition itself is exact and worth keeping.** Built as low-rank plus sparse and asked for both back, it recovers each to within 1e-5 with the rank found rather than dictated — and the rank grows only when the previous step used everything it was given, so a genuinely rank-3 matrix never pays for rank 40. Its limit is recorded too: exact recovery needs rank small against the smaller dimension, and on a 60×90 matrix rank 6 recovers exactly while rank 12 is reported as 27. Past there it degrades rather than breaks — the layers still sum to the input, so the answer is a worse split and never a wrong one.
- **`Audio/Dsp/RandomizedSvd` exists because .NET ships no SVD** and a full one computes hundreds of singular values to use a dozen. Halko–Martinsson–Tropp: sketch, orthonormalise, decompose the small matrix. **Power iterations are what make it work on audio** — a spectrogram's spectrum decays slowly, and raising it to an odd power drives the wanted directions apart without moving the singular vectors. Each pass is re-orthonormalised **twice**, because the point of raising the spectrum is that it also destroys the columns' numerical independence. Writing its test found a trap worth remembering: **a matrix built from random non-orthogonal factors does not have those factors' scales as its singular values**, so the first version measured how non-orthogonal the generator happened to be and reported 9.95 for a "10" that was never there — and a truncation better than the theoretical optimum, which should have been the giveaway.
- **`Audio/Wave64Codec` reads and writes Sony's Wave64**, and the importer sniffs the opening GUID rather than trusting the extension — the format is written to `.wav` by more than one application, and the two containers share nothing but their samples. **Two counting traps, both easy to get backwards**: a chunk's stated size *includes* its own 24-byte header where a RIFF chunk's counts only the payload, and chunks pad to **eight** bytes rather than two. Get either wrong and the file opens in its own writer and nowhere else, which is why the tests walk the bytes and check the offsets rather than only round-tripping. The identifiers are not arbitrary either: the first four bytes of each GUID are the chunk's ASCII name little-endian, and a test asserts it, because a constant that was transcribed rather than understood looks exactly like one that was not.
- **The frame encoder is shared with `WavCodec`** (`WavCodec.EncodeFrame`). The two containers disagree about how to describe where the samples are and agree exactly about what a sample is, so the byte in the file comes from one place — and a test asserts a Wave64 file and a WAV of the same audio decode identically, which can only mean something if it is shared. RF64/BW64 remains the default for long WAVs; Wave64 is for what will read it, not for its length.
- **`ConvolutionReverbEffect` is the one built-in whose identity is a file, so it has the one rack row that is not a slider** (design: `docs/design/convolution_reverb.png`). Three states, and the third is what the design is for: a preset stores the path, so a rack moved between machines arrives naming a file that is not there — the row keeps the name, says so in amber, and offers to find it, rather than silently loading nothing and leaving the reverb apparently on and inaudible. `SetResponse` therefore drops the audio without dropping the path, which is why `ResponseMissing` can be told apart from never having chosen one. Not a button: a control reading only "Load…" says what you may do and never which room you are in.
- **The response is normalised to unit power, not unit peak.** Impulse responses in the wild differ by tens of decibels for reasons that have nothing to do with the room, and matching peaks would make a dense hall quieter than a sparse one because a dense one spreads the same energy over more samples. Power is what makes the mix control mean the same across two files — tested by running a sparse response and a dense one of equal power and requiring them out within 1.5 dB; they measure 0.0 apart.
- **`EffectState.PluginState` became `EffectState.State` behind `IEffectState`**, because a plugin's opaque bytes and a reverb's choice of file are the same problem: a preset stores parameters as a dictionary of doubles and neither of those is a double. **The JSON name is still `PluginState`** via `JsonPropertyName` — renaming it would have quietly dropped the state out of every preset already saved.
- **A caption wider than the control it sits in is cut mid-glyph at both ends**, which reads as a drawing fault rather than as text that did not fit. The rack's render buttons are about 150 px each and carried 28-character mono captions; they came out as `NDOABLE · SELECTION OR FIL`. Captions now say the one thing their title does not — the sentence lives in the tool tip, where there is room — and every one carries `TextTrimming` so a later squeeze degrades to an ellipsis instead. Same fault, same fix, as the plugin name running under the rack card's power LED.

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
- `PeakStore.Rebuild` builds into fresh lists and publishes **one immutable `State`** — pyramid, document and version together; `DocumentViewModel.ScheduleRebuild` coalesces edit bursts. Waveform may show stale peaks for a beat after an edit — by design. Stale is fine; *mixed* is not, which is why the three used to be three fields and are now one.


- The band bitmap is repainted only when (view, size, ampZoom, peaksVersion, channels, doc) changes; playhead/cursor/selection/markers are overlays drawn on top. Don't put per-frame-changing state into that paint key.
- `WaveformView`/`OverviewBar` paint the wave per *device-pixel column* from `PeakStore` into a `WriteableBitmap` (Pbgra32, transparent background so the grid shows through), then blit it and draw overlays as vectors. **Do not go back to path geometry for the bands.** The old design cached a 2×-viewport `StreamGeometry` window and rebuilt it every ~0.5 screens of scroll — a rate of roughly `73.5 / samplesPerPixel` Hz, which is zero at both zoom extremes and maximal in between. That is what made playback stutter only at intermediate zoom, and wrapping it in a `BitmapCache` made each rebuild worse, not better.
- Invalidation in the view-following controls is **synchronous** (`InvalidateVisual` when `Dispatcher.CheckAccess()`). The playhead is driven from `CompositionTarget.Rendering`, which runs inside the render pass; posting the invalidate through `Dispatcher.BeginInvoke` pushed it into the next pass, and at `Normal` priority it also outranked the `Render`-priority meter/spectrum timers and `Input`-priority wheel events — which is why those felt starved whenever the waveform got busy.
- `WaveformView.ShowDiagnostics` (Ctrl+Shift+D over the waveform) overlays frame interval, render/paint ms, playhead advance and view scroll. Use it before theorising about smoothness.
- `WaveTheme.Text` caches `FormattedText` per thread, keyed by brush identity; only frozen brushes are cached. Pass a shared frozen brush, not a per-render `new SolidColorBrush(...)`.
- Saves are version-checked: `MarkSaved` only fires if `EditVersion` didn't change while writing.
- **`Audio/Dsp/BiquadCoefficients` is the one way coefficients reach the audio thread, and nothing else about a filter crosses at all.** A `Biquad` is five coefficients and two delay-line samples in one struct, and the halves belong to different threads: the parameter path decides the coefficients, the audio callback owns the state. Writing coefficients into a filter the callback is running is five unordered writes, so it can read `a1` from one cutoff paired with `a2` from another — and that pole pair can leave the unit circle, which is not a wrong sound but a latched NaN for the rest of the stream. `MasterSection.Read` holds `_chainLock` and `EffectBase.SetParam` does not, so the window was real on every knob in the rack. Publishing a reference is one atomic write; `Process` reads it once a block and copies the coefficients into filters it alone owns. **The copy is `CopyCoefficientsFrom` and not an assignment**, because carrying each delay line across the change is what keeps a live sweep click-free — swapping whole structs would be equally thread-safe and audibly wrong.
- The audit fixed `FilterEffect` and `EqEffect` this way and called the other seven a mechanical follow-up. They were not quite mechanical. **`SaturationEffect` sized its filter bank from the parameter path**, so the thread moving TONE could hand the audio thread a freshly-zeroed array; every bank is now allocated in `OnConfigure` and nowhere else. **`StudioEq` was never torn** — it locked around both the gain setters and `Process` — but that put a monitor on the render callback, and a monitor is not priority-inheriting, so a preempted setter could stall it for a scheduler quantum; it publishes a snapshot now and the lock is gone.
- **`HumRemovalEffect` publishes the request rather than the coefficients, and that asymmetry is deliberate.** Its auto-detector retunes the same notch bank from *inside* `Process`, so there are two writers and only one of them may win; making the audio thread the one that turns a tuning into coefficients leaves the parameter path touching no filter at all. Twelve notches is a dozen sine and cosine pairs, computed only when the tuning actually moves, and it allocates nothing — which the detector's position on the audio thread requires. `ReferenceEquals` against the last applied `HumTuning` is what decides "the parameters moved"; the drift test against `_appliedFundamental` is what decides "the detector moved".
- `CoefficientPublishingTests` pins the two halves that pull against each other: the copy must happen (a forgotten one is silent — the filter keeps running at its old tuning and merely sounds stale) and it must stay a coefficient copy. Its concurrency test can only fail, never prove; it is a backstop against someone writing straight into a live filter again. Two effects sit out the click test for stated reasons — the gate's job *is* a 60 dB step, and the delay drains two seconds of recorded echoes through the newly inserted loop filter. **A Q 35 notch takes about a second to ring down**, so a hum measurement taken straight after a retune reads the settling, not the depth: it reaches −152 dB given the time.

- **`tests/WaveLab.Tests/Wpf.cs` is how a dialog gets opened in a test, and none of its arrangement is incidental.** One STA thread carries one `Application` with the real theme merged **before** anything is constructed, because `InitializeComponent` resolves every `StaticResource` as it parses — a theme merged afterwards is a theme the dialog never saw. An `Application` is per-process, so two test classes each standing one up fail whenever xUnit runs them together, which is why `HelpDialogTests` no longer makes its own. And **test bodies are serialised by a lock held on the calling thread**: `Pump` pushes a dispatcher frame, a frame runs whatever else is queued, and without the lock another test's work executes inside the window this one is holding open. That was a one-in-six failure and it took three wrong diagnoses to find.
- **A broken binding does not throw.** The property keeps its default, the control looks plausible, and the only trace is a line in `PresentationTraceSources.DataBindingSource` that nothing is listening to — which is exactly why the audit could say nothing about whether any binding in this app resolves. `BindingErrors` listens and `DialogLoadTests` fails a dialog that reports one. Two things that cost time: the listener has to **filter by thread**, because several tests build WPF elements on threads of their own; and **an element built outside the tree it belongs in reports failures that are about the test rather than the app** — a bare `MenuItem` binds its content alignment to an `ItemsControl` ancestor it has not got, so it goes in a `Menu`.
- **Showing the window is the point; laying it out is not enough.** An unshown window lays its content out but never raises `Loaded`, and several dialogs do their real work there. They are shown offscreen and unactivated — except **`MainWindow`, which may never be driven this way**, because its close path writes its position to the real settings file.
- **`MainWindow` is testable after all, and the thing it was excluded for is now the test.** Its close path writes its position to the settings file, so `ShellWindowTests` redirects the app-data root and then asserts what the shell wrote: the size kept, `WindowLeft` and `WindowTop` **null**. Removing the `IsReachable` gate fails it. The startup path is safe to run there because a fresh sandbox has no session to reopen and no autosave to recover, and `Environment.GetCommandLineArgs()` under the test host carries no path that exists — checked, not assumed.
- **Pump priority is load-bearing, and getting it wrong looks exactly like a hang.** A dispatcher frame ends when its own callback runs, so anything queued *below* that priority is still waiting when the pump returns. `MainWindow` cancels its own `Closing`, does its shutdown asynchronously and queues the real close at **`ApplicationIdle`** — which sits under the `ContextIdle` the pump used, so the close never ran and it read as the shell refusing to shut down. The close wait pumps at `SystemIdle`; the wait is also in **time** rather than in iterations, because a tight loop of pumps outruns work happening on other threads.
- **`CommandPalette` closed itself twice, and opening it in a test is what found it.** Deactivation dismisses the palette, which is what makes clicking away work — but every other way of closing it deactivates it too, as Windows takes the focus back, so choosing a command re-entered `Close` inside the close already running and `Window.Close` throws on that. `App`'s dispatcher handler caught it, so the symptom was an error report in place of the command rather than a crash. The guard is a `_closing` flag set in `OnClosing`. The harness installs a dispatcher handler for the same reason the app does: an exception raised inside a window message reaches the dispatcher rather than the caller, and an unhandled one takes the test host down naming no test at all.

- **A real disc rip found three things, and the first was not a CD bug at all.** The drive dropdown read `WaveLab.Views.CdImportDialog+DriveRow`: the themed `ComboBox` bound `SelectionBoxItem` and `SelectionBoxItemTemplate` but **not `ContentTemplateSelector`**, and `DisplayMemberPath` reaches the closed box through the *selector*, so the box fell back to `ToString()`. The stock template binds all three; this one does now. Only one combo in the app binds that way, which is why it survived — every other one is filled with strings or carries an `ItemTemplate`.
- **The file tab row is `Auto` and must stay that way.** The strip scrolls horizontally once the open files outrun the window, and the scroll bar has to come from somewhere: in the fixed 36 px row it came out of the tabs, taking the usable height from 35 px to **18** and cutting every tab and every name in half. One CD is fourteen tabs, so a rip is what surfaces it. Measured in `ThemeTemplateTests.AScrollingTabStripKeepsItsTabsAtFullHeight`; the strip grows to about 49 px while it is scrolling and returns to 32 when it is not.
- **A slim scroll bar needs `MinHeight`/`MinWidth`, not just `Height`/`Width`.** What a style does not declare falls through to the built-in `ScrollBar` style, which sets a minimum from `SystemParameters` — so the horizontal bar reported `Height` 8 and measured **17**, and the nine pixels came out of whatever it was scrolling. Both directions are fixed now — `MinWidth` on the style for the vertical bar, `MinHeight` on the horizontal trigger — which took the tab strip from 49 px to 40 and made every other bar in the app the width the theme has always asked for. Rendered before and after on the help dialog first, because it is every scroll bar rather than one.
- **Close All Files asks once, not once per file** — the same bargain batch convert makes, and for the same reason: a CD import opens a tab per track and a dozen separate "close anyway?" boxes is an obstacle rather than a question. It is all or nothing, it goes through the ordinary per-tab close so marker flushes and playback release still happen, and it takes a snapshot of `Documents` first because closing mutates it.

## Audit fixes, August 2026

A module-by-module audit of all 179 source files. What follows is only the part that
**changes behaviour or contradicts something above** — the rest was tidying and is in the
five commits. Everything here builds clean and passes 1483 tests; `AuditRegressionTests`
pins the ones that had no coverage.

- **Every `Biquad` factory clamps its corner frequency to `fs * 0.49` and its Q away from zero, and that decision has moved out of the effects.** Four of the five effects with a frequency parameter clamped for themselves and two did not. `MonoToStereoEffect`'s fixed 4500 Hz all-pass is above Nyquist at an 8 kHz project rate: worked through, `a2/a0` is 1.742, so the **pole radius is 1.32 and the filter diverges** — and that is the effect the rack auto-enables for mono sources. `EqEffect` was unclamped too but degenerates rather than diverging, since `sin(w)` goes to zero at multiples of π. At exactly `fs/2` the RBJ low- and high-pass forms put a double pole *on* the unit circle, which is why the clamp is 0.49 and not 0.5.
- **`Biquad.FirstOrderHighPass` was second order.** It used the second-order high-pass numerator with `alpha = sin(w)`, which is Q = 0.5 — critically damped, −12 dB/octave asymptotically. Its one caller is `CompressorEffect`'s sidechain filter, so the detector was hearing half the low end it was configured for. It is now a genuine one-pole, one-zero.
- **`MasterSection.ProcessOffline` must not share a plugin instance with the live rack, and `EffectFactory.Clone` does.** Sharing is right for an A/B snapshot — a snapshot never plays while the chain it came from is playing — and wrong for a render, which does exactly that. `Vst3Plugin.Configure` calls `Deactivate` and then `AllocateBuffers`, whose first act is to free the planes `ProcessBlock` writes through, so a render starting during playback freed native memory the audio thread was filling. `Vst3Effect._configureGate` looks like it covers this and does not: it is per-`Vst3Effect`, and the clone is a different one wrapping the same `Vst3Plugin`. Renders use `EffectFactory.CloneForOfflineRender`, which opens its own instance and carries the settings across — the module and factory are cached, so it costs a `createInstance` rather than a reload.
- **The clones were also never released, and that had a second consequence nobody would look for.** `Vst3Effect`'s constructor subscribes to `plugin.ParameterEdited` and `Dispose` unsubscribes, so every offline render permanently added another handler to the shared plugin: after N renders one move of a slider in the plugin's own editor fired N+1 `QueueParameter` calls. Both offline paths now `Retire` in a `finally`, which cancellation reaches too.
- **The lifetime invariant now lives inside `Vst3Plugin` rather than in every caller.** `Configure` and `Dispose` take a gate; `ProcessInterleaved` **tries** it and fails the block rather than waiting. Blocking the render thread behind somebody else's `setActive` is a dropout of unbounded length; failing one block is a dropout of one block, and `Vst3Effect.Readout` already reports it.
- **Nothing may reset effect state or configure a plugin under `_chainLock`.** `Read` holds it for the whole chain, so `RackEnabled`, `MidSideMode` and `SetEffectEnabled` were stalling the audio callback for as long as a reverb tail or a convolver history took to clear, and `ToggleCompare` for as long as a plugin took to deactivate, restore and reactivate. Each now takes a snapshot under the lock and does the work outside it; `ToggleCompare` configures the incoming chain **before** publishing it. `ReplaceChain` configures only effects that are not already running — the `Retire(replaced.Where(fx => !list.Contains(fx)))` line shows the two chains are expected to overlap, and reconfiguring a carried-over effect outside the lock is the same use-after-free by another route.
- **`SoftwareInputMonitor` deadlocked a device-loss stop against a teardown, and NAudio's behaviour was verified rather than assumed.** `OnPlaybackStopped` called the owner back on WasapiOut's play thread, which takes `_sync` — and a concurrent `StopStream`/`Dispose`/`Configure` holds `_sync` while calling `WasapiOut.Stop`, which **does** call `Thread.Join` (checked by disassembling NAudio 2.2.1), joining the very thread waiting on the lock. `PlayThread` raises `PlaybackStopped` inline when there is no synchronization context, which is the case the file's own comment at `MonitorSession.Start` already anticipated. The callback goes through the thread pool now.
- **`RecordingEngine.Dispose` held `_lifecycleLock` across `TryStopCore`.** That waits on the finalize gate, and a finalization holding that gate needs `_lifecycleLock` to reach the decrement that releases it — so the two waited on each other until the ten-second timeout broke the tie, on the dispatcher. The wait for quiescence stays under the lock; the stop does not.
- **`LoudnessMeter.Configure` published `_channels` before the arrays indexed by it**, so a render thread already inside `Process` could see the new count against the old, shorter arrays. Reachable, because `PlaybackEngine` tears the previous output down asynchronously. Everything is built first and published under the lock `Process` holds, which is what `RecordingLevelAnalyzer.Configure` already did.
- **`LoudnessMeter` tracks true peak linearly and converts on read.** It was taking `20*log10` per sample per channel for the raw peak and four more inside `MeasureTruePeak`; tracking the magnitude and comparing is strictly less work with no trade.
- **The same reasoning applied to `RecordingLevelAnalyzer`'s histogram is wrong, and the measurement is why it is not in the code.** Counting the calls made it look expensive — one logarithm per sample per channel to pick a 0.5 dB bin — so it was replaced with a binary search over precomputed bin edges. **That is 8.5x slower**: 25.52 ms against 2.98 for ten seconds of 48 kHz stereo. `Math.Log10` is a hardware-assisted intrinsic and seven unpredictable branches are not. It is also **0.03% of realtime either way**, so the finding was real in its count and meaningless in its effect. Keep the logarithm; the general lesson is that a transcendental on an audio thread is worth measuring before it is worth removing.
- **`Dither` fed back the error of the value the quantiser wanted to write rather than the one it wrote, and correcting that needed a bound.** Recording `output - wanted` is right — the shaper can push `wanted` past the rail at full scale, and feeding back the unclamped error left the loop believing it had emitted something it had not. But the existing `NothingEverExceedsFullScale` test failed immediately: a square wave at the rail asks the loop to make up a deficit that grows every sample, and it runs away. The committed error is clamped to 2 LSB, which ordinary operation never reaches (dither is ±1 LSB and rounding adds half of one). **The test finding this is the reason to keep it.**
- **`Janssen` rents its matrix from the pool.** `gapLength²` doubles is 33 MB at the 2048 cap and 1.8 MB for a ten-millisecond pop — past the 85 KB large-object-heap threshold, taken fresh for every defect on the record, on a heap that is not compacted and is collected in gen 2. `RecordingLevelAnalyzer.BuildSnapshot` already reached for the pool and says why. Its Levinson recursion also swaps buffers instead of copying twice per order step, which is what `TryAutoregressivePrediction` documents and this one did not.
- **The two offline noise reducers were leaving the Nyquist bin at unity gain.** Both looped to `NrFftSize / 2` where the processor is handed `NrFftSize / 2 + 1` bins. `RestorationWolaGoldenTests` was re-pinned once for this and **the size of the move is the evidence it was the only change**: the learned profile is bit-identical, the RMS figures move in the seventh decimal, and the largest probe delta is 8.5e-6 — one bin at 22.05 kHz. Anything moving them further than that is a real change in behaviour, not this.
- **`RemoveHumAdvanced`'s dynamic depth measured the whole programme rather than the harmonic** — kept because the mechanism applies to any depth control, though the method itself has since been measured and deleted. It took `|dry|`, so anything above about −40 dBFS pinned every notch at 30% strength for the length of the file whatever the hum was doing — and `reduction = Clamp(smoothing * 20, 0, 1)` saturates by −26 dBFS. What the notch removes is `dry - filtered`, which *is* the energy at that harmonic; hum is steady, so a fast and a slow envelope of it agree, and musical energy on the harmonic makes them diverge. That divergence is the discriminator, and it is what the two arrays there were always named for.
- **`DetectMainsFrequency` always answered, so it was a coin flip that silently overrode the user.** It compared the 50 and 60 Hz bins and returned the larger. A 55 Hz probe sits between them and carries no mains component, so it measures the local floor: neither candidate is hum unless it stands 4× above that and 2× above the other, and otherwise the caller's `baseFreq` stands.
- **`ReconstructClippedPeakSpline` had neither of the two things the main declipper's own remarks say are load-bearing.** It never read `EstimatedTruePeak`, so it smoothed the flat top rather than restoring the peak, and it had no lower bound — a natural cubic spline interpolates its knots, every knot here sits at or below the rail, so away from the centre it wrote values under what was recorded. The clamp that is "the whole of a measured regression on percussive material" is now on both paths.
- **`DetectAmplitudeOutliers`' return-to-baseline gate passed unconditionally near the end of a buffer.** `for (j = end + recoverySkip; j < recoveryEnd; ...)` over an empty range leaves the deviation at zero, so the ratio is zero and the test — described in this file as "the strongest thing separating a defect from an instrument" — accepted a musical attack in the last few milliseconds of a side as a pop. The curvature path was safe because its own guard bounds `end`.
- **Predictive click detection and crackle detection both stopped at the last whole block**, dropping up to 4096 samples — about 93 ms at 44.1 kHz — off the end of every channel, which on a record side is the run-out. Both now process a final block clamped back to `length - block`.
- **`Wave64Codec` was the outlier of the three codecs in three ways.** It passed the container's bit depth straight into `AudioDocument`, whose setter takes 16/24/32, so a **64-bit float Wave64 file decoded in full and then threw** — `WavCodec.Decode` supports `fl64`, so it got all the way there. It set neither `FilePath` nor `Title`, so a `.w64` opened as "Untitled" and Save became Save As. And it staged through a fixed `"<name>.part"` opened with `FileMode.Create`, which truncates a concurrent writer's staging file instead of failing.
- **All three writers called `Stream.Write` once per sample frame** — 159 million calls for an hour of 44.1 kHz stereo. `DecodeStreaming` batched the read side into 1 MiB blocks long ago and the write side never got the same treatment. `WavCodec.EncodeFrame` takes a `Span<byte>` now so WAV and Wave64 still share it; AIFF's big-endian encoder is extracted to `EncodeFrameBigEndian` to match. `AiffCodec.Decode` also hoists its six-way format test above the loop, which `WavCodec.Decode` has always done.
- **`RiffMetadata` capped each chunk and not the file.** A hundred megabytes of empty eight-byte chunk headers is twelve million records — a fresh four-character string and a byte array each — so about a gigabyte of managed heap from a file that fits in memory ten times over. There is a total-bytes and a count ceiling now. Its ids are also written back with **Latin1**: they come from `IdFrom`/`IdFromBig`, which widen raw bytes, and ASCII maps anything above 0x7F to `'?'`, which is not the byte-for-byte fidelity that type exists for.
- **AIFF text chunks are UTF-8**, matching `BroadcastMetadata.WriteInfoList` on the WAV side. ASCII silently turned every accented character into a question mark, so a title did not survive the WAV → AIFF → WAV round trip `FileTags` exists to make work. Plain ASCII encodes identically either way.
- **The clipboard is static and was never released.** Copying an hour-long stereo selection kept about 600 MB resident for the life of the process, whether or not any file was still open, and it outlives the `MainViewModel` that filled it. It refuses a selection over 512 MB and is released in `DisposeOwnedResources`.
- **`AppSettings.Instance` used `??=`, which is not atomic.** Two threads reaching it first both load, one wins the field, and the other is handed to a caller who mutates settings nobody will ever save. Every reader is on the UI thread today — checked — but it is read from forty-odd places across the audio layer and the next one added will not know that. Its save also staged through a fixed name, shared with any other copy of the app running against the same profile, and did not flush before the move.
- **`MontageLaneView` built two frozen brushes and one *unfrozen* `Pen` per clip per frame**, and rebuilt its five-entry palette array on every call, while its other ten brushes and pens are static frozen fields. The unfrozen `Pen` is the worse half: WPF registers change notification on it and on its brush. Both are cached by palette slot; plain dictionaries because both call sites are `OnRender`, which WPF runs on the UI thread.
- **All five unreferenced restoration entry points have now been measured against the methods that ship, and all five lost.** They were the standing roadmap item out of this audit — outside the suite, reading as the upgrade path from the wired-up methods, with nothing to catch a regression in them. The instruction was measure and wire up, or delete. See the section below for `ReduceNoiseAdvanced` and the one after it for the other four; none of them survives.
- **`RecordingLevelAnalyzer.Process` holds `_sync` for a whole capture buffer, and measurement says leave it alone.** The audit called it a priority inversion on the WASAPI capture callback, where an overrun drops recorded audio. Measured (`RecordingLevelAnalyzerConcurrencyTests`): **0.686 ms held for a 100 ms buffer and 2.965 ms for a 500 ms one — 0.69% and 0.59% of the audio each represents.** A cached snapshot read is **1.0 us**. The miss path, which recomputes gated integrated loudness over the whole history under the lock, is **1.875 ms after twenty minutes of blocks** — against a buffer period of 100 ms. None of that can overrun anything. The structural complaint was correct and the effect is not there.
- **The one alarming number in that test is an artefact of the test, and it is labelled as such.** A reader gets only **58 snapshots in 2 s** while the capture thread runs `Process` in a tight loop — but that loop has no real-time pacing, so it holds the lock essentially always. At the 0.69% duty cycle the real capture thread runs at, the reader is not starved. Reported rather than asserted, because it measures the harness rather than the code.
- **`SnapshotsStaySelfConsistentWhileCaptureIsRunning` stays whatever happens to the lock.** A torn read here does not throw and does not fail anything — it produces a gain recommendation that is quietly wrong, occasionally, under load — so the assertions are the invariants a tear cannot survive: no NaN, an active span that cannot exceed the elapsed one, counters that only rise, confidence in range. It found **0 faults** against deliberately hostile scheduling, and it is the safety net any future restructuring needs to exist first.
- **That is the second finding in this audit to survive reasoning and die on measurement**, after the histogram binning. Both had a real mechanism, a real count behind them, and no measurement. The rule that comes out of it: on this thread, count nothing without timing it.

## Noise reduction, measured — and a hang the audit itself introduced

- **The audit put an infinite loop into `AnalyzeClicks`, in a path that is on by default, and it took a 51 CPU-hour corpus run to find it.** Making predictive detection scan the tail replaced a bounded `for (start = 0; start + block <= length; start += block)` with one carrying a *clamped* index. The body already had a `continue` for a zero residual scale — pre-existing and previously harmless — and under a clamped index that `continue` re-clamps to the same block and spins forever. A **digitally silent final block gives exactly that scale**, `PredictiveDetection` defaults to true, and the symptom is the whole application wedged with no error and no CPU attributable to anything. `Windows\Media\Alarm05.wav` is one such file. Both predictors now iterate a **list of block starts built up front**, so no `continue` can fail to terminate, and `AFileWithASilentTailDoesNotHangClickAnalysis` pins it at two sample rates.
- **The lesson is narrower than "be careful with loops": do not put a clamped index in a loop whose body has `continue` paths.** The termination argument then lives in every branch instead of in one place, and it only takes one branch written before the clamp existed to break it. Building the iteration up front costs an allocation of a few dozen ints and makes the argument unfalsifiable.
- **`Restoration.ReduceNoiseAdvanced` is deleted, on measurement.** The Ephraim-Malah MMSE-STSA estimator had never been wired into anything, and it read as the sophisticated upgrade to the spectral gate that does ship. Measured head to head over **108 cells** — 9 record transfers and 45 Windows Media files, six hiss severities from 30 dB below the programme down to 0 — it **loses at every severity, on both metrics, on both corpora**: 4 cells won out of 108, **−136.3 dB in total**, and worse in **54 of 54** record-transfer cells. Segmental SNR by severity, gate against MMSE: −8.13/−9.06 at 30 dB down, −4.32/−5.46 at 24, −1.26/−2.51 at 18, **+1.14/−0.18 at 12**, +3.05/+1.65 at 6, +4.43/+2.91 at 0. It is worst exactly where noise reduction earns its place. Its `ScaledBesselI0`/`ScaledBesselI1` helpers served nothing else and went with it, as did the golden test that pinned it.
- **The shipped gate is measured in the same run and behaves correctly, including where it loses.** It is **negative on clean material and positive where there is hiss to remove** — −8.13 dB segmental at 30 dB down, crossing to positive at 12, reaching +4.43 at 0. That is not a fault: a fixed 10 dB reduction applied to hiss already 30 dB under the programme costs more music than it saves noise. `TheSpectralGateBeatsLeavingHissAloneWhereThereIsHissToRemove` therefore asserts only at 12, 6 and 0 dB, and reports the rest.
- **Segmental SNR, not whole-signal, and the choice is the measurement.** A global ratio is dominated by the loudest passages, where the noise is masked and every suppressor passes the audio through at unity; what separates one estimator from another is what each does *in the quiet*. Both are reported so the metric's effect is visible — it does not change the verdict here, but the whole-signal figure is roughly half as discriminating (MMSE wins 16 of 108 by it, 4 by segmental).
- **The hiss damage model is tilted, not white, and stationary.** Surface noise is tilted, and a flat floor would let a single threshold do as well as a learned profile — which is the thing the profile exists for, so white noise under-tests both methods and flatters the simpler one. Stationary because that is the assumption both estimators are built on. `PlantedHissArrivesAtTheSignalToNoiseRatioItWasAskedFor` calibrates the planting with no corpus needed.
- **`MeasureNoise` deliberately does not apply `UsableReference`.** That screen rejects a recording carrying clicks, which is right for crackle and spectral repair, where a real defect contaminates the clean reference. Hiss is different: a transfer's own surface clicks appear identically in the reference and in the processed output and cancel out of the score. Applying it excluded **all nine record transfers** — every one reads 1.8 to 4.4 clicks a second, being records — and left the measurement standing on five notification chimes, where both methods lost and the answer would have been noise.
- **Two runs were launched at corpus scale on a cost model that had already been wrong once, and the second ran overnight.** The stages measured individually are all fast: `AnalyzeClicks` 1.4 s on a 131 s side, `PlantHiss` 33 ms, `LearnProfile` 25 ms, `ReduceNoise` 257 ms, MMSE 367 ms for 60 s of audio — about 74 CPU-seconds for the whole corpus. The run took 51 CPU-hours because one file hung. **Time one short pass before starting anything that walks a corpus**, and give a long run a progress trace it writes as it goes: the first two attempts produced no output at all, so there was nothing to diagnose from.
- `NoiseReductionCostTests.MeasureWhatEachStageCosts` exists to make that check one command. It reports each stage as a multiple of realtime on five seconds of audio.

## The last four unwired restoration methods, measured — and all four deleted

`RemoveHumAdvanced`, `DetectSilencesAdvanced`, `RepairClicksSpectral` and `RepairClippingSpline`
were the standing roadmap item out of the audit: public, defect-fixed, and called by nothing. Each
is now measured against the method that ships in its place, on the same corpora the declip and
click work uses. **None of them wins, and three lose badly.** About 26,000 characters of source and
ten members went with them.

- **`RepairClicksSpectral` is the clearest of the four: −15.29 dB against Janssen, winning 1 cell of 84.** Same analysis, same events, same score, so the only difference measured is the estimator. Janssen takes the corpus from its damaged state by **+28.70 dB** and the spectral filler by **+13.41**, for **−1284.4 dB in total**. Worse, it is **negative at 12 and 6 dB above the local level** — the quiet end, where a click is hardest to find and the repair has the least to work from, which is exactly where the corpus work has spent its time. Papoulis-Gerchberg fills a gap from the spectrum either side of it; Janssen fits an autoregressive model to the signal *including* the missing samples and refines both together, which is a strictly better-posed question, and 84 cells say so.
- **`RepairClippingSpline` loses by less and loses on the material this app is for.** Aggregate **−2.09 dB** against the shipped arch/A-SPADE chain (chain +11.91, spline +9.82, −392.0 dB in total). It *wins* 86 of 188 cells, which looks close until the cells are split by corpus: on the **nine record transfers it loses 32 of 36**. A natural cubic spline interpolates its knots and every knot sits at or below the rail, so it can only smooth a flat top — the audit added a lower bound to it, and even bounded it has no equivalent of `EstimatedTruePeak`, so it never restores a peak. That is a structural ceiling, not a calibration.
- **`DetectSilencesAdvanced` matches the shipped detector on the only number it could have won on, and is worse on both of the others.** Identical **100% recall** of planted gaps; **9 ms of edge error against 3**, and **2 spurious gaps against 0**. Hysteresis is what buys the recall and it is also what holds a gap open past where it ended. In this app a detected silence becomes a CD track mark, so a boundary 9 ms late is a track that starts late and an invented gap is a track that should not exist — the two things this tool must not do, traded for a recall it did not gain.
- **`RemoveHumAdvanced` is the one where getting the metric right reversed the answer, and that is the finding worth keeping.** The notch bank that ships removes **48.67 dB** of hum for **0.45 dB** of music moved; the adaptive remover removes **26.68 dB** for **2.48 dB** — 22 dB less hum for five times the damage. It wins **0 of 54 cells**.
- **The first hum measurement said the adaptive remover won 52 of 54, and it was measuring the metric rather than the filter.** A whole-signal residual against the clean reference charges *phase rotation* as error, and cascaded notches rotate phase far outside their own bandwidth: measured, notching music with **no hum in it at all** scores about 20 dB of "damage" by that metric. So the residual rewarded whichever method notched *less*, which is the method that removes less hum. The valid measurement is two numbers taken in the frequency domain — power at the mains harmonics before and after, and power at probe frequencies that are harmonics of neither 50 nor 60 Hz — and it reverses the verdict completely. `NoiseReductionCostTests.AWaveformResidualCannotScoreANotchBank` pins the trap.
- **This is the third time in this repo a waveform residual has given the wrong answer about a filter**, after wow (where a whole-file residual reads a good correction as total failure, because correcting a time base integrates a rate) and declip (where A-SPADE was scored against the damage rather than against the incumbent). The pattern: **a residual is only a valid score when the two signals are meant to be sample-aligned and equal.** Anything that legitimately moves phase or time needs a metric that does not look at sample differences.
- **Two of the four comparisons are kept as characterisation tests of the shipped tools, and two are not.** `TheNotchBankTakesTheHumOffWithoutTakingTheMusic` and `TheSilenceDetectorFindsPlantedGapsWithoutInventingAny` cover ground nothing else did — there was no corpus measurement of either tool before this. The click and declip comparisons are dropped, because `ClickCorpusTests` and `DeclipCorpusTests` already measure those shipped paths over the same corpora and a second copy would only be a second thing to keep in step.
- **`Restoration.GoertzelPower` and `DetectMainsFrequency` went with the hum remover.** `DetectMainsFrequency` was the audit's coin-flip finding — it always answered, so it silently overrode a user's 50 Hz choice — and `GoertzelPower` served nothing else. Note that `RecordingLevelAnalyzer.GoertzelPowerDb` is a **different method** and is still live; the names are close enough to be worth stating.

## VST3 parameters, verified against a plugin built to be verified against

The parameter and editor path had been written and never executed: every plugin installed here
publishes **zero host-visible parameters**, so `ReadParameters`, `SetParameter`, `ApplyParameter`,
`Vst3ParameterChanges` and the rack's flag filtering were correct-looking code with nothing to run
against. "Written but unverifiable" was the standing entry. It is now verified.

- **`tests/WaveLab.Tests/Vst3SyntheticPlugin.cs` is a VST3 plugin built in this process out of
  vtables of function pointers**, and the host cannot tell the difference: a factory, a component, a
  separate controller, real slot numbers, `stdcall`. `Vst3Plugin` runs against it **completely
  unmodified**. It is the existing pattern turned around — `Vst3HostContext` and `Vst3MemoryStream`
  are managed objects handed *outwards* as `[UnmanagedCallersOnly]` vtables plus a `GCHandle`; this
  is one handed *inwards*. The only production change is `Vst3Module.FromFactory`, which wraps a
  factory pointer this process already holds instead of calling `LoadLibrary`.
- **The component and the processor must be different addresses, and that is the ABI rather than a
  detail.** `IComponent` slot 7 is `getBusCount`; `IAudioProcessor` slot 7 is `setupProcessing`.
  Returning one pointer for both calls the wrong function with the right arguments. The native block
  carries two vtable pointers and hands out its own address for one and that address plus eight for
  the other, which is what a C++ compiler emits for two base subobjects.
- **Every object shares one layout, because two layouts put the handle at two offsets.** A component
  block puts its processor vtable exactly where a plain object puts its `GCHandle`, so recovering the
  managed object from a component pointer read a vtable as a handle. That is a fault, not a wrong
  answer, and it is the same class of bug the `AudioBusBuffers` note already warns about. The factory
  and the controller carry a null second vtable and eight wasted bytes to keep the offset uniform.
- **The instrument reproduced the bug it was built to detect, and the negative test caught it.**
  The first version backed the controller and the processor with **one** value dictionary — so
  `setParamNormalized` alone appeared to change the audio, which is exactly the failure-that-looks-
  like-success this file exists to pin. VST3 splits a plugin so the halves can live in different
  processes; they hold their own copies and the host is what keeps them in step. They are two
  dictionaries now, and `SettingOnlyTheControllerDoesNotChangeTheAudio` asserts the controller moved
  and the processor did not.
- **What the six tests establish**: the host reads a published list with identifiers that are not
  indices and flags intact; setting round-trips through the controller and clamps out-of-range values
  before the plugin sees them; **a parameter set on the host reaches `process` and changes every
  sample** (0.5 → 0.5000, 0.25 → 0.2500); setting only the controller changes nothing; an edit made in
  the plugin's own editor reaches the host through `performEdit`; state round-trips and the controller
  is told; and the rack draws **1 slider from 4 published parameters**, hiding the bypass, the hidden
  one and the read-only meter.
- **`ApplyParameter` does not reach the processor until a block is processed, and that surfaced
  here.** It writes the controller and *queues* for the processor, and the queue is read only inside
  `process` — so a parameter moved while nothing is playing has not reached the component when
  `getState` is called, and the state saved is the value from **before** the move. Found by writing
  the state test without a block in the middle and watching 0.8 come back as 0.5.
- **It is a property rather than a defect, and only because two things are true at once.** A rack
  preset stores each parameter value *as well as* the opaque state, and restores state first so the
  values land on top; and a plugin publishing no parameters — all 22 here — has a component state
  that never depended on the queue. Break either and stale state ships. Worth knowing before anything
  starts saving presets from a plugin that both publishes parameters and is not playing.
- **This does not make the 22 local plugins publish anything**, and nothing about them has changed.
  The claim that their emptiness is the plugins rather than the host stands on the five eliminated
  hypotheses recorded above; what is new is that the host side is now known to work when given
  something to work with.

## ML denoise: the ceiling, measured before anything was built

The roadmap declines ML denoise as "needs bundled models; out of scope for a lean native app".
Reversing that starts with a number, not a model — and the number can be had **without a model, a
runtime, a download or a training set**.

- **Every single-channel denoiser in the class being proposed estimates the same thing: a per-bin
  gain on the noisy magnitude spectrum.** RNNoise, DTLN, the DNS-Challenge models and the
  Ephraim-Malah MMSE-STSA this repo just deleted differ only in how well they estimate it. So
  compute it **exactly** — from the clean signal and the noise the harness planted — and run it in
  the same STFT the shipped gate uses. That is the Wiener mask, no estimator can beat it in this
  framework, and the gap to the shipped gate is the entire headroom available to any of them.
- **The verdict is that the headroom is large: +9.63 dB over 108 cells, and the oracle wins 108 of
  108.** By severity, gate against oracle: −8.13/+5.09 at 30 dB down, −4.32/+6.83 at 24,
  −1.26/+8.20 at 18, +1.14/+9.52 at 12, +3.05/+10.84 at 6, +4.43/+12.23 at 0. **The oracle is
  positive at every severity and the gate is not**, which is the shape of the opportunity: the
  gate's documented failure on quiet hiss is not intrinsic to masking, it is intrinsic to *this*
  mask.
- **Measured honestly the figure is +7.15 dB, and that is the one to quote.** Headroom over the gate
  flatters any replacement, because the gate scores *below do-nothing* at the two quietest
  severities — a fixed 10 dB reduction applied to hiss already 30 dB under the programme costs more
  music than it saves noise. A rule that simply declined to fire there would collect **8.13 dB of
  the 13.21 dB gap at 30 dB down for free, with no model at all**. Against the better of the gate
  and doing nothing, what remains is +7.15 dB, and that part is reachable only by estimating the
  mask better.
- **So the cheap experiment comes first, and it is not machine learning.** Making the reduction
  depth follow the measured noise-to-programme ratio — or simply not firing when the floor is
  already far down — is a few lines against days of training and a native dependency.
  **It was done, and the section below has the result: it is worth about half a decibel of mean and
  a large cut in the tail, not the 8.13 dB this bullet originally implied.** That figure was the gap
  at one severity; a six-severity average can be at most a sixth of it, and only for a rule that
  identifies those cells perfectly. What it fails to collect is the honest brief for a model.
- **A ceiling is not a forecast.** Real estimators reach a fraction of an oracle mask, and this repo
  has a worked example of a principled one landing *below* a crude one: MMSE-STSA lost to this same
  gate on these same cells, by 136.3 dB in total. +7.15 dB is what is on the table, not what a model
  would get.
- **The oracle is exact and its floor is measured rather than assumed.** `TheOracleMaskIsActuallyAnOracle`
  calibrates it with no corpus: +16.68 dB on noise it can see perfectly, and on clean audio it costs
  **0.00 dB beyond a bare STFT round trip**. That round trip is 35.0 dB segmental — a magnitude mask
  leaves the noisy phase alone, so even a perfect one falls short of the clean signal — and since
  the gate runs through the identical STFT the floor cancels out of the comparison. Quoting the
  ceiling without it invites the reading that the oracle is near-perfect reconstruction. It is not.

## The cheap half of the noise headroom: reduce less where there is less to remove

Built, measured, held out, and **wired up** — `Restoration.SuggestReductionDepthDb`, called from
both noise-reduction entry points. It is the non-ML half of the +7.15 dB the oracle mask showed,
and it is worth much less than the ceiling suggested — but what it is worth is not in the mean.

- **My own earlier framing overstated it and the correction matters.** The ceiling run said a rule
  that declined to fire would collect "8.13 dB of the 13.21 dB gap at 30 dB down". True *at that
  severity*, and a corpus average over six severities can be at most a sixth of it — and only for a
  rule that identifies those cells perfectly, which no estimator built from the audio alone can.
- **The measured value is about half a decibel of mean, and the tail is the real result.** A fixed
  10 dB scores **−0.85 dB segmental over 108 cells, worse than leaving the audio alone**. The rule
  scores **+0.04 fitted, −0.37 held out by recording, −0.40 held out by corpus**. But **cells that
  come out worse than doing nothing fall from 46 of 108 to 15, and cells worse than −3 dB from 31
  to 8**, worst cell −21.13 → −17.26. It improves 52 and worsens 56 and still gains, because what
  it gives up is small and what it prevents is large. "The noise reducer rarely makes things
  audibly worse" is worth more than half a decibel of average.
- **The depth response is why, and it is worth reading.** Segmental gain by planted severity against
  reduction depth: at **30 and 24 dB down every depth is negative** and monotonically worse
  (−1.38 at 1 dB through −8.13 at 10); at **18** it peaks at +0.36 at 1 dB and turns over; at **12**
  it peaks at +1.30 at 6 dB; at **6 and 0** it is still climbing at 10 dB (+3.05, +4.43). So the
  optimum depth moves from zero to more-than-offered across the range, and one fixed number cannot
  serve both ends.
- **The parameters sit on a plateau, not a peak, and that is the reason to trust them.** The whole
  fitted surface spans about half a decibel and every ceiling between 8 and 10 dB scores within 0.01
  of the best. Folds disagree about the argmax — 2 of 18 by recording, 0 of 2 by corpus — and that
  is **near-ties moving under tiny corpus differences, not the instability that killed five declip
  calibrations**, where variants swung by tens of decibels. Reading fold disagreement without
  looking at the surface would have condemned this wrongly.
- **The ceiling for this whole class of fix is +2.64 dB and it collects about a fifth.** Best depth
  chosen per cell in hindsight scores +1.79 against the fixed −0.85. Adapting the depth is the
  cheap part; the oracle mask's +7.15 dB is still out there and still needs a better estimator.
- **The estimator is the quietest two-second window against the whole-signal level**, both from the
  audio alone — a rule needing a clean reference could not ship. Over the corpus it tracks the
  planted severity monotonically: **15.9 / 14.8 / 12.7 / 9.5 / 5.9 / 2.7 dB** for hiss planted at
  30 / 24 / 18 / 12 / 6 / 0 dB down. It is compressed, because each recording carries its own floor
  and the quiet window holds music, and the spread within a severity is wide — 22.1 dB at the
  quietest. That overlap is exactly why the rule collects a fifth of its ceiling rather than most
  of it.
- **A low percentile of the windows is the more defensible-looking statistic and is measurably
  worse.** It was built and run: readings compress to **8.7/8.5/7.8/6.4/4.3/2.0** and the rule takes
  cells-worse-than-nothing to **29** where the minimum takes them to **15**. What the minimum costs
  is a boundary case — a window lying across the edge of a spliced-in silence is almost all silence
  and reads far below the real floor, so the estimate comes out high and reduces too little.
  `ASplicedSilenceStillFoolsTheNoiseFloorEstimate` records it. **A constructed case lost to a
  measured one.**
- **Two defects came out of writing the non-corpus tests, both of the sentinel kind.** The estimator
  returned **0 both for "no programme" and for "programme level with its own floor"**, and the rule
  reads 0 as maximum noise — so an empty buffer asked for **full reduction**. It returns
  `NoiseDepthCeilingDb` when there is nothing to measure. And the quietest-window search accepted a
  **digitally silent** window as the floor, reporting an infinite ratio and switching the suppressor
  off entirely on a gated or hard-trimmed file.
- **`ReduceNoise` itself is untouched**, so `RestorationWolaGoldenTests` still pins what it always
  pinned; the depth is chosen by the callers. The workbench **reports the depth it chose** rather
  than applying it silently — a tool quietly ignoring most of a slider's travel is
  indistinguishable from a broken one. The card carries a readout line under the Maximum reduction
  slider (design: `docs/design/noise_depth_readout.png`), following the declip readout exactly:
  “Applying 2.3 dB · hiss sits 7.7 dB under the programme”, or “**Not reducing** · hiss already
  12.4 dB under the programme” with amber **on the verb alone** — declining is a decision, not a
  fault, and colouring a fact like a fault is what the VST3 scanner note records as teaching users
  to distrust the colour. `DescribeNoiseDepth` is pure, so the wording is unit-tested without a
  window, and `NoiseDepthRenderProbe` builds the real dialog at its 860 px minimum and measures the
  line in place: **370 px of room, 286 px wanted, one line**. The mockup originally quoted 365 and
  268 from `FormattedText`; glyph metrics are not the built control, and the render is what the
  numbers now come from.
- **That render found a second defect and it is fixed: at the 860 px minimum the Hum Removal card
  cut its description mid-word** — “without shifting stereo alignm”. Same fault as the rack’s render
  buttons and the plugin name under the power LED, and the same cause: a caption with no
  `TextWrapping`. **The fix is a style rather than an attribute**, because all four workbench card
  captions repeated the identical three attributes inline with nothing shared — exactly the
  duplication recorded above for `UpperLabel`. `CardCaption` in `Theme.xaml` carries `Faint`,
  `SecondarySize` and `Wrap`; the declip card keeps its 10 px bottom margin as the one override.
  Only six places in the app used that triple and two of them are different components — a trimmed
  status line and an inline “BPM ·” label — so they are left alone.
- **Reviewing the readout found that it re-measured on every slider tick, and the fix is an API
  split.** `UpdateNoiseDepthReadout` runs from `UpdateReadouts`, which fires on every movement of
  any of the dialog’s ten sliders, and it called the `float[][]` overload of
  `SuggestReductionDepthDb` — which walks every sample and then slides a two-second window over
  them. **Measured: 388 ms for a 22-minute stereo side, 88 ms for five minutes**, on the dispatcher,
  per tick. The cached `_noiseToProgrammeDb` field existed for exactly this and was not being used.
  There are now two overloads: one that measures, one that takes an estimate and is three
  comparisons, with `BothOverloadsOfTheRuleAgree` pinning that they cannot drift.
- **The render path now uses that same cached estimate, so the readout cannot disagree with what
  ran.** It had been re-measuring from `work`, which by the noise stage has already been through
  click repair, declip and hum removal — so its floor is not the one the user was shown a number
  for. Same lesson as `DescribeDeclipChoices`, which is deliberately the call the selection is made
  with.
- **A declined reduction on the plain Reduce Noise path was making an edit and saying nothing.**
  `RunRangeTool` skips the splice when the transform returns `null`, and it was returning the
  untouched buffer instead — which splices a range over itself, costing an undo step and a dirty
  document for an edit that changed nothing. It returns `null` now. That path has no card to carry a
  readout, so the one case that needs explaining gets an `InfoDialog` naming the measured figure; a
  tool that declines silently is indistinguishable from one that failed.
- **The line also ignored the card’s own Enabled switch**, so a switched-off card still reported
  “Applying 2.3 dB” for a stage that would not run. The switch is now the first thing it reports.
- **Wrapping costs about 14 px of card height and that is the right trade.** The hum caption becomes
  two lines, which pushes the output-mix row down; the panel already scrolls, so nothing is lost. A
  caption cut mid-glyph reads as a drawing fault, a caption on two lines reads as a caption.

## Review fixes: five places an invariant this repo already states was not actually held

A review of the timing-critical paths — playback, the master section, capture, the document, the
codecs, and the async surfaces above them. Most of it held: the copy-on-write document, the
session-id fencing through `RecordingEngine`, the RF64 bounds checks, the VST3 refcount with
`Monitor.TryEnter` on the audio thread, and staged-temp-then-move on every writer. What follows is
the part that did not, and in four of five cases the file already had the correct rule written down
somewhere else in itself.

- **`MasterSection.RemoveEffect` cleared the effect under the lock `Read` holds for the whole
  chain, and the comment three lines below it said the opposite.** `RackEnabled`, `MidSideMode` and
  `SetEffectEnabled` all deliberately reset outside that lock and each says why; this one did not.
  Removing a Convolution Reverb mid-playback therefore parked the render thread on
  `ConvolutionReverbEffect.ResetState` — every partitioned convolver plus five per-channel buffer
  sets, megabytes for a multi-second IR — which is an underrun, i.e. a click.
  `RemovingAnEffectResetsItWithTheChainLockAlreadyReleased` pins it by having another thread take
  the chain lock from inside `ResetState`; against the old code that thread blocks for the test's
  full two-second budget.
- **The metering ring took a lock on the render thread so the UI could copy 16 384 samples under
  it.** A monitor is not priority-inheriting, and the spectrum analyser and goniometer each hold it
  for a full ring at 30 Hz — the same objection this repo already raised against `StudioEq`
  locking around `Process`. The ring is a power of two now, so the write index advances with a mask
  instead of a modulo, `Read` is its only writer, and the position is published once per block with
  `Volatile.Write`. A reader that catches the leading edge mid-write sees the previous block's
  samples there: one refresh of staleness on a scope trace, against a stall on the audio callback.
- **`PeakStore`'s "atomic publish" was three unsynchronized stores.** `_doc`, `_levels` and
  `Version`, written on a thread-pool rebuild and read by `WaveformView.OnRender`. Every index is
  clamped so it never faulted; what it could do is pair the new document's length with the previous
  pyramid for a frame. One immutable `State` record makes the comment true.
- **`SoftwareInputMonitor` published the session reference atomically and then disposed it out from
  under a call already inside it.** `Volatile.Read`/`Volatile.Write` order the *reference*, not the
  work: between the capture thread reading `_session` and calling `Enqueue`, `StopSession` can null
  the field and COM-release the `WasapiOut` and `MMDevice`. The reachable trigger is toggling
  software playthrough **during a take** — `RecordingEngine.SoftwarePlaythroughEnabled` calls
  `SetEnabled`/`Configure` while `OnData` is still feeding the monitor; the other stop paths are
  safe only because `capture.Dispose()` joins the capture thread first. `MonitorSession` now counts
  in-flight enqueues and `Dispose` spins them out. Increment-then-test against
  exchange-then-read: both are full fences, so either the enqueue sees the flag or the teardown
  sees the count.
- **The device-failure dialog was dropped whenever a new `Play()` beat the dispatcher.**
  `PlaybackEngine` raises `PlaybackStopped` then `PlaybackFailed` back-to-back, so the two
  `BeginInvoke`s are adjacent and the session id pairs them correctly — but `MainViewModel` set
  `_stoppedPlaybackSession` *after* the staleness guard, so a superseded session returned before
  recording the id its own failure notification was about to look for. Playback stopped and said
  nothing. It is recorded before the guard now.

Four more, smaller, and none of them races:

- **`Limiter.Process` reconfigured itself from inside the audio callback** — six allocations — and
  did it against the stored channel count rather than the stream's. It passes audio through and
  reports `Configured` false instead; a safety ceiling that silently stops limiting is worse than
  one that says so, so `LimiterEffect.Readout` surfaces it.
- **The channel menu ran whole-file edits inline on the dispatcher, at three full-length copies of
  the file apiece** — the working copy, the undo copy, and the splice. An LP side at 96 kHz is
  ~700 MB a copy with the window frozen and uncancellable, while every comparable operation in the
  app already goes through `Progress.RunBlockingAsync`. `ChannelTools` now has pure
  `…Data(channels, …)` transforms that a worker can run and `MainWindow.EditDocumentAsync` commits
  with `ReplaceAllOwned`, which retains the outgoing arrays and removes the third copy. The
  transforms clone rather than alias, deliberately: `ReplaceAllOwned` takes ownership of what it is
  handed.
- **`Processing.InsertSilence` cast an unbounded `seconds * SampleRate` straight into `new
  float[n]`.** Only ever called with a literal `1.0`, but it is public and a negative length is an
  odd way to find that out.
- **`AppSettings.Save` serialized the live object while nothing ordered the mutations against it.**
  A dictionary edited mid-serialize throws out of the writer and loses the whole file, not the one
  entry — and the class's own comment already worried that "the next one added will not know" the
  threading rule. Rather than sprinkle locks at fourteen call sites, the mutable collections are
  reached through accessors that take `SyncRoot` (`GetInputCalibration`, `SetInputCalibration`,
  `RemoveInputCalibration`, `AddVst3Folder`, `RemoveVst3Folder`, and the two snapshot readers).
  `SetInputCalibration` also rolls the entry back when the write fails, which is what
  `ForgetDeviceMemory` had always done and `WriteCalibration` had not.

And one that was left as it was, with the reasoning recorded because it looks like a bug:
`RecordingEngine.Start` reaches `TryStopCore` from the dispatcher and waits on `_finalizeGate`,
which a finalization may hold while waiting for the `RecordingStopped` that `WasapiCapture` posts
to *that same thread*. The wait cannot help it finish; it parks the UI for the timeout and fails
anyway. Failing on `_activeFinalizations` instead would regress the legitimate case — the flatten
releases the gate, and starting a new take during one has always worked — so a separate
`_finalizeGateHolders` counter marks only the stop/snapshot phase, and `StartCore` fails fast on
that alone.

## Keep what was removed: the residual, and what measuring it said about the tools

A restoration pass is a destructive claim — that what it took out was damage and not music — and
until now the only evidence was an A/B. **Keep what was removed** collects the pass's residual into
its own tab so the claim can be listened to.

- **One subtraction covers every tool, and that is the design.** `RestorationPreview.Difference` is
  the complement of `MixRange`, taken at the commit point from the dry snapshot and the audio that
  was actually spliced. Three bespoke routes were available and all were declined: `HumTracker`
  already builds its removed signal as a buffer and throws it away, and the declick and declip
  deltas are confined to their event spans. **A tool reporting its own residual can disagree with
  what ran** — the same reason `DescribeDeclipChoices` is the call the selection is made with — and
  the dry/wet blend falls out for free, `dry − blend = wet·(dry − processed)`.
- **The dry reference is free everywhere it is needed.** `RunRangeTool` and `RunWholeFileTool`
  already snapshot `doc.Channels` before the transform, and splices replace channel arrays rather
  than writing into them, so the difference is taken against that snapshot at an offset. No extra
  copy of the range is made; the residual itself is the only allocation.
- **The samples are the exact difference and are never touched.** `AudioDocument.MonitorGain` is
  applied in `PlaybackEngine.DocumentProvider.Read` and nowhere else, so save, export, the peak
  pyramid, statistics and loudness are unaffected by construction. It is hard-limited to full scale
  in `ApplyMonitorGain`, which is not optional: the loudest thing in a declick residual is a click,
  and it is going to the speakers. `MonitorGain` is read once per callback so one buffer is never
  half at the old lift; `IsResidual` — not the gain — is what puts the bar on screen, so pulling the
  lift to 0 dB to hear the true level is not a one-way door.
- **The lift needs two anchors and either alone gets a real case wrong.** Peak alone under-lifts the
  case the feature exists for; RMS alone clips the other one. `MonitorGainFor` takes the smaller of
  "body to −24 dBFS" and "peak to −1 dBFS", clamped to [0, +60] dB. **Both halves are measured, not
  supposed**, and the measurements are the next three bullets.
- **A declick residual is louder than the record it came out of, which is the opposite of what the
  word suggests.** Clicks planted 18 dB above the local level: the residual **peaks at +6.7 dBFS
  while the programme peaks at −7.8**, rms −32.3. It is offered no lift, correctly. Anyone
  designing around "residuals are quiet" is designing for the wrong tool.
- **99.72% of what click repair removes sits on a planted click.** Measured with the hit mask
  widened by 24 samples either side, because a repair reconstructs from a span a little wider than
  the defect and charging that to the music would score a correct repair as damage. What lands
  elsewhere is the detector's false alarms — and hearing them is the point.
- **The spectral gate's residual is not a hiss bed, and that is the best argument the feature has.**
  With hiss planted 18 dB down the residual sits **5.6 dB under the programme**; planted 30 dB down
  it sits **5.8 dB under**. It barely moves, because the gate reduces by a fixed depth wherever it
  is asked to, so what it removes tracks the programme rather than the noise. That is the same
  finding the corpus records — a fixed reduction on hiss already far down costs more music than it
  saves noise, which is why `SuggestReductionDepthDb` exists — except that it is now **audible**
  rather than a number in this file.
- **Hum removal is the case the lift was built for, and its residual is not only hum.** A −42 dBFS
  mains line comes out as a **−33 dBFS residual** — about 9 dB more than there was hum to take,
  which is the music in the notches' skirts, and exactly why `HumTracker` subtracts an estimate of
  each partial rather than notching. It sits 15 dB under the programme and is offered **+9 dB**.
- **Residual plus restored returns the original to 3.7e-9** over 88,200 samples of real repair.
  Asserted as a bound rather than as bit equality: `(a−b)+b` is exactly `a` only where Sterbenz
  applies, which a repair usually but not always satisfies.
- **The option is off by default and the caption states its cost**, because keeping a residual is
  one more copy of the range — about 505 MB for a 25-minute stereo side. It is remembered in
  `AppSettings.KeepRemovedMaterial`, since the person who wants it wants it for a collection. It is
  deliberately kept out of `RestorationSettings`: folding it in would invalidate the wet preview
  cache and mark the preset custom for a choice about where the output goes.
- **The residual tab does not steal focus.** It arrives immediately after an Apply, and moving the
  user off the file they just restored onto a file of clicks is not what they asked for. A pass that
  removed nothing opens no tab and says so — an empty tab is worse than no tab.
- **Wow and flutter is deliberately excluded.** Correcting a time base integrates a rate, so a
  whole-file waveform residual reads a good correction as total failure; this file already records
  that trap twice. A residual there would be actively misleading.
### Review fixes, and the two that were about telling the truth rather than about audio

- **Cancelling while the residual was being built reported "document unchanged" about a document
  that had been changed.** The splice commits, then `Difference` runs under the same token, and
  `ProgressHost.RunAsync` only has a `finally` — so the cancellation reached the caller's handler,
  which says the document is untouched. It is not a narrow window: building a residual for a record
  side is half a gigabyte of writes with a cancellation check every 65 k samples. Both orchestrators
  now go through `MainWindow.CaptureRemovedAsync`, where **cancelling cancels the residual and not
  the repair** and the message says which half you got; the outer handler is guarded on `applied`
  as a backstop. The workbench never had this — there the difference is taken inside the render's
  own `Task.Run`, before the commit — and that asymmetry is now deliberate rather than accidental.
- **The peak and RMS behind the monitor lift were two full passes on the UI thread**, which is the
  rule this file states for itself and then broke. They are one pass (`MeasureLevels`) on the worker
  that built the residual, handed to `AddResidualDocument`; measuring there is still supported and
  documented as what it costs. It hid well because it ran under the blocking overlay, so it read as
  the operation still going rather than as a freeze — worth remembering when judging whether a
  progress overlay is covering for something.
- **A residual is a whole extra copy of the range and had no ceiling, where the clipboard already
  has one.** Stating the cost in the caption is not declining an impossible one: without a limit a
  three-hour 96 kHz transfer throws `OutOfMemoryException` out of a background task *after* the
  repair has committed. `ResidualSummary.MaximumResidualBytes` is the clipboard's 512 MB, the option
  is **shown and disabled** past it rather than hidden — an option that vanishes on long files reads
  as a feature that does not exist — and `ReadKeepRemoved` will not persist an answer from a box the
  user was never able to reach.
- **The limit on the monitor path was gated on the gain not being one, which skipped it exactly
  where it was needed.** A declick residual is louder than the record it came from and is correctly
  left at unity, so `if (gain == 1f) return` sent a +6.7 dBFS click straight to the speakers of
  somebody about to hear it for the first time. `ApplyMonitorGain` now takes a `limit` flag set from
  `IsResidual`; ordinary documents pass through untouched as they always have.
- Three smaller ones. `AddDocument(activate: false)` could leave a workspace holding documents with
  nothing selected, so the first tab activates regardless. The crackle tool's `ContinueWith` wrote
  its defect count over the residual's status line, and now composes one line carrying both. And a
  sub-range residual is titled **"(removed at 1:23)"**, because two selections restored in one
  session otherwise arrive as two identically named tabs with nothing to say where either belongs.

- **Rendering the monitor bar found the assertion wrong rather than the layout, and the failure mode
  is worth knowing.** `DesiredSize.Width` includes an element's own margin and `ActualWidth` does
  not, so a note given exactly the width it wanted read as trimmed by its 14 px margin. Worse, the
  test **crashed the host rather than failing**: an assertion thrown inside `Wpf.Show`'s callback
  skipped the cleanup, so `MainWindow`'s close path met two unsaved documents and put up a modal box
  with nobody on that thread to answer it. **In a shell probe, measure inside the callback and
  assert outside it, and mark documents saved in a `finally`.** At the shell's 1180 px minimum the
  bar is one 45 px row and the note fits in 469.5 px.

## Run-out detection: the noise floor was being read off the music

"Stop when the side runs out" cut a take short mid-fade, and the diagnosis is that the
detector's *floor* was never a floor. Measured by replaying the real recordings through the
shipped detector (six transfers from `Music\mymusic`, 44.1 kHz stereo float).

- **The evidence is arithmetic before it is a mechanism.** `cut short.wav` is 140.250 s and its
  last block classified as programme ends at 138.25 s — exactly `KeepAfterProgramSeconds` apart,
  so that file *is* a run-out trim. The same song played again and stopped by hand runs
  **154.49 s**, so about **13 s of fade-out was thrown away**, cut while the music was still only
  6 dB down from full level.
- **The floor was a low percentile of a 30 s sliding window, and half a minute into a track that
  window holds nothing but music.** At the moment the fade began the "floor" read **−28 dB** and
  the gate sat at **−18.2 dB** — while the transfer's actual groove noise, plainly visible in the
  lead-in of the same file, is **−66 dB**. The gate was inside the music's own dynamic range: the
  song's median block level is about −17.5 dB, so **27% of blocks during full-level music already
  failed the programme test**, and the longest all-fail stretch mid-song was **6.6 s against a
  12 s hold**. The take was about two of those from ending itself in the middle of the song.
- **`HasSeparableFloor` is the guard that should have caught it and it cannot, because ordinary
  music satisfies it.** Its test is `P90 − P10 >= 10 dB`, and this track measured **10.4 to
  11.2 dB** — so the threshold **flickered between −55 (no honest floor) and −18 (floor + 10)
  every few seconds** for the length of the song, decided by whether the last thirty seconds
  happened to clear 10.0 dB of spread. That is the whole of "works perfectly except it got fooled
  on one record": a compressed pop single sits on the boundary and the coin lands either way.
- **The fix is where the floor is learned from, not how far above it the gate sits.**
  `RunOutDetector` now keeps the take's ten quietest blocks — *admitting only blocks already below
  `MinimumProgramBlockDb`*, so a block loud enough to be programme can never become the floor —
  and gates at the loudest of them plus the same 10 dB. Non-circular, and it bounds the gate into
  **[−55, −45] dB** by construction: a quiet transfer gets the absolute minimum, a noisy one earns
  up to 10 dB more. The sliding window, its percentiles and `WindowHoldMultiple` are gone.
- **Measured over the six transfers, the three takes that had stopped correctly do not move at
  all**: last programme stays at 170.4 / 214.1 / 182.3 s, the same 100 ms block, so their trims are
  unchanged. On the take that was cut, the last programme block moves **139.8 s → 151.5 s** and the
  trim point **141.8 s → 153.5 s of a 154.49 s take** — the entire fade is kept, and the run-out at
  −70 dB is still rejected against a learned floor of −67. `One More Chance` fades too and gains
  1.7 s of it back.
- **The learned floors are what make the relative design worth keeping**: −67 dB on the quiet
  transfers and **−56 dB on `One More Chance`**, whose lead-in and run-out both sit near −55. A
  fixed gate that worked for the first four would either cut that disc's fade or never stop on it.
- **One behaviour change worth knowing: a loud enough thump in the run-out now restarts the hold.**
  `One More Chance` has one 100 ms block at 188.5 s reaching −19 dBFS peak that clears the gate by
  0.09 dB, so the stop is deferred by another hold. It costs seconds and keeps audio, which is the
  safe direction, and it is the same bargain `GapBetweenTracksDoesNotEndTheTake` already makes.
- **`RecordingLevelAnalyzer` still uses the percentile form and is deliberately not changed here.**
  It shares `ProgramBlockClassifier` but works offline over its whole block history for a *gain
  recommendation*, where the same weakness costs a slightly wrong `NoiseFloorDb` readout rather than
  destroying audio. `ProgramBlockClassifier.ThresholdAboveFloor` is the shared gate; the percentile
  entry point stays for that caller.
- **Reviewing the fix found that it had introduced a worse failure than the one it removed, and the
  mechanism is the ratchet.** The floor only ever moves down, so unlike the window it replaced it
  cannot recover from one bad reading — and **arming the recorder and then cueing the stylus by hand
  puts seconds of dead input at the head of the take**. Ten blocks of it fill the floor with values
  no disc can beat, and the gate is pinned at the absolute minimum **for the whole side**. Measured
  by prepending 3 s of digital silence to `One More Chance`, whose groove noise is −52 to −57: the
  floor is never learned, the threshold never leaves −55, and **the run-out is classified as
  programme, so the take never stops**. The old sliding window scrolled past the silence and did
  not have this failure. `MinimumMediumFloorDb` (−80 dB) is the fix — below it a block is a dead
  input rather than a groove. The separation is wide and measured: the quietest real lead-in in the
  corpus reads **−73.8** and digital silence reads **−86 to −90**. With the guard the same padded
  file learns its floor at 3.2 s and rejects every run-out block.
- **The clamp is not a substitute for that guard, which is what made the hole easy to miss.**
  `Math.Max(MinimumProgramBlockDb, floor + separation)` keeps a nonsense floor from producing a
  nonsense *number* — an infinitely low floor still yields −55 — so nothing looks wrong. What it
  cannot do is stop that −55 being **permanent**, and −55 is the wrong answer for any transfer
  whose groove noise is louder than it.
- **Feeding the run-out detector the needle-drop pre-roll was built, measured, reviewed and
  withdrawn. It is not in the code; this is why.** It looked obviously right — the promoted pre-roll
  *is* the head of the take, so the detector should hear it, and the trim arithmetic is indifferent
  (`SamplesSinceProgram` is a backward offset from the caller's own total).
- **What it bought, measured: nothing.** `EnqueueNeedleDropPreRoll` caps the queue at
  `sampleRate * channels * 0.25`, which at the 100 ms capture buffer is **two blocks — two of the
  ten the floor is read from**. Feeding the detector only the last 0.25 s before the music, the
  floor arrives **0.7 s earlier on `One More Chance` (185.1 → 184.4 s, still deep inside the
  run-out)** and **not at all on `Dancin'` (7.6 → 7.7 s, which is noise)**.
- **What it cost: the one safety invariant this detector states about itself.** "Nothing triggers
  until programme has been heard at least once" held *structurally* on the auto-start path, because
  the detector was created after the contact packet and so began on lead-in groove.
  `EnqueueNeedleDropPreRoll` runs **before** `NeedleDropDetector.Process`, so the packet carrying
  the stylus contact is always in the pre-roll — and the drop can register as programme. Measured
  over the six transfers, **three of six takes have their hold armed by the stylus contact rather
  than by music**: `One More Chance` at 0.3 s (−48.9 dB), `Super Do Nothing Day` at 0.3 s (−44.2),
  `Watching The World Go By` at 0.4 s (−53.2). The longest lead-in gap after arming is **2.6 s
  against a 5 s minimum hold**. Not reachable on this corpus, but the margin becomes the length of
  a lead-in groove, which varies by pressing, and if it is ever exceeded **the take stops in the
  lead-in and is trimmed to about two seconds**. A structural guarantee traded for 0.7 s on one
  disc is not a trade.
- **The three takes where it did not arm are the median doing its job**, and worth reading next to
  the three where it did: a contact transient short enough to sit inside one or two 10 ms
  sub-blocks is killed by the block median, and a stylus that settles over a longer moment is not.
  "A single click cannot lift a run-out block" is true of clicks and not of a stylus landing.
- **The gap it was aimed at is an arithmetic mismatch: 0.25 s of pre-roll against the 1.0 s the
  floor needs.** Enlarging the pre-roll past a second would close it and changes what is
  *recorded* — every needle-drop take keeping more lead-in noise at its head — so it is a product
  decision, not a fix. **Lowering `FloorBlocks` is the wrong answer**: taking the tenth lowest
  rather than the lowest is the entire outlier resistance, and the entry above is what happens when
  one bad block reaches the floor.
- **Carrying the floor over from the monitor phase was the proposed fix for the auto-start gap. It
  is measured, it is wrong, and the gap it was aimed at does not exist.** `NeedleDropDetector`
  triggers on **stylus contact**, so on the auto-start path the monitor phase is by construction
  *before the stylus touches the record*: it holds the input chain's own noise and no groove noise
  at all. What it would carry over is the quietest thing in the signal path, permanently, into a
  gate that cannot rise again.
- **Simulated on `One More Chance` by prepending five seconds of live input noise to the take, which
  is what seeding its floor amounts to**: as recorded the floor is **−55.57**, the gate −45.57, and
  **1 of 81 run-out blocks** reads as programme (the known thump) — it stops correctly. With a
  monitor floor of −70 dB the floor becomes −69.94, the gate **clamps to −55**, and **59 of 81
  run-out blocks read as programme** — the take never stops. Identical at −75 dB. The clamp is the
  mechanism: `max(−55, floor + 10)` collapses every floor below −65 onto the same −55 gate, and a
  disc whose run-out is louder than −55 needs a floor from **its own groove** or none at all.
- **The lead-in groove is already inside the take, which is why there was nothing to fix.** All six
  transfers show the same head: at most 0.3 s of pre-contact input, a loud contact transient at
  0.1–0.3 s, then two to three seconds of lead-in groove, then music. The detector sees every bit of
  that and learns its floor at **1.3 s on `Dancin'` and 3.2 s on `One More Chance`**.
- **The experiment that produced the phantom gap was mislabelled: `skip=4` removes the lead-in from
  the take, which models a manual start made *after the music began*, not the needle-drop path.**
  That case is real but rare, self-corrects when the run-out supplies a floor, and cannot be told
  apart at the decision point from the needle-drop case that carrying the floor would break.
- **Three predictions about this path in a row were wrong, and all three failed the same way** — the
  pre-roll's size, then what the monitor phase contains, then whether the gap existed. Each was a
  sound inference from the code's shape and each took ten minutes to disprove with the corpus
  already sitting on disk. **Nothing about the capture path should be asserted from reading it.**
- **The order statistic is why the pre-roll would have been harmless to the floor, and it is the
  same property that condemns the monitor phase.** On an interface whose own noise sits between −80
  and the groove, two or three sub-groove blocks leave the **tenth** lowest of ten a groove value,
  so the floor does not move; fifty blocks of monitor phase fill all ten. Two or three blocks cannot
  move an order statistic and fifty can. The pre-roll was withdrawn over the hold, not the floor.
- **The floor cannot rise, so a run-out more than the separation noisier than the quietest second
  of the take will not be recognised.** Not reachable in this corpus — lead-in and run-out sit
  within a few dB of each other on all six transfers — but it is the price of the ratchet and the
  first thing to look at if a dirty run-out ever fails to stop a take.
- **Three tests pin it and all three fail against the old detector** (checked by stashing the fix,
  not assumed): `AFadeOutIsNotARunOut`, `MusicsOwnDynamicsAreNotItsNoiseFloor`, and
  `TheRunOutIsStillFoundAfterATrackLongerThanAnyWindow`. They need a `DynamicMusic` generator
  rather than the existing steady one — **the old code passes every one of them on a steady tone**,
  because a tone has no spread, `HasSeparableFloor` correctly refuses it, and the fallback gate is
  the right answer by accident. The bug only exists for material with about 10 dB of its own
  dynamics, which is to say for music.

## Vertical surface noise: the de-crackler was working on the wrong signal

A record whose crackle would not come off. Measured, the shipped de-crackler barely moved it — and
the same de-crackler, unchanged, removes most of it once one thing runs before it. Nothing here
changes `Decrackle`; three stages were added to the workbench and the ordering between them is the
whole result.

- **Surface noise on a record is vertical, which means it is the side signal, which means a
  per-channel repairer is fighting it twice with two different guesses.** On the run-out of a real
  transfer **78% of crackle events are anti-phase** (L·R below zero across the event) and 77% appear
  in both channels: one defect in the difference signal, not two defects in two channels. Each
  channel's autoregressive model therefore sees a *different realisation* of the same tick, repairs
  that, and summing the channels back reconstitutes what the other one still holds.
- **Measured on the run-out of `One More Chance`, ticks above −45 dBFS: 318 untouched, 263 after the
  shipped de-crackler, 54 after collapsing the side first.** The de-crackler alone removes 17% and
  the chain removes 83%, from the same detector at the same threshold. `VerticalNoiseCorpusTests`
  asserts **both** halves — the success and the near-failure — because reordering the stages loses
  the effect entirely while still looking like a chain.
- **The rise is a property of records and the programme ratio is a property of the pressing, and
  conflating them is the trap.** Across five transfers, side-to-mid rises from the programme to the
  quiet end by **23.9, 21.3, 26.1, 20.7 and 20.2 dB** — every one, including the widest stereo record
  in the set. That says the noise is vertical and says nothing about whether music is there with it.
  Only the **programme** ratio answers that. Through the shipped `CleanupAnalyzer` gate the same five
  read **−15.9, −15.0, −11.5, −9.2 and −7.6 dB**, and the recommendation ramps between
  **−14 dB (discard the side) and −8 dB (leave it alone)** — so two are collapsed, one is left
  alone, and **two land in between and get a partial reduction** at 40% and 80%.
- **That middle pair is the honest result and the exploration did not have it.** A first pass with a
  hand-written programme gate read a clean gap between −12.3 and −9.8 and made the anchors look
  like a classification; the analyzer's own 60th-percentile gate reads −11.5 and −9.2, which is
  no gap at all. The ramp is what makes that survivable — a threshold would have decided those two
  records by itself — and the card says “some of what goes is music” rather than claiming a pressing.
  **Five files from one collection is not a corpus**: it recommends on a control the user can see and
  move, and never applies silently. The declip calibrations were fitted this confidently five times
  and held out four.
- **Half the energy was below 40 Hz and nothing in the workbench was filtering it.**
  `CleanupAnalyzer.EstimateRumble` has always measured it; its result reached the *rack* chain and
  never `RenderOwnedWork`. On that transfer's run-out **48% of the total energy sits under 40 Hz**,
  peaking at 10.8 and 16.1 Hz — tonearm resonance excited by warp. `Restoration.RemoveSubsonic` is a
  24 dB/octave Butterworth pair; at a 30 Hz corner it measures **30.9 / 21.7 / 7.3 dB down at 12 /
  16 / 25 Hz** and moves 120 Hz and above by less than 0.5.
- **Its placement is measured and it does *not* do what it looks like it does.** High-pass then
  collapse then de-crackle gives 15 ticks and 4.2% of samples repaired; collapse then de-crackle then
  high-pass gives 14 and 4.4% — the same answer either way round. Nor does it rescue
  `AnalyzeClipping` from inflated plateaus: removing the rumble moved that transfer's peak by
  **0.09 dB**. It runs first so that every downstream *measurement* — the automatic noise profile,
  the per-block robust scales, the levels on the cards — is taken on the audible band rather than on
  the rumble. That is a reason about the readouts and not about the audio, and it is written up as
  such rather than as more.
- **The cost on music is small, and it is the shipped Janssen repair that makes it so.** Over the
  five transfers, de-crackling after the chain costs **0.19 to 0.64 dB** of high-frequency energy. An
  exploration with a cubic bridge in place of Janssen predicted 1.4 dB, which is a measure of how
  much of this tool is its interpolator rather than its detector.
- **De-crackle is not recommended below 3.0σ and the card says why.** At 2.5σ it repairs roughly
  twice as many samples and leaves *more* audible ticks than 3.5σ does — worse on both axes at once,
  which is the "an aggressive de-crackler sounds dull" failure `Decrackle.cs` already warns about,
  reproduced on real material.
- **Broadband noise reduction is switched off on this file and it is right to be.**
  `EstimateNoiseToProgrammeDb` reads **24.2 dB** here against a `NoiseDepthCeilingDb` of 10, so
  `SuggestReductionDepthDb` returns 0 and both the workbench and the standalone tool decline.
  **The rule is not loosened**: it is validated over 108 cells and takes cells-worse-than-doing-
  nothing from 46 to 15. It is an RMS ratio and crackle is impulsive, so a surface can be plainly
  audible while its floor sits 24 dB under the programme. That is a limit of the rule's scope rather
  than a fault in it, and the fix is that de-crackle is now in the chain — so both messages name the
  card that does apply instead of only declining.
- **Linking de-crackle across mid/side inside `Decrackle` was measured and rejected.** Detecting on
  M and S and repairing the union in both reaches 0.5 audible events a second against 1.6 for
  per-channel — but on music it repairs **12–14% of samples against 6–8%** and costs **2.09 dB of
  high frequencies against 1.10**. Collapsing the vertical noise is strictly better than trying to
  repair it in place, wherever the pressing allows it.

### The preview boundary, where both new stages had a hole

- **The high-pass needed a warm-up term and the flat fallback pad was nowhere near covering it.**
  With hum and noise both off, `needsContinuousState` was false and the preview fell back to
  `max(NrFftSize * 2, rate / 10)` — 4,410 samples. A 30 Hz corner at 44.1 kHz needs **12,670**.
  Measured end to end against a whole-file pass rather than against the pole arithmetic that chose
  the number: the planned lead-in leaves **−189.0 dB** of startup error at the boundary and the flat
  pad leaves **−84.8 dB**. That is an audible thump, and it would have appeared only when the other
  two stages happened to be switched off.
- **De-crackle needed the same fix for a reason that has nothing to do with state, which is why it
  is easy to miss.** It carries no filter memory at all — but it fits one autoregressive model per
  block on a grid anchored at index zero of whatever array it is handed, so a preview buffer
  starting anywhere else fits its predictors to *different audio* than the render does, and the two
  disagree about what is crackle. Same class as the STFT hop alignment already there, same fix.
  The alignment is a **least common multiple** rather than a maximum: the default block is a whole
  number of hops, but the block is `max(order * 8, BlockLength)` and neither is required to be one.
- **`RestorationPreviewPlanning` had no test at all before this.** It does now, including the
  boundary-error comparison above — measuring the plan against the filter rather than against the
  formula that produced it, which would only check one arithmetic against itself.

### The depth ceiling is a setting now, and the default is untouched

`SuggestReductionDepthDb` declines on a record whose crackle is plainly audible, because the
estimate behind it is an RMS ratio and crackle is impulsive — `One More Chance` measures 24.2 dB
against a ceiling of 10 and gets nothing. The rule is right and the user is not wrong, so the
ceiling moved from a constant to `AppSettings.NoiseDepthCeilingDb`, **defaulting to the same 10 dB
and clamped to 10–40**.

- **Raising the default was the obvious change and it is the one not made.** The rule is measured
  over 108 cells: a fixed depth scores −0.85 dB segmental, worse than leaving the audio alone, and
  scaling takes cells-worse-than-doing-nothing from 46 to 15. Raising the ceiling hands that back.
  At 30, a file whose hiss is already 15.9 dB down goes from 0 dB applied to 5.6 — and that is the
  severity where a fixed depth measured −8.13. The setting lets one installation take that trade
  without every installation taking it, and the Settings line states the trade in those terms rather
  than warning about the control.
- **The floor of the range is 10 and not lower, because there is nothing below it to offer.** Every
  ceiling between 8 and 10 dB scores within 0.01 dB of the best over those cells, so going under the
  default only protects material that did not need protecting. The top is 40, past which the scaling
  does nothing a fixed depth would not do.
- **Making it adjustable reintroduced a defect the estimator had already been fixed for, by another
  route, and the mechanism is worth stating.** `EstimateNoiseToProgrammeDb` says *no reading* by
  returning the ceiling — that is how "nothing to measure" is distinguished from a measured zero,
  which means "the programme is no louder than its own floor" and asks for full depth. Hand a fixed
  10 to a rule running at 30 and "no reading" stops meaning nothing to remove and starts meaning
  **two thirds of the requested depth**: an empty buffer asks for 6.7 dB of reduction. So the
  estimator takes the ceiling too, and its sentinel is expressed in whatever ceiling is in force.
  `NothingToMeasureAsksForNothingAtEveryCeiling` pins it, and was checked by mutation — pinning the
  sentinel back to the constant fails it at 20, 30 and 40 and passes at 10, which is exactly the
  signature of the bug.
- **One ceiling per decision, read once.** The workbench caches it beside `_noiseToProgrammeDb` at
  analysis time rather than reading `AppSettings` at each use, and `OnReduceNoise` takes it once for
  both calls. An estimate and a rule evaluated under two different ceilings are not a pair, and the
  readout would then describe a depth the render did not apply — the disagreement every readout in
  that dialog is arranged to prevent.
- **Rendering the General settings page found nothing, but the check was worth having and the first
  version of it was worthless.** That page is a bare `StackPanel` where the Audio page is a
  `ScrollViewer`, so content added past the bottom is clipped rather than scrolled. Asserting on its
  `ActualHeight` proves nothing — a stretched panel reports its host's height whatever the content
  does — and it read 471 px inside 471 px. `DesiredSize.Height` is the number that answers the
  question: 263 px of content in 471 px of room.

### Wiring notes for the next stage added here

- **`RestorationSettings` is positional and `CaptureSettings` fills it positionally**, so a field
  added at the wrong index compiles and is silently wrong. It is not
  `RestorationRecommendations.Settings`; nothing copies one into the other.
- **`progressSpan / 5.0` is now `/ 8.0`** — eight slots, seven `at += step` increments, the last
  consumed by the blend. A stage added without its increment silently compresses every bar after it.
- **`OnPresetChanged` sets every control for Gentle/Balanced/Strong**, so a new control omitted there
  keeps whatever the Analyzed pass left — "Gentle" would carry a Strong-analysis high-pass. The side
  level is deliberately **not** set by the presets: how far it may go is a fact about the pressing,
  which a strength preset knows nothing about, and collapsing a stereo record is not a thing
  "Strong" should mean.
- **De-crackle is the first card here whose recommendation is not a measurement of the thing it
  removes.** Crackle sits below the click detector's reach by definition, so nothing counts it; it
  rides on impulses having been found at all. `DescribeCrackle` says so on the card, because a
  control that turns itself on for a reason the user cannot see is one they cannot overrule.
- **Rendering the three new cards at the 860 px minimum found nothing this time**, which is worth
  recording as the exception: all three get 370 px and their evidence lines wrap to 13, 38 and 51 px.
  `CardCaption` and an explicit `TextWrapping` on the evidence lines are why — the fault they would
  otherwise have is the one already on record, a caption cut to "without shifting stereo alignm".

### Review fixes: what wiring de-crackle into the chain actually cost

A review of the three stages above found six things. The largest was not a bug at all — it was that
nobody had asked what the new stage costs.

- **De-crackle is 400× the other two new stages put together, and it was switched on by default.**
  Measured on five real transfers: the high-pass runs in 140–193 ms and the side scale in 19–24 ms
  on a three-minute side, and de-crackle runs in **34.3 to 68.3 seconds** — 0.19 to 0.36× realtime.
  It repairs about 4% of *every* sample rather than a bounded list of events, and Janssen is ~35× a
  linear bridge. On a 22-minute LP side that is four to seven minutes per Apply.
- **`RenderOwnedWork` is also the preview path, so the same cost lands on every parameter change.**
  Over the 12 s preview window the rest of the chain costs **428–920 ms** and this stage costs
  **2.6–5.7 s**: a **7.2× to 10.7×** slowdown. The guess that A-SPADE already made previews this
  expensive was wrong and checking it is what made the finding solid — these transfers carry **no
  clipping at all**, so declip costs 1–2 ms and there was nothing already paying that bill.
- **What saved it from being unusable is that `BeginOperation` cancels the superseded render.**
  Dragging a slider cancels and restarts rather than queueing, and `Decrackle` checks its token
  every 64 events, so the cost is *latency after you stop moving* rather than a backlog. Worth
  knowing before treating a slow stage in this dialog as a hang.
- **The fix is the parallelism this repo had already reasoned about and never claimed.**
  `Janssen`'s note records that per-channel parallelism "would halve it and is safe — each channel's
  samples are independent — but has not been done". Measured, it is **exactly 2.0× on all five
  files**, taking the stage to 0.09–0.18× realtime. `DeCracklingChannelsInParallelMatchesDoingThemInTurn`
  asserts the claim underneath it: identical output, bit for bit, however the work is scheduled.
- **The longest stage in the chain reported no progress at all.** `Decrackle.Process` takes an
  `IProgress<double>` and it was being passed `null`, so seven of eight progress slots filled
  quickly and the bar then sat still for the stage that dominates the wall clock.
  `ChannelFractionProgress` combines the channels' independent fractions; the read of the array
  races the other channels' writes **on purpose**, because the value is a number a 10 Hz timer
  samples and a lock between two workers would cost more than the exactness is worth.

Three smaller ones, all of a kind this file already warns about:

- **`AppSettings` was being read on a worker thread.** `OnReduceNoise` reads the depth ceiling
  inside the transform lambda, which `RunRangeTool` runs inside `Task.Run` — two lines under a
  comment reading *"captured up front, like the profile above"*. This is exactly the reader
  `AppSettings`' own remarks predicted: *"it is read from forty-odd places across the audio layer
  and the next one added will not know that."* Hoisted.
- **`_impulsesFound` did not follow the analysis that ran.** It was set in `OnLoaded` only, so after
  a click-sensitivity change re-ran `AnalyzeClicks`, the crackle card kept quoting the first pass
  while the header showed the second. `RefreshAnalysisAsync` updates it now — the same rule
  `DescribeDeclipChoices` exists to enforce.
- **Reducing the side over a *selection* leaves a seam, and only that stage needed to say so.** A
  notch or a gate at a range boundary is subtle; a stereo image snapping to mono and back is not.
  The card says so when the workbench is scoped to a selection and the side is actually being moved.

And one finding was against the tests rather than the code:

- **A test was asserting that today's tool stays bad.** `CollapsingTheSideFirstIsWhatMakesTheDeCracklerWork`
  had `afterCrackleOnly >= before * 0.6` — pinning the de-crackler's weakness on un-collapsed stereo
  as a *requirement*, so anyone who later improved the detector would fail the test with an
  improvement. The ordering claim is carried by the two assertions that survive: the chain works, and
  it beats crackle-only by a wide margin. The crackle-only figure is reported instead. Same
  over-specification the two declip chooser tests were rewritten for.

**One finding was raised and deliberately not acted on.** `_noiseDepthCeilingDb` is written on the UI
thread and read on the worker without a barrier. It is safe as arranged — `_source` is assigned first
in the same continuation with no `await` between, and `QueueParameterRefresh` returns early while
`_source` is null, so no render can observe it unset — and it is the same shape as the pre-existing
`_noiseToProgrammeDb`. Adding a barrier for one field and not the other would be worse than either.

## The noise-mask headroom is not reachable from the profile, and that is now measured

The +7.15 dB the oracle Wiener mask showed over the shipped gate invited one more family of cheap
candidates before conceding the roadmap's "needs a model": estimate the Wiener gain from the same
learned profile the gate already uses. Built as an exact copy of `ReduceNoise` with only the per-bin
target changed — same STFT policies, same temporal smoothing, same median-of-three — and measured
over **120 cells** (54 from `C:\Windows\Media`, 66 from the eleven WAV vinyl transfers now in
`Music\mymusic`; the harness's corpus-1 AIFFs no longer exist on disk, see below). **All variants
were deleted, and this entry is the record.**

- **Plain Wiener (`1 − N²/|X|²` clamped to the depth floor), noise power swept from −3 to +4 dB
  around the profile's own level.** Over-subtraction loses everywhere — at +4 dB it wins 0 of 120
  cells. At the neutral scale it beats the gate in 91–96 of 120 at fixed depth, but the wins live at
  the quiet severities **where the shipped depth rule already declines to fire**: under the shipping
  config (depth rule + dialog defaults) it is **+0.47 against the gate's +0.36 mean, wins only 30 of
  65 decided cells, and moves the below-do-nothing count from 15 to 16**. Its one real property is
  the tail — worst cell −15.06 against −16.86.
- **Decision-directed Wiener (Ephraim & Malah's ξ estimator with a Wiener gain — the temporal half
  of the MMSE-STSA that lost here by 136 dB, without the STSA amplitude rule that lost it).** The
  strongest candidate measured: at fixed depth α=0.90 beats the gate in **117 of 120 cells and at
  every severity, on both populations**. And under the shipping config it collects **+0.10 dB of
  mean** (+0.46 against +0.36), wins 42 of 65 decided, improves the worst cell −16.86 → −15.78 —
  and still nudges the below-do-nothing count 15 → 16 (`Ring04.wav` @18 dB, +0.02 → −0.26), rescuing
  no cell in return.
- **The fixed-depth aggregates flatter every candidate, which is the reading to guard against.**
  dd90's 117-of-120 headline shrinks to +0.10 dB because the severities where it wins big are the
  ones the depth rule already routes to doing nothing. Any future candidate must be judged on the
  composed shipping config, not on the fixed-depth table.
- **Why the headroom does not move: the profile is stationary and so is the information in it.**
  The oracle knows the per-frame signal/noise split; every candidate here reshapes the same
  time-invariant noise estimate the gate already thresholds on, and the harness's hiss is stationary
  by design, so even a minimum-statistics tracker could only re-derive the profile. Three estimator
  families have now been measured against these cells — MMSE-STSA, subtraction-scaled Wiener,
  decision-directed Wiener — and none collects more than a tenth of a decibel where it ships. **That
  is the brief for a model, sharpened: it has to estimate the per-frame split, and nothing cheaper
  than that will move the number.**
- **The harness's corpus-1 AIFFs no longer exist on disk, and corpus 1 is now the WAV transfers, by
  David's decision.** `Music\mymusic` carries his newer WAV vinyl transfers of the same records —
  the run-out detection corpus — and `DeclipCorpus.Recordings` accepts them as `1-record` now. The
  old aiff-only guard existed to exclude a different population (the deleted internet WAVs), and
  keeping it once the folder held only genuine transfers excluded the exact material the corpora
  exist for. **Corpus-1 figures recorded before 2026-08-24 are from different audio and must not be
  compared against fresh runs** — the record already holds that corpus-1 numbers stopped reproducing
  once before for exactly this reason.
- **How the hole was found: the wow corpus test failed rather than shrank.**
  `CorrectingPlantedWowRemovesTimingErrorRatherThanAddingIt` ran with **0 cells and 38 recordings
  excluded** under `WAVELAB_CORPUS=1`: corpus 2's longest file is 12.8 s against the 20 s a 0.7 Hz
  wow needs, so the wow harness stood entirely on corpus 1, and `Assert.NotEmpty` failed. The wow
  figures committed on 2026-08-24 were taken while the transfers were still reachable; the corpus
  change re-measures them.
- **Re-baselined the same day, and the whole corpus battery passes on the blessed corpus.** Declip:
  corpus 1 gives **44 of 44 cells over do-nothing, mean +6.37 dB, worst +0.81** — remarkably close
  to the AIFF corpus's +6.40, being the same records — with corpus 2 reproducing to the decimal.
  The wow tests reproduce this morning's committed figures **exactly** (237 → 197 at 2.4%, the
  128-against-133 wash at 1.2%, reads 1.070/0.798/0.716/0.733), which settles that they were
  measured on these WAV transfers. The oracle noise headroom re-reads **+7.17 dB** on the new
  120-cell population against the recorded +7.15. Hum, spectral heal, silence, crackle and the
  click corpus all pass; the click harness rightly **excludes all eleven transfers from planted-
  click cells as already clicky** (1.5–4.1 events/s — they are real records), so its planted
  recall figures stand on corpus 2.

## Resizing a selection by its edges

A selection could be drawn and not adjusted: getting the out point a hundred samples further along
meant drawing the whole thing again. Both edges are now draggable, and none of it is a new mode.

- **A resize is an ordinary selection drag anchored on the edge that is staying put**, which is the
  whole implementation. `BeginDrag` sets `_dragAnchor` to `SelEnd` for a press on the start edge and
  to `SelStart` for a press on the end edge, and `ContinueDrag` is the code that already existed.
  The min/max, the clamping, the flip when the dragged edge passes the anchor and the live update
  with no undo step behind it all come out identical to building one, because they *are* building
  one — a second code path would be a second set of those behaviours to keep in step.
- **The one thing an edge drag does differently is skip the travel threshold**, and it has to.
  Building a selection needs a pixel of movement before it becomes one, so a click does not select a
  sample; an edge drag is already past that on its first move, and following the hand exactly is
  what lets one edge be dragged onto the other to clear the selection. With the threshold it stuck
  at its last width instead.
- **The playhead sits on a selection edge every time, so precedence had to be decided rather than
  left to distance.** Building a selection sets the cursor *and the playhead* at the drag anchor
  (`SetCursor(_dragAnchor, clearSelection: true)`), so the edge the user just drew always has the
  playhead underneath it — and the playhead's 8 px grab is wider than the edge's 6. Nearest-wins
  would make that edge the one edge that could never be grabbed again.
- **The split is vertical: the edge wins in the body of the wave, the playhead keeps the top 12 px.**
  That band is not arbitrary — it is the triangle `OnRender` already draws at the top of the
  playhead, so it is the grip the user can see. Nothing new is drawn for any of this; the resize
  cursor on hover is the only feedback, which is why there is no mockup for it.
- **`WaveformView.GrabAt` is pure and is where that judgement lives.** Distances are arithmetic;
  what beats what is a decision, and it is the part worth pinning without a mouse. An edge scrolled
  off the view returns infinite distance rather than clamping — a selection whose start is off to
  the left is not something the pointer at x=2 is near.
- **The drags themselves go through `PerformDrag`, the same seam `SpectralEditorView.PerformGesture`
  uses**, with the mouse handlers as thin wrappers over `BeginDrag`/`ContinueDrag`/`EndDrag`. Both
  halves were checked by mutation rather than assumed: disabling the edge branch in `GrabAt` fails
  **10 of the 26** tests in `WaveformViewTests`, and setting `PlayheadGripHeight` to zero fails
  exactly the two that pin the band.
- **`EndDrag` clears the flags before `ReleaseMouseCapture`**, because releasing raises
  `OnLostMouseCapture`, which ends a playhead seek of its own. That ordering was in the old handler
  and is easy to lose when the branches are pulled out into a state machine.

### Shift+click to extend

The keyboard half of the same gesture, and it reuses the same anchor.

- **Shift is read before what is under the pointer**, so it means one thing wherever the click lands
  — including on the playhead, which would otherwise take the press as a seek. It leaves the drag in
  exactly the state an edge grab does, so a shift-click can be kept dragging rather than being a
  one-shot.
- **`WaveformView.ExtendAnchor` splits on the selection's midpoint rather than comparing distances**,
  which is the same answer inside the selection and the right one outside it: a click past either end
  is nearer the end it is past, so shift-clicking beyond the selection lengthens it instead of
  collapsing it onto the nearer edge. With nothing selected there is no far edge and the cursor is
  the anchor, so an ordinary click followed by a shift-click selects between the two.
- **A shift-click applies on the *press*, which no other press here does**, because a click is a
  complete gesture and every other press only arms a drag. That one line in `BeginDrag` is the whole
  difference and it is the part a test seam has to be able to see.
- **The seam could not see it, and a mutation is what said so.** `PerformDrag(p, p)` looks like a
  click and is not one: a real click raises no `MouseMove`, so anything reached only from
  `ContinueDrag` never runs — but a zero-length drag runs it anyway, and `_draggingEdge` skips the
  travel threshold, so the selection landed from the move handler. Deleting the press's own
  `SelectBetweenAnchorAnd` left all 38 tests passing. `PerformClick` is press-and-release with
  nothing between; the five shift-click tests go through it and now fail against that deletion.
  **The general form: a seam that models a click as a degenerate drag cannot test what a press does
  on its own.**
- **`APlainClickThatNeverMovesSelectsNothing` is the other half** and is the reason the travel
  threshold exists — shift-click is the deliberate exception to it, so both sides are pinned.

## The subsonic residual sounds like it took the vocals, and all of it is phase

**Keep what was removed** on a subsonic-only pass produces a file with the vocals plainly audible
in it, and the obvious reading — that a 30 Hz high-pass is reaching into the midrange — is wrong.
Measured against the pair on disk: a 184 s 44.1 kHz stereo transfer and the residual that pass
produced, which sum back to the original by construction, so the filter's own response can be
recovered by division.

- **The filter is exactly the filter it claims to be.** Recovered that way it is a 4th-order
  Butterworth high-pass at **30 Hz, matching the analytic curve to 0.002 rms over 8–400 Hz**. What
  it takes out of the vocal band: **−0.25 dB at 100 Hz, −0.066 at 200, −0.026 at 315, −0.010 at
  500, −0.002 at 1 kHz and 0.000 above 2 kHz.** There is no level to lose there and none is lost.
- **Everything audible in the residual above the corner is phase, and it is accounted for to within
  half a decibel with nothing fitted.** `RemoveSubsonic` is minimum phase, so it still rotates far
  above its corner — **+45.5° at 100 Hz, +22.5° at 200, +4.5° at 1 kHz**. Subtracting two signals of
  equal magnitude differing by θ leaves `2·sin(θ/2)`, so the residual's level follows from the phase
  alone: predicted **−9.11 / −17.05 / −23.07 / −29.09 / −35.11 dB** at 200 / 500 / 1 k / 2 k / 4 k
  against **−8.72 / −17.01 / −22.89 / −28.80 / −34.86** measured. Four decades, every band inside
  0.4 dB.
- **A residual like this cannot be quiet in the midrange, and that is arithmetic rather than a
  calibration.** The complement of an Nth-order high-pass is not an Nth-order low-pass: `1 − H` has
  a numerator of order **N−1**, so it rolls off at **6 dB/octave** however steep the filter is. The
  file measures about **2 dB per third-octave from 200 Hz to 16 kHz**, which is that slope exactly.
  Expect the same of the residual of any minimum-phase filter in this app.
- **Near the corner the residual is *louder* than the original, and that is the same fact.**
  `|1 − H|` overshoots unity around a Butterworth's cutoff: **+1.6 dB at 25 Hz, +3.9 at 31.5, +3.8
  at 40** against the original's own level in those bands. Nothing is added — the two signals are
  close to antiphase there.
- **The one thing the file said about the render rather than about the maths is the wet mix.** About
  **10% dry is blended back** (wet ≈ 0.896, read off the bottom of the band where `H` is zero), so
  10 Hz comes out **19.7 dB down overall where the filter's own response is 40 dB**. A subsonic pass
  that looks weak is worth checking against the mix control before the cutoff is touched.
- **The consequence is the one this file already records for wow and flutter: a residual guaranteed
  to be misread is worse than no residual.** Wow is excluded from capture because a whole-file
  waveform residual reads a good time-base correction as total failure; this is the same shape of
  trap, in that anyone who listens to a high-pass residual will conclude the filter ate their music.
  Excluding the stage, or saying on the residual tab that a high-pass residual is mostly phase, is
  the choice; leaving it to be discovered is not.

### The mix was the binding constraint, and nothing on screen said so

- **The workbench's output mix ships at 90% restored, and that is a 20 dB ceiling on every stage in
  the chain.** The blend is applied once, to the whole chain output — `out = dry·(1−wet) +
  processed·wet` — so whatever share of the original it returns is a floor under everything the
  chain removed. On the transfer above, the high-pass's own **40 dB at 10 Hz came out as 19.7**. It
  is not specific to this stage: the notch bank's measured **42 dB of hum is capped at the same 20**,
  and so is anything else that works deeper than that. The ceiling was found by measuring a residual
  rather than by reading the code, which is the whole argument for saying it out loud — a stage
  underperforming its own recorded figures reads as a broken stage.
- **The readout says it; the default does not move** (design: `docs/design/output_mix_ceiling.png`).
  “Ceiling 20.0 dB · 10% dry returns over every stage”, from
  `RestorationWorkbenchDialog.DescribeOutputMix`, pure and unit-tested without a window exactly as
  the declip and noise-depth lines are. 90% stays the shipped value: it is a defensible safety
  margin for a chain doing five destructive things at once, and the answer for someone who wants the
  full effect is a slider they can now see the reason to move. **Amber is on `Bypassed` and on a
  fully dry mix only** — a ceiling is a fact about a setting, and colouring a fact like a fault is
  already on record here as teaching users to distrust the colour.
- **The widest wording is the fully dry line, not any ceiling**, because the deepest ceilings carry
  the shortest detail — a mix near full has almost no dry share left to describe. `OutputMixRenderProbe`
  measures that line in the built control at the dialog's 860 px minimum: **330 px of room, 282 px
  wanted, one line 13 px tall**. The character count in `OutputMixReadoutTests` is only the cheap
  bound that catches a wording change growing without limit. **The mockup said 339 px and it was
  wrong by the width of a scroll bar**, because it came from the XAML column arithmetic rather than
  from a control — the same correction the noise-depth readout needed, where 365 px of estimate
  measured 370. The probe also checks the audition combo beside it, because this is the one readout
  in the dialog added to a row that has other columns to take room from.

## The edit history was already there; nothing could see it

`AudioDocument` has always kept a full linear history — two `List<Edit>` stacks, a byte budget, and a
name on every entry that reads well enough to put on screen (`Gain +3.0 dB`, `De-emphasis · RIAA`,
`Fade In (Equal Power)`). The only thing missing was a way to look at it. `Edit` is private, the
stacks are private, and the entire public surface was `CanUndo`/`CanRedo`/`NextUndoName`. So the
panel is a read model and two primitives, not a new engine.

- **The timeline is `_undo` in order followed by `_redo` reversed.** `_redo` is used as a stack, so
  `_redo[^1]` is the *next* redo and `_redo[0]` the *furthest future*: `T[i] = i < n ? _undo[i] :
  _redo[E-1-i]`. Concatenating the two lists the obvious way produces a list that looks entirely
  plausible and jumps to the wrong state, which is why `TheTimelineListsUndoneStepsAfterTheCurrent
  PositionInTheOrderRedoWouldReapplyThem` exists. The same reversal is what makes
  `TruncateHistoryFrom` drop from the *front* of `_redo`.
- **A jump raises one `Changed` and bumps `EditVersion` once**, and that is not an optimisation.
  `DocumentViewModel.OnDocChanged` re-anchors markers and regions, schedules a peak rebuild and
  queues a `.wlmeta.json` sidecar write; `MainViewModel.OnActiveDocumentEdited` requeries 35 commands
  and writes the status line. Per step, a ten-step jump pays all of that ten times and settles on the
  right answer only at the end.
- **The composed span has to be tight.** The single event carries `(start, removed, inserted)` for
  the whole run, and `OnDocChanged` puts any marker inside `[start, start+removed)` *at* `start` — so
  a lazy whole-document triple would collapse every marker to sample 0. The composition keeps the
  hull of the accumulated span and the step's span in current coordinates, growing the removed count
  by the samples on each side the step reached beyond it (`extra` and `deficit` count identically in
  both frames because everything outside the span maps one to one). Two properties fall out and both
  matter: a run of same-length edits composes to `removed == inserted`, so the marker loop is skipped
  exactly as it is when stepping; and a run containing a `ReplaceAllOwned` composes to the whole
  document, which is what that step raises on its own anyway. `TheSingleChangeEventSpansEveryRegion
  TheRunTouched` asserts the *definition* — prefix equal, suffix equal — rather than a magic triple,
  because a wrong composition produces a span that reads as reasonable.
- **The budget is enforced once, at the end of a jump.** Enforcing mid-run could release entries
  while the loop is still counting against `_undo.Count`. Retained bytes are invariant under a
  stack-to-stack move, so deferring costs nothing. `Redo()` still does not enforce and `Undo()` still
  does — unchanged, and for the reason already recorded: `EnforceUndoBudget` refuses to drop below
  `_undo.Count > 1`, so an over-budget document only becomes reclaimable once entries migrate to
  `_redo`.
- **The savepoint is now dropped explicitly when it becomes unreachable**, from `TruncateHistoryFrom`,
  from `EnforceUndoBudget`, and from `DiscardRedo` — the last of which was missed on the first pass
  and is the commonest of the three. **Save, undo, edit** throws the forward chain away and the saved
  state can be on it, so without that call the invariant "the savepoint is reachable or absent" was
  merely nearly true and only `SavepointReachable` knew the difference. None of this changes an
  observable `Dirty` value: `_nextStateId` never reuses an id, so an unreachable savepoint already
  compared unequal to every state forever. What it buys is that the state says what is true, and that
  a future change which did recycle ids cannot resurrect a savepoint that no longer exists.
- **Markers and regions are warned about, not snapshotted.** `OnDocChanged` removes a region that
  collapses during a length-changing splice and undo does not bring it back — true of a single Ctrl+Z
  today; the panel only makes it easy to cross several at once. A per-step marker snapshot was
  rejected because markers are added, renamed and deleted with no `AudioDocument` edit at all, so
  restoring one would silently delete work no step on the list is responsible for — a worse failure
  than the one being fixed. Keying a stash is not stable either: the budget renumbers every combined
  index whenever it releases an entry. **The deferred alternative**, if it is ever wanted: stash only
  the regions the engine itself destroyed, keyed by a monotonic timeline index on `Edit` that does
  not exist yet, and re-add them when that position returns. Until then it is the `↕` badge, the live
  caution line, and a sentence in the help topic.
- The panel holds no state but the selected *position*, and re-reads the whole snapshot on
  `DocumentViewModel.HistoryVersion` — the arrangement `MarkersDialog` already uses with
  `MarkersVersion`, and for the same reason. `HistorySnapshot.Generation` is what separates "the list
  grew" (clamp the selection) from "the list renumbered" (go back to where the document is).
  `AudioDocument.JumpToHistoryPosition` **throws rather than clamps** on a stale index, because a
  silently wrong jump is much harder to notice than a thrown one; `MainViewModel.JumpToHistoryPosition`
  **absorbs and reports it**, because the panel is modeless and a stale click is not worth taking the
  session down. The asymmetry is deliberate: the engine states the rule, the shell survives it.
- **The rows are a view model behind a `DataTemplate`, not `ListBoxItem`s built in code.** That is a
  departure from `MarkersDialog`, and the reason is scale: a file has a handful of markers and a long
  restoration session can leave *thousands* of steps, so items built in code would construct every
  row's visual tree on every refresh — and a refresh is every edit, with `Trim Silence` alone
  committing one step per silence. With an `ItemsSource` the list builds only the rows it shows. The
  theme brushes are resolved once into fields for the same reason; `FindResource` per row per brush
  was seven resource-tree walks a row.

- **The panel is the first thing in the app that can reach a document from outside the progress
  overlay, and that is the dangerous thing about it.** The overlay covers the shell's rows and the
  shell is deliberately never `IsEnabled = false`; a modeless window of its own is covered by
  neither. And the tools do not commit against an identity: `RunRangeTool` guards on
  `start + count > Doc.Length` and `RunWholeFileTool` on `length != Doc.Length`, which was sound for
  as long as every route to a document went through the shell — but a **same-length** jump (`Gain`,
  `Reverse`, `Remove DC Offset`, most spectral edits) slips straight past a length check, and the
  tool then splices a result computed from audio that is no longer there. Silently.
  `MainWindow.LongOperationRunning` is a property rather than a field now, and setting it tells
  `MainViewModel.SetDocumentOperationRunning`; `CanMoveHistory` gates both history primitives on it
  *and* on the clipboard flag, and the panel asks again on every refresh rather than sampling once.
  `GuiActionStatusTests.TheHistoryCannotBeMovedWhileAnOperationOwnsTheDocument` fails if either flag
  is dropped. **`ProgressHost.IsBlockingVisible` is not a substitute**: `Blocking` is only assigned by
  `Tick()` after the 400 ms show delay, so it reads false for the first 400 ms of every operation.
- **Discarding a step is an edit to the samples, so it releases playback too.** `TruncateHistoryFrom`
  steps the document back before dropping anything — what is being thrown away must not still be in
  the audio — which makes it as much a splice as an undo. It was the one history path that did not
  call `PrepareForDocumentEdit`.

## Match Loudness: gain only, and say what it could not do

`LoudnessCompliance` already owned the rule — the suggested gain is the smaller of what loudness asks
for and what the true-peak ceiling allows, and the difference is *reported* because it is the amount
of limiting the master would need, which is a decision rather than an adjustment. `LoudnessMatch`
applies that rule across a set of tabs; it measures through `LoudnessCompliance.Measure` and adds
nothing but arithmetic, which is what makes every case testable without a window.

- **The counter-example is in this repo.** `BatchConvertDialog`'s LUFS branch applies
  `10^((target-current)/20)` with no true-peak protection at all and returns `void`, so it will push
  a track past 0 dBTP and say nothing about it. Every ceiling assertion in `LoudnessMatchTests` is
  there so that cannot happen on this path.
- **"Average" is the arithmetic mean of the LUFS figures, not of their power.** LUFS is already a
  perceptual scale; a power mean is dominated by the loudest track and lands several LU above where a
  listener puts the average of a record.
- **The relative modes use −1 dBTP**, because they are not delivering to a specification and so have
  no stated ceiling to take. A preset target uses its own — which matters, since `CompactDisc` allows
  only −0.3 and a hardcoded −1 would disagree with it.
- **Two notes that had to be separated.** "true-peak limited" alone reads as "left alone", so a track
  whose loudness asks for a boost while its true peak is already over the ceiling says *"already
  0.4 dB over the ceiling — brought down instead"*. It is the one case where the sign of the applied
  gain is the opposite of the sign of the request.
- Measuring is sequential, never parallel: each meter carries its own ring buffers, `SubProgress.
  Slice` assumes one item at a time, and a cancelled parallel run leaves a table half from this
  measurement and half from the last. Apply is all or nothing, checked against each document's
  `EditVersion` first — half a record moved with no record of which half is a worse state to be left
  holding than nothing applied.
- The gain commits under the name `Match Loudness −14.0 LUFS (+2.3 dB)` rather than `Gain +2.3 dB`.
  That is the durable half of "show what has been applied": the dialog closes, the status line
  scrolls away, and the history row is still there. `Processing.NormalizeLoudness` is untouched but is
  now the inferior path — a 2-tap inter-sample estimate, a hardcoded −1 dBTP, and no report;
  `LoudnessMatch` is the maintained implementation.
- **It costs one copy of the document, not three, and it does not run on the dispatcher.** The first
  version went through `Processing.Apply`, which copies the range, scales the copy, copies it again
  for the undo entry and allocates a third array in the splice: about **1.4 GB moved per side of
  vinyl, on the UI thread, uncancellable, over every open tab in turn** — precisely the defect the
  audit had already fixed for the channel menu, reintroduced. `Processing.MatchLoudnessData` scales on
  the way into one new buffer on a worker, and `ReplaceAllOwned` commits it by taking ownership,
  retaining the outgoing arrays by reference rather than copying them.
  `CommittingAMatchRetainsTheOutgoingSamplesRatherThanCopyingThem` pins both halves with
  `Assert.Same`.
- **All or nothing is enforced by rolling back, not by hoping.** The version check happens up front,
  but the dispatcher pumps across every `await`, so it is checked again immediately before each
  commit; a failure or a cancel undoes the documents already committed. Each commit is exactly one
  undo entry, so putting one back is exact. Holding every scaled buffer and committing them in one
  synchronous pass would also be atomic and is the wrong trade — it is N copies of the album resident
  at once.
- **The reference track is held by row, not by title.** Two tabs can carry the same name — the same
  file opened twice, or two untitled recordings — and matching the combo selection by text pointed
  the reference at a different track, silently moving the level everything else was matched to.

## The undo list was short by about half, and nothing ever said so

"I undid everything on the undo list and there were still changes in my file." The undo engine was
working exactly as written; what it was written to do was silently throw the oldest steps away, and
it was throwing away roughly twice as many as the memory limit actually called for.

- **`EditBytes` counted shared buffers twice, and whole-document renders are precisely the case that
  shares them.** `ReplaceAllOwned` reads the live channels as its `Old` side and keeps the incoming
  render as its `New`; the next render then reads that same object as *its* `Old`. So a chain of N
  renders holds **N+1 documents, not 2N** — and the budget was summing the steps, which charges one
  album-sized array once per step that refers to it. `ConsecutiveWholeDocumentRendersAreChargedOncePerBuffer`
  pins the arithmetic and `TheBudgetDoesNotReleaseHistoryThatWouldNotActuallyBeFreed` pins what it
  cost: with room for the original and four renders, the old accounting kept **one** step of four.
- **The scale is why it bit rather than being a rounding error.** A 20-minute stereo 44.1 kHz
  document is 423 MB of samples, so one `ReplaceAllOwned` was charged **847 MB against a 512 MB
  default** — over the whole budget on the first whole-file operation. Render Master Chain, Match
  Loudness, Vinyl Restoration, Swap Channels, Invert Phase and Channel Balance all commit that way.
- **The eviction loop had the same fault in its other half, and that one is subtler.** It subtracted
  the dropped step's gross size from a running total, which reports memory reclaimed that is still
  live — releasing `_undo[0]` frees only the array `_undo[1]` is not also holding. A drop is costed
  by decrementing reference counts now, so it frees exactly the buffers nothing else holds. It was
  first written to re-read the whole total after every eviction, which is also correct and is
  quadratic; see the review section below for what that measured.
- **The gross sum survives as the screen, because it can only over-state the exact one.** A document
  inside the budget by the cheap sum is inside it by the deduplicated walk too, so the walk — which
  allocates a set and touches every channel of every step — is paid for only when it can change an
  answer. `EnforceUndoBudget` returns on the cheap test in the overwhelmingly common case.
- **`GetHistory` charges each buffer to the first step on the timeline that holds it**, so the
  panel's rows add up to the figure in its header. `TheStepsSumToWhatTheHistorySaysItRetains` is what
  stops those two drifting apart, which is the failure a per-step gross figure would have produced.
- **Saying so was a separate fix from counting right, and the ordering is the whole of it.** The
  budget is enforced from inside `ReplaceRange`, **before** the `Changed` event the status line is
  written from — so a line written at eviction time is overwritten by "… applied · Undo available" a
  moment later, and the one thing the user needed to know is the one thing that does not survive.
  `AudioDocument.HistoryReleased` is held in `MainViewModel._releasedSteps` and *appended* to that
  line instead. Ctrl+Z with nothing left also says why now rather than doing nothing, and both
  messages name Settings ▸ General, where the limit is.
- **The counting fix does not make the limit generous, and one eviction is still silent.**
  `HistoryReleased` is subscribed for the active document only, matching `Doc.Changed`, so a tool
  working across tabs — Match Loudness — can evict without a line. The Edit History panel has always
  reported it per document and still does.

## The spectral actions work through a mask, and a plain time selection is one

Heal, Attenuate, Gain and Learn pattern were reachable only by drawing on the spectrogram, and the
bar carrying them appeared only with the spectrogram. Nothing about the four needs the picture: they
take a `SpectralMask`, and a range selected on the waveform is a mask across the whole frequency
band. `MainViewModel.ResolveSpectralSelection` returns the drawn region if there is one and builds
that band if there is not.

- **`SpectralMask.FullBand` rather than `ForRegion` with the band set to DC and Nyquist, and the
  reason is the feather.** The general taper erodes inward from the edges of the weight array in
  *both* directions — but the frequency edges of a full-band mask are the ends of the spectrum
  rather than anything in the signal, so it fades out exactly the bins a selection across everything
  asked for. `AFullBandMaskDoesNotFadeOutDcAndNyquist` measures the two builders side by side. A
  taper exists to stop an edit ringing, which is a statement about edges the audio *has*: across a
  full band the only edges are the two ends of the span, so only the frames are tapered.
- **It is also the cheap way round, which matters at this size.** Erode-then-smooth is four passes
  over every cell plus two scratch arrays as large as the mask; a full band over a minute of 44.1 kHz
  audio at 2048/512 is 5.3 million cells. A fill plus a frame taper is one pass and one allocation.
- **There is a ceiling and it is the one this repo already uses twice.** A repair holds the mask plus
  real, imaginary and weight planes of the same size — 16 bytes a cell, per channel — so
  `MaximumFullBandCells` is 512 MB's worth, matching the clipboard and the residual. That is a little
  over six minutes at 44.1 kHz. Past it the actions stay disabled rather than the repair failing
  partway; a whole-file change of level is the ordinary Gain command, not this one.
- **The mask is built when an action runs and never from the binding the buttons read.** Four
  `IsEnabled` bindings re-read `HasSpectralSelection` on every pixel of a selection drag, and
  allocating millions of cells inside a property getter would make dragging a selection cost what a
  repair costs. `TimeSelectionSpan` answers with a length check; `ResolveSpectralSelection` builds.
- **The bar now follows the document and the four selection tools still follow the picture.**
  `ShowsSpectralBar` is `HasAudioDocument`; the tools, the scale switch and the bins-per-octave combo
  each keep `ShowsSpectrogram`. Learn pattern resolves its selection **after** its dialog closes, not
  before: the box is up long enough for the selection to move, and it is the selection standing when
  Remove is pressed that the user meant.

### Rendering it earned its keep three times

The bar had only ever been laid out with the tools and the scale switch present. Showing it in
waveform mode changes what is in it, so it was rendered at the shell's 1180 px minimum rather than
reasoned about — and two wordings had to be cut down.

- **The three right-docked groups are readout, then scale switch, then hint, and a DockPanel serves
  them in that order — so the switch pays for every pixel the readout spends.** The first band
  wording, "0 Hz → 22.05 kHz · full band", took the switch from **37.5 px to 2** at 1180. It reads
  **"full band"** now, which says the same thing — DC to Nyquist *is* the whole band — and gives
  50 px back, leaving the switch **88 px**, wider than the drawn-band case it sits beside.
- **The hint is docked last and so is cut first.** Naming the waveform route in the rectangle tool's
  prompt took it from 167.5 px wanted to **343**, cutting it at 1400 as well as at 1180. The
  spectrogram wordings are therefore left exactly as they were; the route is on the four buttons'
  tool tips, which have room, and in the waveform-mode prompt, where there is no picture to name
  instead. The hint also carries `TextTrimming` now, so the next long one degrades to an ellipsis
  rather than being cut mid-glyph — the fault this file already records for the rack's render
  buttons and for the plugin name under the power LED.
- **The margin trap caught this measurement too, exactly as it caught the monitor bar's.**
  `DesiredSize.Width` includes an element's own margin and `ActualWidth` does not, so the scale
  switch read "199.5 of 209.5 wanted" at every width and looked clipped at all of them; the ten
  pixels are its `Margin="0,0,10,0"`.
- **End to end, on a 1 kHz tone with the middle second selected on the waveform: −24.0 dB inside the
  selection and 0.0 dB outside it**, for a −24 dB Attenuate through the resolved mask.

### The bar had never fitted at 1180 px, and now it drops a control instead of cutting one

The same render that measured the two wordings above found something older: **with the tools, the
four actions, both rules and a drawn band's readout, the bar's children want 1314 px and the panel
has 1152** at the shell's declared minimum. Something had always been losing, and what lost was
whatever was docked last — the scale switch, at **37.5 px of the 199.5 it wants**, so `CONSTANT-Q`
shipped cut mid-glyph inside its `ClipToBounds` border.

- **The switch is the right thing to lose and the readout is not.** The readout says what a repair is
  about to act on and exists nowhere else; the scale is one choice out of three. So the switch and
  the bins-per-octave combo beside it are **dropped** below `MainViewModel.SpectralScaleMinimumWidth`
  rather than squeezed, and **View ▸ Frequency Scale** carries the same three commands at every
  width — nothing becomes unreachable, it merely stops being on the toolbar.
- **1350 px is measured, not chosen.** The children want 1314 and the window is 28 px wider than the
  panel they sit in, so everything fits from about 1342. The shell's minimum is 1180 and its default
  is 1680, so this only bites in between.
- **It keys off the window's width and deliberately not off the switch's own shortfall.** A control
  that decides its own visibility from the room it was given oscillates: hiding it makes room, which
  makes it fit, which shows it again. Hysteresis would stop that and would be a second number to
  keep in step with the first. One threshold on a width nothing else depends on has neither problem,
  and `ShellWidthPixels` is a property a test can set without a window — it starts at infinity, so a
  view model that has never seen a window assumes there is room.
- **Bins per octave goes with the switch rather than staying behind.** It sits immediately beside it
  and describes it, so leaving it would strand a control explaining a choice no longer on screen —
  and it is 100 px of the width the bar had already run out of.
- **`SpectralBarRenderProbe` now pins the invariant that is actually true: the switch is whole or
  absent, never in between.** In between is exactly what a `ClipToBounds` border does with less room
  than it wants. Measured: **0 px at 1180 and 199.5 of 199.5 wanted at 1350**. The earlier assertion
  — that the new readout costs the switch no more than a drawn band's does — is superseded by it.
- **The View menu items were opened in the probe rather than trusted.** This repo's record is that a
  broken binding does not throw, and that a `MenuItem` realised in the wrong place reports failures
  that nothing is listening for. All three items realise, `Logarithmic` reads checked against the
  default, and `BindingErrors` reports **0**.
- **That menu shipped gated on `ShowsSpectrogram` for a day, and greying it in waveform mode was
  wrong.** The scale is a sticky preference rather than an action: choosing it with no picture on
  screen is choosing what Split will draw the moment it opens, and the checkmark moving is feedback
  enough that nothing happens silently. What the gate bought was agreement with a toolbar switch
  that is not there either — and what it cost is that **a menu item greyed for a reason the user
  cannot see reads as broken rather than as not applicable**, which is the disabled-`TextBox`
  finding from the delivery dialogs arriving by the opposite route. It now inherits the View menu's
  own `HasAudioDocument` gate and nothing else.
- **`TheFrequencyScaleStaysReachableWhicheverWayTheSwitchIsLost` covers both ways of losing the
  switch** — waveform mode and a window too narrow — and was checked by mutation: putting the gate
  back fails it. It opens the submenus rather than reading them closed, because a declared
  `MenuItem`'s bindings are what is under test and a broken one leaves `IsEnabled` at its default,
  which is the answer the test wants either way.

### Reviewing the two above found four things, and the largest was a complexity class

- **Re-reading the deduplicated total after every eviction is quadratic in the retained depth, and
  it is on the dispatcher.** Correct, and the thing the fix above needed to be correct — a drop
  frees only the buffers no surviving step holds, so the total cannot be decremented by the dropped
  step's gross size. Re-reading it walks every remaining step again. Measured on a history squeezed
  in one go, which is what the Settings dialog lowering the limit does to the next edit:
  **19 ms at 500 steps, 316 at 2 000 and 1 190 at 5 000**. `EnforceUndoBudget` counts references
  once and then does arithmetic: **0.7, 0.7 and 6.1 ms**, and removes from the front of both stacks
  with one `RemoveRange` rather than one at a time, which was a second quadratic underneath the
  first. `ReleasingAWholeHistoryAtOnceStaysLinearInItsDepth` is a timing assertion, which this suite
  otherwise avoids — justified because the defect is a shape rather than a constant, so the ceiling
  can sit two orders of magnitude above the measurement and an order below the failure.
- **The eviction note was announced and then overwritten, on exactly the path that most needed it.**
  Undo moves an edit onto the redo stack, which is retained too, so undoing on a tight budget can
  itself release older steps — measured, one Undo released two. The change event fires from inside
  `Doc.Undo()` and the shell writes "… applied · Undo available" from it, carrying the note; then
  `MainViewModel.Undo` overwrites the line with "… undone." and the note goes with it.
  `_suppressEditReport` hands the line to whichever history move owns it — Undo, Redo and the panel's
  jump — and each folds the note into its own wording. Running out of undo **supersedes** the
  per-eviction note rather than joining it, because both name the same fact.
- **`HistoryReleased` fires from the middle of a commit, and the remark on it claimed otherwise.**
  It said a handler "sees the state the eviction settled on"; the eviction, yes, but not the
  document — `Dirty`, `EditVersion` and the current state id have not moved yet, and `Changed` has
  not fired. Measured: `EditVersion` reads 2 inside the handler where it reads 3 a moment later.
  The ordering is load-bearing and stays — it is what lets the shell hold the count and fold it into
  the line the change event produces — so the contract is now stated instead: **record and return,
  do not read the document.**
- **`SizeChanged` was subscribed in the XAML and its handler reads `_vm`, which
  `InitializeComponent` runs before.** It works today because layout happens after the constructor,
  which is luck rather than a guarantee: one early measure pass is a null reference at startup. It
  is subscribed from the constructor now, after the view model exists.

Two smaller ones on the spectral side, both introduced by moving the Learn pattern resolve after its
dialog: it read whichever tab was active while splicing into the one captured before the box opened
(unreachable while the dialog is modal, and not a thing to leave resting on that), and a resolve that
came back empty returned in silence after the user had pressed Remove. Both are guarded, and the
second says so — a tool that declines without a word is indistinguishable from one that failed, which
is already on record here twice.

### The rest of the review, including the one that was over-cautious

- **The full-band ceiling was flagged as understating what a repair holds, and it does not.** The
  arithmetic behind it — mask plus real, imaginary and weight planes, four arrays of four bytes a
  cell — is right for the continuation, and **the continuation is the only method anything reaches**:
  nothing outside the tests ever sets `SparseInpainting`, which is the case that would allocate a
  block and a window sum on top. The constant is correct as it stands; what it lacked was a note
  saying which method it was sized for, so that wiring the solver to a control brings the number
  down with it.
- **`FullBandFrames` counted in `int`, and the wrap is the wrong way.** At the hop spectral edits use
  it cannot overflow; at a hop of one it can, and the wrapped count is *negative* — which reads as
  "no frames", so `FullBandFits` answers yes to a span `FullBand` then refuses to build, leaving the
  four actions lit and doing nothing when pressed. Counted in `long` now, and
  `AFrameCountTooLargeForAnIntIsRefusedRatherThanWrappingNegative` fails against the int version at a
  hop of one and passes at 2 and 512, which is the signature of exactly that bug.
- **`HistoryEntry.RetainedBytes` changed meaning and its own documentation did not.** It is no longer
  what a step holds; it is what the step is first on the timeline to retain, which is the property
  that makes the panel's rows sum to its header rather than over-state it. The parameter doc says so.
- **Four silent declines in the spectral repair path now say what happened.** They are reachable the
  way this repo's other silent-decline findings were — a selection that goes between the click and
  the read, a repair that produces nothing, a file that moves under the operation — and the rule is
  already on record twice: a tool that stops without a word is indistinguishable from one that
  failed.
- **The exhausted-undo line quoted the current limit as the one those steps went under.** The count
  is cumulative and the limit can have moved since. It reads "have been released to stay inside the
  undo memory limit … it is N MB now", which is true whenever it is shown and is also the only figure
  the reader can act on.
- **The menu test asserted from inside the render callback**, which is the one thing this repo's
  shell probes are arranged not to do — an assertion thrown before the cleanup meets a modal
  unsaved-work box with nobody on the thread to answer it. Its readings are collected and judged
  outside the callback like the probe beside it.

## Normalization had four implementations and one of them was the only one that mattered

The app already did both kinds of normalization, and the question of which it had was harder to
answer than it should have been, because it had four separate answers.

**Peak** was `Processing.Normalize`, reached by one menu item that hardcoded −0.3 dBFS at the call
site — the ceiling was not a parameter of the command, it was a literal in `MainViewModel`. **Loudness**
was `LoudnessMatch`, which is the good one: BS.1770 gating, the 4× oversampled true peak, a gain that
is the smaller of what loudness asks for and what the ceiling allows, and a shortfall reported rather
than swallowed. It was reachable only as *Match Loudness Across Tabs*, so the single most ordinary
request — bring this file to −14 LUFS — had no command. **Batch convert** had its own LUFS branch
that applied `target - current` and nothing else. And `Processing.NormalizeLoudness` was a fourth,
with a 2× inter-sample estimate, zero call sites in `src/` or `tests/`, and a doc comment.

The batch branch was the defect, and it was already on record: `LoudnessMatch`'s own remarks name it,
and `LoudnessMatchTests` opens by saying the file exists because of it. Nobody had fixed it. It would
push a track past 0 dBTP and say nothing — unattended, over a queue, which is the worst place for a
silent breach.

**What changed is routing, not arithmetic.** `LoudnessMatch.Plan` is pure, total, and already tested;
it was never multi-track-specific, and a one-element list is a valid input. So the new single-file
*Normalize Loudness* drives it with one measurement, and the batch converter now does too. The true
peak the batch path needed cost nothing to obtain: the meter it was already running over the whole
file computes it, and the old code read `IntegratedLufs` off that meter and threw the peak away. The
three LUFS modes map onto `LoudnessTarget.Apple` / `Streaming` / `Ebu` rather than onto bare numbers,
so the ceiling travels with the target instead of being restated somewhere it can be forgotten.

Three consequences worth knowing:

- **Peak normalize asks for a ceiling.** `AppSettings.NormalizePeakCeilingDb` remembers it, so the
  old one-value behaviour costs one Enter, and the undo entry is `Normalize −6.0 dBFS` rather than
  `Normalize` — two normalizations to different ceilings used to read back identically. The rounding
  is `Math.Round(value, 1)`, not divide-by-step-and-multiply-back, because the latter leaves −0.3 as
  −0.30000000000000004 and a settings file nobody touched then looks dirty.
- **Loudness normalize is whole-document and says so in its title.** A selection was considered and
  rejected: the gate works on 400 ms blocks against a threshold taken from the programme, so a short
  range has no loudness rather than a smaller one — `ARangeShorterThanTheGateHasNoLoudnessToNormalizeTo`
  pins that a 100 ms fragment measures negative infinity. Peak normalize keeps taking a range,
  because the peak of a selection is the same kind of measurement as the peak of a file.
- **A ceiling that binds is reported, never applied quietly.** Interactively that is a Yes/No naming
  the dB of limiting the master would need; in a batch it is `true-peak limited, N dB short of target`
  on the row and a count in the summary line. That rule was already stated twice in this repo — by
  `LoudnessCompliance` and by `LoudnessMatch` — and the batch path was the one place it was not held.

`Processing.NormalizeLoudness` and its private `MeasureIntegratedLufs` were deleted, on the precedent
the four unwired restoration methods set: an implementation nothing reaches has not been measured,
and keeping a second answer to a question the app already answers correctly is how the four
implementations happened in the first place.

### Review fixes: two silent lies, a stalled bar, and a test that did not test its own claim

- **`Processing.Normalize` declined silence inside `Apply`'s delegate, which commits regardless.** So
  normalizing a silent range spliced it over itself — an undo entry and a dirty document for an edit
  that changed nothing — and the new status line then said "Normalized to −6.0 dBFS." This is the
  defect already on record for Reduce Noise, where the transform returned the untouched buffer
  instead of null. The peak is measured **in place before anything is copied**, so the silent case
  now costs neither the copy nor the entry, and the method returns `bool` so the caller can say
  which of the two happened.
- **The apply pass pinned the progress bar at a determinate 0%.** `OperationProgress.Refresh` treats
  any `reported >= 0` as a figure, and `MatchLoudnessData` takes no `IProgress`, so a single
  `Report(0)` took the bar out of indeterminate and then left it at 0% for the whole scaling pass —
  a stalled bar rather than an honest "Working…". Nothing is reported there now.
- **The document could move under the true-peak prompt, and the work was only discarded afterwards.**
  `MessageBox.Show(owner, …)` is Win32 and disables **the owner alone**, unlike `ShowDialog`, which
  disables the application — so the modeless Edit History panel stays live, and `CanMoveHistory`
  gates on exactly the flag the measure block's `finally` has just cleared. The post-apply
  `EditVersion` check always caught it, so nothing was ever committed wrongly; what it cost was a
  full pass over the document behind a blocking overlay before saying so. Checked before the pass as
  well now.
- **A true-peak-limited batch row wore the in-flight colour.** `AccentBrush` is what a row still
  converting wears, and colour is what an unattended queue is scanned by. It is amber now — the
  shade `MatchLoudnessDialog` already uses for exactly this — and amber is right here rather than
  against the house rule, because the row is reporting a limit rather than a fault.
- **The re-pinned side-level test asserted to three decimal places while its own remarks said
  "exactly 1.0".** 0.9999 satisfies a three-decimal tolerance and breaks all three `< 1.0` guards,
  which is the bug that had just been fixed. The two full-level rows now assert the boundary
  directly. **Mutation-tested, and the first attempt at that mutation is the more interesting
  result**: multiplying the curve by 0.9999 *before* `Quantize` changed nothing, because the
  quantiser snaps 0.99979 back onto 1.0 — so the exactness this depends on comes from the 0.05 step
  dividing 1.0, not from the 0.80 coefficient. Applying the same drift *after* `Quantize` fails the
  two boundary rows and no others.

## The side-level sigmoid could not reach the top of its own range, and three things read the top as "off"

Eight tests were red on `main` before any of the above. All eight came from one commit — `0523a24`,
which changed two restoration algorithms and touched no test file. Six were the side-level rule; two
were the WOLA golden pinning. Both are now green, and the six were not a stale pinning.

**The rule.** How far the Vertical Surface Noise card pulls the side down was a linear ramp between
two anchors measured off five transfers — −14 dB side-to-mid for a mono cut, −8 dB for a real stereo
one. `0523a24` replaced it with a sigmoid, floored at 0.20 so a mono pressing keeps a fifth of its
side, on the honest grounds that five records from one collection do not justify a hard switch. That
much was deliberate and is kept.

What was not deliberate is that `0.20 + 0.75 * sigmoid` has a supremum of **0.95**. A logistic never
reaches 1, so the expression cannot either — and the line it sits on is
`Math.Clamp(0.20 + 0.75 * sigmoid, 0.20, 1.0)`, whose upper bound is therefore dead code. Nobody
clamps to a value they know is unreachable; the `1.0` is the intent, left over from the ramp.

**Why 0.95 is not a rounding detail.** Three separate places read `SideLevel < 1.0` as *this stage
exists*: the workbench ticks its card on it, the render skips `ScaleSide` on it, and
`DescribeSideLevel` has a "leaving the side at full" branch behind it. At a ceiling of 0.95 all three
fire for **every** stereo record — the card switches itself on, the readout says the image is being
narrowed, and the side loses 0.4 dB nobody asked for. It also silently broke a contract stated in
`CleanupAnalyzer.SideToMidDb`, which returns `0` when nothing clears the gate and says so in a
comment: *"Zero is the neutral answer, and it recommends leaving the side alone."* Under 0.75, zero
recommended 0.95.

The fix is `0.75` → `0.80`, which restores a reachable 1.0 from about −6.6 dB up and leaves every
softened value the commit chose untouched: −16.5 → 0.20, −14 → 0.25, −11 → 0.55, −8 → 0.90. Only
those three middle rows were genuinely re-pinned. `StereoSideToMidDb`'s docstring still said "at or
above which the side signal is left entirely alone", which the sigmoid made false at the anchor
itself; it now says which.

**Why no test caught it.** `VerticalNoiseReadoutTests` does pin the full-level behaviour — but by
passing `level: 1.0` as a literal, so it kept passing while testing a state the recommender could no
longer produce. The pinning was one layer below where the regression was. The re-pinned theory rows
now carry the reason the two ends are not free to move, so the next person to reshape the curve is
told what depends on it.

**The golden pair.** `Restoration.ScrubTonalPeaks` — a 5-bin median stripping narrow spikes from a
learned profile, so music left in a "quiet" passage is not gated away as noise — genuinely changes
the denoiser's output, and the golden test's signal is built to contain exactly that case. Re-pinned,
with the evidence the file's own convention asks for: of the three pinned profile bins only bin 10
moved (0.6547 → 0.4708), bin 0 being the one the new code documents as exempt because it is DC; the
output RMS *rose*, 0.2295 → 0.2434, which is the direction a lower profile has to move it; and the
probes move where the tones are rather than uniformly.

**And the Enabled box the render never read.** Found while tracing the above, from `5fa63a1` and
older than it: `verticalEnabled` was read in exactly two places, set from the recommendation and
passed to the evidence line. It never reached `RestorationSettings`, which took
`sideLevel.Value / 100.0` directly. Six of the seven cards on that dialog put their Enabled box into
the record and the render reads it; this one did not. So unticking *Enabled* by hand printed "This
card is switched off; the side signal is untouched" over a render that went on reducing the side —
not a stale caption but a false one, on the single card that can throw away half of a stereo record.

The record now carries `ReduceSide` beside `SideLevel`, the way `RemoveSubsonic` sits beside
`SubsonicCutoffHz`, and the guard became a named predicate next to the readout it has to agree with:

```csharp
internal static bool SideStageRuns(bool bypass, bool enabled, double sideLevel) =>
    !bypass && enabled && sideLevel < 1.0;
```

**Naming it is the point, not tidiness.** Pinning the predicate alone would not have caught this: the
guard was correct about what it read, the caption was correct about what it read, and the defect was
only ever visible in the two being asserted against each other.
`TheCaptionClaimsAReductionOnlyWhenOneWillHappen` does that, and reverting the predicate to the
pre-fix `!bypass && sideLevel < 1.0` fails it on exactly the two rows where the box is unticked and
the slider is not at full — which is the bug, reproduced.

## The ceiling prompt stated the arithmetic and left the reader to do the subtraction

Normalize Loudness put up "Loudness alone asks for +9.8 dB, but the -0.3 dBTP ceiling allows only
+5.2 dB", offered Yes/No, and never printed the one number anyone would act on: **where the file
actually ends up**. `LoudnessMatchStep.ResultingLufs` had been on the step all along and the prompt
did not use it. The dead end was structural as well as verbal — the message named limiting as the
thing that would close the gap and gave it nowhere to happen.

- **It leads with the outcome now, and the two costs of the alternative are stated rather than
  discovered.** `LoudnessMatch.DescribeCeilingChoice` is pure, so the wording is unit-tested without
  a window — the arrangement `DescribeDeclipChoices`, `DescribeNoiseDepth` and `DescribeOutputMix`
  all use. It says the file can only reach -16.6 LUFS against the -12.0 asked for, that going louder
  passes the ceiling, and that a limiter **lands a little under the target** (limiting removes energy
  as well as peaks) and **leaves the document above full scale until the rack is rendered** — fine in
  32-bit float, hard clipping the moment it is saved at 16 or 24 bits.
- **The limiter route applies the full gain, not the permitted one, and that is the whole reason it
  works.** A limiter after a gain already capped at the ceiling has nothing to catch, so the offer
  would be theatre. `+9.8` goes on destructively and a Precision Limiter goes into the rack at
  `thresh 0` — transparent peak protection that only catches overs, which is exactly what the gain
  just created — with `ceiling` set from the plan's own `CeilingDbtp`, so the bound the gain was
  computed against and the bound the rack enforces are one number.
  `TheTwoCoursesOfActionCarryDifferentGains` pins it.
- **The limiter is added before the gain is committed, so the two land together or neither does.**
  An effect that will not load leaves the rack unchanged and reports why; committing the full gain
  without the limiter that justifies it is the one outcome this path must not produce, because it is
  the loud one. The scaling pass runs first, since it is the cancellable part and touches nothing.
- **`MessageBox` cannot label its buttons**, so a three-way decision through it has to be worded as a
  question whose Yes and No mean things the buttons do not say. `Views/ChoiceDialog` is the themed
  alternative: title, message, and a button per course of action carrying what it will do. Being a
  real `Window` shown with `ShowDialog`, it disables the whole application rather than its owner
  alone — the asymmetry already recorded for the true-peak prompt, where the modeless Edit History
  panel stayed reachable behind a `MessageBox`.
- **The choices are stacked, not in a row, and they wrap.** They are sentences carrying figures
  rather than verbs, and a row of them is what runs a dialog past its own width. `ToolButton` is a
  38x38 icon square, so `ChoiceButton` clears the fixed width the way `SegmentButton` had to, and
  `Height` becomes `MinHeight` because the content wraps — the failure mode is a taller dialog
  rather than a word cut mid-glyph. Measured at the dialog's 460 px: the widest label wants
  **213.5 px against 414 given**, so nothing wraps today and the probe fails if that stops being
  true. The first option takes the accent and `IsDefault`, so the emphasised button and the default
  answer are the same one, and it is the conservative one.
- **`MasterSectionViewModel.AddConfiguredEffect` sets its parameters before `SyncFromMaster`**, so
  the card is built holding the values asked for. A card that appears at its defaults and then jumps
  is indistinguishable from one the user moved. `SetParam` clamps to each parameter's own range, so
  a caller cannot put an effect somewhere its own UI could not reach —
  `EveryTargetsCeilingIsReachableByTheLimiter` checks the five presets against the limiter's -12..0.

## Prepare Audio CD could be told about the world exactly once

Reported as "I see no way of adding more tracks", and the report was very nearly right. There was
one way — a button called **Use Selection**, parked in the AUTO SPLIT panel between the threshold
slider and Analyze, named after its input rather than its action. It read `_document.SelStart`/
`SelEnd` at click time. The window was opened with `ShowDialog()`, which disables the owner, so
those could not be changed while it was up: the selection was whatever happened to be set before
the window appeared, and pressing the button twice added the same range twice. `Split` had the same
defect one level down — it splits at `_document.Cursor` and quietly fell back to the track midpoint
because the cursor could not be moved either. Everything else in the list came from Analyze, which
*replaces* the list rather than adding to it, or from regions read once in the constructor.

So the window was modal over the only two pieces of state its manual editing depended on. Both
changes below follow from removing that.

**Modeless.** `ShowDialog()` → a static `ShowFor(document, main, owner)` that `Show()`s. Three
things that a modal window never had to handle:

- *Asked for twice.* `OpenDialogs`, keyed by `DocumentViewModel`, raises the window already
  arranging that file. Two windows on one document would both be writing regions through Sync
  Regions, last one winning silently.
- *The file changes underneath it.* `Doc.Changed` is subscribed, and every track range is mapped
  through the splice the way the cursor, playhead, selection, markers and regions already were.
  `DocumentViewModel`'s local `MapAnchor` became `public static MapEditAnchor` for this — the third
  caller made it worth naming. A track the edit collapses is left in the list rather than dropped:
  validation names it, and the row is the only record of a title and ISRC that would go with it.
  (`PqSheet` tolerates the collapsed row — `DdpImage.Timecode` clamps at zero — and the misleading
  lead-out figure it would produce is only printed at Information severity, which an invalid range
  precludes.)
- *The tab closes.* `Documents.CollectionChanged` closes the window. `_busy` is cleared first,
  because `OnDialogClosing` vetoes a close while an operation runs and there is no longer a document
  to finish it for.

**The rack checkbox stopped being a private copy.** It was captured at open and restored on close.
Modeless, that restore stomps a bypass the user pressed in the main window meanwhile — so the
checkbox now follows `Master.RackEnabled` both ways and nothing is restored. It always *was* the one
master rack; only the modality made a snapshot look reasonable.

**Add Track.** One button, in the row with Remove and Split where the other list operations are,
replacing Use Selection. Selection if there is one — verbatim, short or overlapping, because the
user pointed at that range and validation is better placed than the button to object — otherwise
the longest stretch no row has claimed, swept rather than assumed ordered, since ranges may overlap
and the list is an arrangement rather than a timeline. Inserts after the selected row, and selects
what it inserted, which is what makes repeated presses come out in source order without ▲▼.

The no-selection fallback took two goes to get right, and both wrong answers came from the same
unexamined premise: that a new track comes from *space no track is using*.

First it claimed the whole longest unclaimed stretch, reasoning that any other length would be
invented. Correct, and useless — a side is one unclaimed stretch, so the first press was the only
one that did anything. Then it took a three-minute block off the front of that stretch, which fixes
the second press but not the premise, and the premise is the bug: **the tracks tile the recording.**
`SuggestTracks` returns boundaries running `0 → …gaps… → end`, contiguously, and the window runs it
on load. All of the file is claimed before the user has touched anything, so the search found
nothing and the button reported that everything was claimed — with an analysis that had found one
gap, a list of two tracks and no way to reach a third. That is what got reported, twice, and the
second report is the one that said the button was no better than the Use Selection it replaced.

So another track comes out of an existing one. `DivideRow` takes a block off the selected row's
front and leaves the remainder as the next row — which is exactly what Split already did, at a fixed
offset instead of at the cursor, so both now call it. It selects the remainder, and *that* is what
makes a repeated press walk forward through the side instead of subdividing the same head. A row too
short to give a block and a valid remainder is divided at its midpoint, the clamp Split already had.

Unclaimed space is still checked first, because a Remove leaves a real hole worth filling, but it is
now the exception rather than the rule.

`AddTrackDividesTheSelectedTrackWhenTheRecordingIsAlreadyTiled` is the one to keep. Both earlier
versions passed their own tests — each was seeded with regions that left a gap, which is not the
state the dialog actually opens in. It seeds one region over the whole file, which is what Analyze
leaves on a side with no gap it can find. It runs at 8 kHz: the block is defined in seconds, so what
it does is a function of duration alone, and eight minutes of timeline is 3.8 M samples there
against the 42 M a real side would be.

`CdTransferDialogTests` covers all of it. The one thing worth knowing before writing more: seed the
document with at least one region, or the constructor's `Loaded` handler starts the asynchronous gap
analysis and the list is not the one the test set up.

## Analyze was doing its job and taking five other buttons down with it

Reported as "the Analyze button does not seem to do anything — a quick flash of the box and nothing
else", and the flash is the diagnosis: the analysis ran, found what it found, and rebuilt the
`ListBox`. Every button in the dialog was then swept, driving the real window through the shared
test harness rather than reading the handlers. Four faults, and the reported one is a compound of
the first two.

- **Rebuilding the collection clears the selection, and five buttons read their enabled state off
  it.** `ReplaceTracks` clears `_tracks`, `OnTrackSelected` fires with nothing selected, and
  Preview, Remove, Split, ▲ and ▼ all go dead. Nothing re-selects — the constructor's `Loaded`
  handler sets `SelectedIndex = 0` and does not run again. So every press of Analyze handed back a
  list with five of its six buttons inert until the user happened to click a row. Measured: `sel`
  goes 0 → −1 and all five read `False`. The fix is in `ReplaceTracks` rather than in the caller,
  because it is the one place a row can stop existing.
- **The second press proposes what is already on screen, so there was nothing to see either.** The
  window analyses on load, so by the time anyone reaches for the button the list *is* the analysis;
  pressing it again at the same threshold rebuilds identical rows. It also threw away every title,
  ISRC, performer and pre-emphasis typed since — for nothing, because the boundaries had not moved.
  `MatchesCurrentBoundaries` compares the proposal against the rows and leaves the list alone when
  they agree, saying so and naming the threshold. **Titles are deliberately not compared**: Analyze
  does not produce one, so a run that agrees about where the tracks are has nothing to say about
  what the user has typed into them.
- **The threshold analysed was not the threshold printed.** The slider is continuous and the label
  prints `{0:0}`, so a drag to −45.4 dB shows "−45 dB" and analysed at −45.4. `IsSnapToTickEnabled`
  is set, and is not the fix on its own — **WPF applies it to a thumb drag and not to a value set
  any other way**, which is exactly what the first version of the test discovered. `SuggestTracksAsync`
  rounds, and the status line quotes the rounded figure, which is what ties the two together in a
  test.
- **Analyze on a zero-length document returned in silence** — the finding this file already records
  for Reduce Noise and for four spectral declines: a tool that stops without a word is
  indistinguishable from one that was never wired up.
- **The five row buttons started enabled with no row to act on.** Nothing sets their state until a
  selection *changes*, so a list that opens empty leaves them lit and inert. The XAML starts them
  disabled and `OnTrackSelected` turns them on.

**Everything else in the window is connected and does what it says**, verified by driving it: both
deliverable toggles, the threshold slider (−70 dB gives one track on a side whose gaps hold a
−60 dBFS floor, −45 gives three), Add Track, Remove, Split, ▲▼, Sync Regions, Preview, Import
ISRCs, Auto-number, the rack checkbox in both directions, Close-as-Cancel, and both exporters. Zero
binding errors. One cosmetic thing left alone: Add Track and Split name new rows from the order
they land at, so a list can hold two rows both called `Track 03` until they are renamed — the
exported filenames are prefixed `01 - `, `02 - ` and stay unique.

### The status line was written in the vocabulary of the source, and said so

Reported twice about the same line. First: *"if you change Quiet below to −30 it gives the same
message with −30. What is this supposed to mean and what am I supposed to do with this
information."* Then, after a rewrite that reported the difference accurately: *"the message at the
bottom is too cryptic — I have no idea what the info means or what I'm supposed to do with the
info."* The second report is the one that decided the shape, and it was right about the rewrite:
being **correct** and being **readable** are different problems, and only the first had been solved.

- **The original said a count and the setting that produced it.** "Found 3 probable tracks at
  −45 dB" — and the setting is printed beside the slider that set it, so the only new fact was a
  count that had not moved. The rewrite after it was an accurate diff written in the words of the
  code: *boundaries*, decibels, and a pointer at two column headers. A status line arrives once,
  unbidden, and is read by somebody who has not seen any of that.
- **The rule that came out of it, and it is worth applying to the next readout as well: name what
  is on screen in ordinary words, then name the next thing to do.** No level (the slider prints its
  own), nothing called a boundary, and no reference to internal vocabulary. Every branch of
  `DescribeProposal` now ends in an action — "Select one and press Preview Track", "Preview them to
  check", "Drag Quiet below to the right, then Analyze again". `PlainEnough` asserts the negative
  half of that on every wording, because the failure here is additive: the next person with a new
  case will reach for the vocabulary that is already in the file.
- **The count staying the same does not mean nothing happened, and that is why the "splits moved"
  line survives the simplification.** Measured on three real transfers butted into one 504 s side:
  **−45 dB and −30 dB both propose three tracks with the splits 7.6 s apart** (155.57 → 149.99 s,
  367.83 → 360.27 s). A split is the midpoint of a detected gap, so a looser threshold calls the
  fade-out quiet sooner and the split lands inside the music. Reporting only the count there is
  reporting the one number that did not move. It is stated in seconds, with **which way** — earlier
  eats the end of the song before the gap, later eats the start of the one after it — and with what
  that costs, because that is what tells a listener where to listen.
- **The same count updates the ranges in place rather than rebuilding the list.** Same number of
  tracks means the same tracks, moved: every title, performer and ISRC carries across by position,
  along with the region each row is bound to and the row that was selected. An earlier fix only
  covered splits that were *identical*, so a one-decibel nudge still wiped everything typed.
- **Which way to drag lives on the slider's own tool tip, which had none.** It is needed once, and
  the line has 756 px. The exception is the single-track result, where there is nothing to rename,
  reorder or preview and the user is stuck — that line spends its room saying "drag to the right".
  **Right is worth naming because it reads backwards**: right is the number nearer zero and the
  *laxer* test, so it finds *more* gaps.
- **The slider now holds whole decibels rather than being asked to look like it does.**
  `IsSnapToTickEnabled` applies to a thumb drag and not to a value set any other way, so a slider
  moved from code sat at −45.4 dB under a label reading "−45 dB" and analysed at the figure nobody
  was shown. `OnThresholdChanged` rounds and re-enters once with an already-round number.
- **The render probe's first version measured the wrong thing and reported a pass.**
  `DesiredSize.Width` on a `TextBlock` in a tree is capped at the room its parent gave it, so four
  different wordings all read **752 px against 756** — which looks like "fits" and means "trimmed to
  the ellipsis". A detached copy carrying the live element's own typeface, measured against
  infinity, is what answers it; the widest wording wants **576 px of 756**. Same class as the monitor
  bar's margin, and the second time a measurement in this repo has flattered itself.
- Both dialog tests were checked by mutation: rebuilding the list instead of updating it in place
  fails `AnalyzeThatFindsTheSameBoundariesKeepsWhatWasTypedIntoTheRows` and
  `ALooserThresholdMovesTheBoundariesAndSaysSoWithoutLosingTheRows`. The synthetic side they run on
  needed **a fade into each gap** to be worth anything — without one the edge of a gap is a step, so
  the split lands in the same place at every threshold and the test cannot see the defect.
### The other three lines, and what reading them in order found

The three left behind by the pass above — Add Track, Split, Sync Regions — brought into the same
voice, as `CdTransferDialog.DescribeAddedTrack` / `DescribeAddedByDividing` / `DescribeSplitTrack` /
`DescribeTooShort` / `DescribeRegionSync`, internal and pure so they are tested and measured without
a window. All three had the same fault as the analysis line: naming things by what they are called
in the source rather than by what the user is looking at.

- **"Added track 03 from a 3:00 block off the unclaimed stretch"** → "Track 03 added, 0:00 to 3:00,
  off the front of the stretch no track was using." *Unclaimed* is `LargestUnclaimedSpan` leaking
  out; the position of a track is what the user is looking at.
- **"Split at 00:00:21.249; edit In/Out fields to fine-tune the boundary"** → "Split at 0:21 — track
  01 ends there and track 02 starts. Use their SOURCE IN and SOURCE OUT boxes to move it." Two
  changes worth naming. **The milliseconds go**, because the In and Out *cells* carry them (a split
  point is exact) and a sentence about one does not. And **the columns are named as they are
  labelled on screen** — SOURCE IN, not "the In/Out fields", which is what they are called in the
  code.
- **"Synchronized 3 arranged track region(s); 0 other region(s) preserved"** → "Marked 3 tracks on
  the waveform." plus "One other region was left alone." where there is one. It described the
  operation; this describes what the user now has. Singular and plural are written out, because
  "1 region(s)" is the same voice by another route, and `PlainEnough` asserts against it.
- **"Too short to divide into two valid CD tracks"** → "… each half would be under the 4 seconds a
  CD track has to run for." A refusal becomes an explanation by naming the rule, and the rule is
  about CDs rather than about this program.

**Reading the lines in order is what found the last two faults, and neither is visible one line at a
time.** A sweep through the real window printed every line the buttons produce, in sequence:

- **Analyze dropping from three tracks to one said "Now 1 track — there were 3. Preview each one to
  check where it starts."** Advice about a list that no longer exists. The no-gaps wording was
  guarded on `previous <= 1`, so it only appeared for a side that had never found any; one track is
  one track however many there were before, and it is the outcome with nothing to rename, reorder or
  preview, so it is the one that has to say which way the slider goes.
- **Pressing Add Track answered "Split at 0:31".** True — `DivideRow` is shared, and Add Track is
  Split at a fixed offset — and it describes the code rather than the press. It now reads "Track 03
  added by dividing track 02 at 0:31." **The button pressed is not the same question as the
  operation performed**, and a readout built from shared machinery will answer the second one unless
  it is made to answer the first.

Widest wording measured in the built dialog at **656 px of 756**, after trimming two that came back
at 692 and 744 — the second of those with 12 px to spare, which is not a fit worth shipping.

**Sync Regions is now Save Track List**, with a tool tip, because the name was the same fault as the
lines: *sync* says how it works and not which way it goes, and *regions* is this app's word for the
thing it writes rather than anything the user asked for. What the button actually is: the **save
button for the arrangement** — the window's list lives in memory, so nothing else makes it survive
being closed. It marks the tracks on the time ruler and writes them to the `.wlmeta.json` sidecar
immediately, which is what lets reopening the file rebuild the same list. **Export does not need
it** and the tool tip says so, because a button sitting beside Export that looks like a save step is
one people will believe they have to press.

- **The button cannot be clipped and that is not the measurement to make.** It is in a `StackPanel`,
  which measures its children unbounded, so a longer label is never cut — it is *taken out of the
  `*` column beside it*, which holds the validation line and trims. Measured: the label wants
  **127 px** and is given **121** (the 6 px difference is its own margin, the trap already on record
  for the monitor bar), and the validation line goes from 496 px to **475**.
- **That line already did not fit, and the rename made it 21 px worse.** Its DDP wording — program
  length, lead-out and the ISRC tally — wants **536 px**, so it trimmed by 40 px before and by 62
  now. The ordinary WAV+CUE wording wants 319 px and fits with room to spare. The probe **asserts
  the ordinary wording and reports the DDP one**, rather than asserting a failure that predates the
  change or quietly shortening a second readout inside a commit about a button label.

### The validation line, where the wording and the layout turned out to be one problem

The last readout in this window in the old voice, and the one where the two complaints met: it did
not fit **because** of how it was written. "Program length: 79:58 across 99 track(s), aligned to CD
sectors. Lead-out at 79:58:00; 99 of 99 ISRC(s) set." wanted **536 px in a column holding 475**, so
the part being cut was the tail — which is where the DDP user's catalogue tally lives. Saying the
same thing plainly took it to **351 px**, and the ordinary WAV+CUE form from 319 px to **141**.

- **"Program length: 79:58 across 99 track(s), aligned to CD sectors."** → "99 tracks, 79:58 on the
  disc." Everything dropped was either restating the label the number sits under or explaining an
  implementation detail: *sector alignment* is why the figure differs from the source duration, and
  "on the disc" already says that.
- The rest followed the same rule as Add Track's refusals — name what the rule is rather than that
  one was broken. "Track 03 has an invalid source range" → "Track 03 covers no audio - check its
  SOURCE IN and SOURCE OUT." "Track 03 is 2.0 s after CD alignment; tracks must be at least 4 s" →
  "Track 03 comes out 2.0 s long on the disc. A CD track has to run for at least 4 seconds."
  "The sector-aligned program is 1:21:00; the CD target is at most 1:20:00" → "… A CD holds at most
  1:20:00 - shorten one or take one out."
- **These are the only wordings in the window that are read outside it.** `Validate`'s errors reach
  the Export message box and are thrown out of both exporters, so the plain form has to work as a
  sentence on its own rather than only as a line under a list.
- **`FormatDuration` goes to `h:mm:ss` past the hour**, so a 75-minute programme reads **1:15:00**
  and not 75:00 — which is why the warning beside it says "74 minutes" in words. Two test
  expectations were written assuming otherwise; the code was right both times.
- The probe now **asserts** both forms rather than reporting the DDP one. It could not before: that
  wording had been trimming since before the button was renamed, and asserting it would have been
  asserting a pre-existing failure inside a commit about a label.

### Find Tracks: the setting is findable, so the user should not be hunting for it

Reported as *"with the Analyze button it is hard to determine how to fix the problem with the
slider - I want an autofix feature."* Fair: the window was asking for the answer to an **inverse
problem** — which level produces the right tracks — and the only way to answer it was guess, count
the rows, guess again. The three rewrites before this one made the *feedback* honest and left the
hunting exactly where it was.

- **A real gap structure is robust to the threshold and a spurious one is not.** That is the whole
  idea. Measured on three real transfers butted into one 504 s side, every setting from −55 to
  −40 dB proposes the same three tracks with the splits steady within 0.07 s; past −40 they slide,
  by 7.6 s at −30, because a looser threshold calls the fade-out quiet sooner. So the setting to
  use is the **middle of the widest run of thresholds that agree**, and that is a property the
  program can measure and the user cannot see at all.
- **It is affordable because the envelope does not depend on the threshold.**
  `Restoration.BlockPeaks` is the whole cost of a silence pass, and only the comparison after it
  varies — so the envelope is measured once and forty-six thresholds run against it. The old
  `DetectSilences` is now those two in sequence, and
  `MeasuringTheEnvelopeOnceGivesTheSameSilencesAsMeasuringItEveryTime` pins that the split changed
  nothing.
- **A candidate carries the splits at its *chosen* setting, not at the edge its run began from.**
  Find Tracks leaves the slider where its answer came from, so an Analyze pressed straight after
  re-derives from that setting — and if the two differed by even a tenth of a second the user would
  be told the splits had moved by pressing a button that changed nothing.
  `AnalyzeAtTheChosenSettingReproducesWhatFindTracksApplied` is that invariant, and it is also **the
  first direct test `SuggestTracks` has ever had**.
- **An optional track count, because the record label carries a number the audio does not.** Blank
  takes the steadiest answer. Filled in and unreachable, the reply is *which counts are reachable* —
  "This side splits into 1, 3 or 4 tracks, never 6" — which is the sentence that ends the hunt
  instead of sending the user back to the slider.
- **12 sides built from the real transfers: right count on 12, every join placed within 0.8 s.**
  Plateaus ran about 20 dB wide (−58 to −37 typical). `CdAutoSplitCorpusTests` is opt-in on
  `WAVELAB_CORPUS=1` and states its own limitation: a join butted together is real run-out groove
  against real lead-in groove, but it is **not** one continuous groove between two songs on a
  pressing, and this file records five declip calibrations that died of exactly that gap.

**The constant the corpus does not test, said plainly rather than left to be discovered.**
`MinimumPlateauDb` (3 dB) is how wide a multi-track answer must hold before it beats "one long
track". **Every real side produced a ~20 dB plateau, so the guard never binds there and the 12-of-12
result is no evidence about it either way.** It binds only at the top of the sweep, and the reason
is structural: once a gap is quiet enough to register it keeps registering at every louder setting,
so a multi-track answer always runs to the end of the sweep and is narrow only when it first appears
near that end. A stretch a mere 26 dB below the programme is a soft passage inside a song rather
than the space between two.
`AQuietPassageIsNotAGapAndOneDecibelIsNotEvidence` builds that case directly and is the only thing
holding the constant; asking for two tracks **by name** still gets them, because the guard is about
what to choose unprompted rather than about overruling somebody who knows their own record.

- Both halves were checked by mutation: dropping the plateau-width guard fails that test, and
  pointing `ApplyProposal` at `ReplaceTracks` unconditionally fails three.
- `ApplyProposal` is shared by Analyze and Find Tracks, so a swept answer keeps typed titles and
  ISRCs on exactly the terms a hand-set one does.
- Measured in the built dialog: the AUTO SPLIT row went from four controls to seven and nothing in
  it is cut, and the widest new wording wants 515 px of the status line's 756.

### An even gap between tracks, which is a subtraction before it is an addition

Asked for as *"can we have an option to put in some pregaps — silence between tracks."* Two
decisions, both put to the user because the wrong choice on either is invisible until the disc is
burned.

- **Every gap is made the same length rather than lengthened by the same amount.** The splits land
  at the middle of the quiet between two songs, so each track already carries half of whatever the
  record left there — measured on the test side, four seconds at one split and eight at the next.
  Adding a fixed silence on top of that keeps the unevenness and makes it worse. `ApplyGaps` trims
  each split back to the music either side of it and puts back exactly what was asked for.
  **So the setting usually makes the disc shorter, not longer**: with 4 s gaps the test side goes
  from 3:00 to 2:56, because twelve seconds of the record's own quiet came out and eight went back.
- **Nothing above the threshold is ever trimmed**, so a fade only loses the part of itself that had
  already fallen below the level the user called quiet — inaudible by that definition. The level is
  the AUTO SPLIT slider's, so the two halves of the window cannot disagree about where a song ends.
  A track with nothing above the threshold anywhere is left exactly as it is rather than collapsed.
- **It trims the rows visibly rather than doing it at export.** SOURCE IN and SOURCE OUT move and
  can be read and corrected. A gap arranged in secret would be a plan that does not describe the
  disc, which is the fault this window has been reported for four times.
- **It is idempotent**, which is what lets it be re-applied whenever the list changes underneath it:
  a range already trimmed to its music trims to itself. A gap is an instruction about the disc, not
  a one-off edit, so re-analysing must not silently drop it — `RetrimForGap` runs from
  `ApplyProposal` and `RefreshOrder`, and `AGapSurvivesTheListBeingRebuiltUnderneathIt` pins it.
- **The silence is the incoming track's pregap, not its opening**, so choosing a track starts on the
  music the way a shop-bought CD does rather than serving two seconds of dead air. That cost a
  format change in both deliverables: the cue sheet gains `INDEX 00` / `INDEX 01`, and the PQ
  descriptor — which had one INDEX 01 row per track hardcoded — gains a row of its own for the gap.
  `BothDeliverablesCarryTheGapAsAPregapRatherThanAsTheTrackOpening` checks the two against each
  other, because a gap in one and not the other is two discs described by one window.
  **Track 01 never carries one**: the two-second lead-in every disc begins with already is it.
- The silence is **real samples in both**, not a note in the sheet, because a DDP image has to carry
  it and both deliverables are cut from the same programme.
- **Found on the way past: `PQDESCR` is written as `Encoding.ASCII`, and its own header carried an
  em dash** — so every image set ever written says `# PQ descriptor ? 3 tracks`. Same trap this file
  already records for the AIFF text chunks. ASCII is defensible for a file a plant's systems read;
  putting a character outside it into that file is not. The header is a hyphen now and the test
  asserts the whole sheet is question-mark free, which catches the next one.

### Review of the CD window: two real faults, and a guard that turned out to be already there

- **A proposal outlived the document it was measured against.** Both analysis paths snapshot the
  audio, await, and write the result into the list — with nothing checking the document is still
  the one they measured. **This window is modeless precisely so the waveform stays editable
  underneath it**, and a splice reaches `OnSourceEdited`, which carries every row onto the new
  timeline; the continuation then overwrote those rows with ranges derived from audio that no
  longer existed, putting each track on music it was never measured against, silently. Preview and
  Export have always checked `EditVersion`. The analysis paths never had, and Find Tracks
  inherited the omission along with a longer window to hit it in.
  `RefuseAStaleProposal` is the check; `AnEditDuringAnAnalysisIsRefusedRatherThanAppliedToTheWrongAudio`
  is **deterministic rather than a race** — raising Click runs an `async void` handler as far as its
  first await and returns, so the test edits the document while the analysis is genuinely in flight.
- **The gap trim walked the audio on the dispatcher, on every arrow press.** `RefreshOrder` reaches
  `RetrimForGap`, and `FirstAbove`/`LastAbove` ran inward from each track end until they met music
  — so a track with nothing above the threshold, which is what a run-out or a quiet interlude is,
  made each one walk the whole track, uncancellable, with `RefreshOrder` on the path of Add, Split,
  Remove and both arrows. The same envelope that made the sweep affordable fixes it: a block's
  entry is the largest magnitude in it, so a block under the threshold **cannot** hide a sample at
  or above one, and only a block that clears it is read sample by sample. Exact, and a walk over a
  two-hundred-and-fifty-sixth of the audio.
  `TheEnvelopeSearchFindsExactlyWhatWalkingEverySampleWould` compares it against a brute-force
  reference, because "faster" is worth nothing here if it is not the same answer.
- **The envelope is cached on the dialog and dropped in `OnSourceEdited`**, so a gap applied after
  an edit pays one pass and every press after it pays none. A cached envelope of the wrong length
  is rebuilt rather than trusted, which is what makes a stale cache a slow path instead of a wrong
  answer.
- **A pregap can only be a whole number of CD frames**, and the box rounded entries to tenths — so
  0.1 s was seven and a half frames, reached the disc as eight, and the readout said 0.1 while the
  disc got 0.107. `SnapGapSeconds` rounds to frames and the box shows what it snapped to. Whole
  seconds are exact either way, which is why it took a review to see.
- `_operation.Token` was read **inside** the `Task.Run` lambda, so a field the `finally` is free to
  null was being dereferenced on the pool. Unreachable today because `_busy` serialises operations;
  a latent `NullReferenceException` for whoever adds a second one. Captured into a local, which is
  what `OnPreview` already did.
- **The fifth finding was already fixed and the mutation test is what said so.** A guard against
  mismatched channel lengths in `ApplyGaps` looked obviously right — the length is read off channel
  0 and every channel indexed to it. Removing it changed no test, because measuring the envelope is
  the first thing that touches the audio and `Restoration.BlockPeaks` validates. **A guard whose
  presence no test can detect is dead weight**, so it came back out and
  `MismatchedChannelLengthsAreRefusedByName` pins the behaviour instead. Four of the five mutations
  failed a named test; the one that did not is the one that should not have been written.

### The cue sheet credited the application on every disc

Found while checking Export, which had **no test at all** — `ExportDdpAsync` was covered and
`ExportPackageAsync`, the dialog's *default* deliverable, was not. The cue writer emitted a fixed
`PERFORMER "Deep Groove Transfer"`, so every disc burned from one carried CD-TEXT naming the app as
the artist. Meanwhile the dialog's own DISC PERFORMER box was greyed out in WAV+CUE mode with a
tooltip reading "DDP only" — a field that could not be filled in, standing beside a line that was
filled in with something nobody had said.

- The typed performer is threaded through `ExportPackageAsync`, and **blank stays blank**: the
  `PERFORMER` line is omitted rather than invented, which is the rule `CdTrackPlan` already states
  about a track's performer, and a plant or a burner reads a sheet as a statement of fact.
- Disc performer and the per-track Performer column are live in **both** deliverables now, because a
  cue sheet carries `PERFORMER` at both levels. UPC, ISRC and pre-emphasis stay DDP-only and
  `TrackRow.DdpFields` gates those three alone. (A cue sheet can carry `CATALOG`, `ISRC` and
  `SONGWRITER` too; leaving those out is the existing design, not an oversight found here.)
- `TheCueSheetCarriesThePerformersThatWereTypedAndInventsNone` is the first test of that exporter at
  all. It asserts the count of `PERFORMER` lines, not just their presence — a disc line plus one
  track line for three tracks of which two are anonymous — because "contains the right string" would
  pass a writer that emitted a default for the other two.

## The workbench holds an analysis, which is a harder thing to be modeless about

Same treatment for `RestorationWorkbenchDialog`, and the registry, the tab-close and the rack all
came out the same shape. What did not is the middle one, because this window does not hold a *view*
of the document the way the CD list does — it holds a **measurement** of it: channel arrays, a
range, an edit version, taken once, rendered from, and spliced back over the document at Apply.

Modal, the source could not move. There was already a version check before the splice, for an async
race nobody expected to hit; it refused with "Reopen the workbench to analyze the current audio"
*after* paying for a full restoration render. Modeless, editing while it is open stops being a race
and becomes a thing people do, so:

- `Doc.Changed` sets `_sourceStale`, which disables both Apply buttons and prints the reason next to
  the range the analysis describes. Refused at the edit, not after the render. The post-render check
  stays — a render can outlive the edit that invalidates it — and now sets the same flag.
- A selection move is *not* that. The captured range is still the range that was analyzed, so
  `_rangeStale` only offers a re-scope and leaves Apply alone. Two flags, worded apart, because only
  one of them makes what the window is holding wrong.
- **Re-analyze** re-runs the capture and the scan against whatever the document has become. Every
  `readonly` capture field had to stop being one; `CaptureSource(firstCapture)` is the single place
  they are taken, and the flag exists for `keepRemovedCheck` alone — a re-capture must clear the box
  if the new range is past the residual budget, but must not otherwise re-read a stored preference
  over a choice the user has already made. It re-tunes every control, exactly as reopening did.

No locking guards the capture fields against the worker threads that read them, and none is needed:
`OnReanalyze` returns on `_busy` and `UpdateStaleChrome` disables the button for the same reason, so
a capture can never be replaced while a scan or a render is reading it.

`DialogResult = true` is the one line that could not survive this — it throws on a window that was
shown rather than shown modally. It became `Close()` plus an `Applied` event carrying
`PrepareCdRequested`, which is what `ShowFor`'s `onApplied` wires to the CD hand-off.
`ApplyingCommitsOneUndoableEditAndReportsThroughTheAppliedEvent` exists for that line specifically:
it runs a real analysis and a real apply on two seconds of tone, because no amount of layout or
readout coverage would have reached it. The rack path is the one thing untested — bypass-on-preview
needs both a finished analysis and playback — so the snapshot now following a bypass the user works
themselves rests on inspection.

## Gotchas

- **A dialog that vetoes its own close while busy must re-issue it.** `CdTransferDialog`,
  `RestorationWorkbenchDialog` and `MontageRenderDialog` all cancel a close while an operation runs,
  so an X cannot abandon a render mid-flight — and all three therefore need `_closeWhenFinished`,
  which `SetBusy`/`CompleteOperation` acts on once the work unwinds. `MontageRenderDialog` was
  missing it, so X during a render cancelled the render and left the window standing. The matching
  error is the other direction: do not clear `_busy` to force a close through. `CdTransferDialog`
  did that when its document's tab closed, which took the window down while an export was still
  unwinding — a write that had already finished then parented its "CD Package Ready" dialog to a
  dead owner, and a package sitting correctly on disk was reported as failed.
- **A modeless window's registry entry goes in after `Show()`, not before.** `ShowFor` on both
  modeless dialogs keys a static dictionary by document. Registering first means a `Show` that
  throws leaves an entry nothing can clear — the `Closed` that removes it never runs — and every
  later request raises a window that was never shown, where `Activate` fails silently.

- Absolutely-positioned canvases in the HTML mockups need explicit width/height 100% (replaced elements ignore inset stretching).
- `EnableWindowsTargeting` is set so the project also builds on non-Windows CI.
- WPF menu/combo templates are custom — new menu items inherit styling automatically; keep `InputGestureText` in sync with `Window.InputBindings` in MainWindow.xaml.
- The Recent Files submenu is a `CompositeCollection`: the paths, a rule, then Clear Recent Files. **Nothing in it may be a declared `MenuItem`.** One parsed there is styled while it belongs to no `ItemsControl`, and the content-alignment bindings the theme `MenuItem` style carries then have no ancestor to find — two `System.Windows.Data Error: 4` lines at every launch, which neither a local `Style` nor local alignment values suppress, and which `ShellWindowTests` fails on. Both halves are therefore bound collections of `MenuEntry` (text, command, parameter) that the menu generates containers for, reached through the `BindingProxy` resource `ShellContext` because a `CollectionContainer` is in neither tree and a plain `{Binding}` there resolves against nothing — silently, leaving a submenu that opens, shows Clear, and lists no files. `RecentFilesMenuTests` counts the paths for that reason.
- Menu headers run through a `ContentPresenter` with `RecognizesAccessKey="True"` (Theme.xaml), so any *text* bound into a header needs `AccessKeyEscapeConverter`: an unescaped underscore is eaten from the display and claims the next character as a shortcut — `take_1.wav` listing as `take1.wav`, with 1 invoking it. The recent-file list is the only place this arises today, and the escape is on the header binding only: `CommandParameter` stays the stored path, or Open would be handed a path that never existed.
