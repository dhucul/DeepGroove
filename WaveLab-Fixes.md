# WaveLab — Audit Fixes Applied

**Result:** all 81 findings fixed across 59 files. `dotnet build` clean (0 errors, 0 warnings), `dotnet test` 229/229 passing, 3 consecutive runs with no flakiness. `59 files changed, 2150 insertions(+), 610 deletions(-)`.

**Nothing is committed.** All changes sit in the working tree on `main` for you to review (`git diff`). The only untracked file is `WaveLab-Audit.md`.

---

## Verification

The CRITICAL defect was proven fixed, not just assumed. A temporary test compared saturating a mono tone against the identical signal as stereo (which must match):

| Build | Worst L/R vs mono divergence |
|---|---|
| Original shared-cursor code | **0.232624** (~23% of full scale) |
| After the fix | **0.000000** |

The temporary test was removed afterwards. Everything else rests on the build plus the existing 229 tests.

One note from that exercise: `git checkout HEAD -- <file>` silently no-op'd on this repo (exit 0, file unchanged) — probably a Visual Studio file lock. Worth knowing before you rely on it to discard any of these changes; `git restore` or a fresh checkout is safer.

---

## Behaviour changes you should know about

These are deliberate consequences of the fixes, not regressions — but they are user-visible.

**Audible / signal-affecting**

- **Advanced noise reduction sounds different.** The Ephraim-Malah gain was inverted (loud bins crushed, noise passed at unity). It is now a correct MMSE-STSA estimator with proper Bessel terms. Output should move toward "tone survives, noise attenuated".
- **Limiter true-peak now actually works.** The old "2× oversampling" computed convex combinations, which mathematically cannot exceed the endpoints it already had — it provided no protection. It is now a real BS.1770 4-phase polyphase FIR, so material near full scale gets gain reduction it previously did not. Detector cost rises to ~48 MAC/sample/channel when enabled.
- **Stereo Width: MONO BASS and SPLIT FREQ changed meaning.** `SPLIT FREQ` was a dead control (filters built, never used) and `MONO BASS` doubled as the crossover. Now `SPLIT FREQ` owns the split and `MONO BASS` does what its label says. At defaults the side is split at 300 Hz with low ×1 / high ×1.2 instead of ×1.2 broadband. **Existing Stereo Width presets will sound slightly different.**
- **Hum removal AUTO DETECT now retunes the notches.** Previously only the readout changed — the notches stayed at whatever MAINS was set to. A 50 Hz transfer with AUTO DETECT on will now actually be notched at 50 Hz.
- **Channel Balance AUTO ALIGN now works and no longer overrides you.** The old autocorrelation of `L·R` could only ever return lag 0 with saturated confidence, silently cancelling manual ALIGN. M/S MODE also did literally nothing (algebraic identity) and now applies balance to mid/side.
- **De-click "Smooth Edit Point" no longer writes ~17 dB over full scale.** The bridge is now bounded by the window endpoints.
- **Loudness meter's integrated LUFS / LRA now describe the most recent hour.** Bounding the unbounded block list required dropping something; sessions under an hour are bit-identical. Raise `MaximumBlockLoudnessEntries` if you need longer.
- **`Processing.Crossfade` is now effectively a no-op.** The honest unity-gain fix on a single buffer reduces to ×1 — only the +3 dB bulge was ever real. A genuine join crossfade needs two source regions; that is a feature, not a fix. It has no call sites today.
- **`NormalizeLoudness` now measures real BS.1770** instead of unweighted RMS, so its gain values change (they were several dB off) and it is much slower on long files. Also has no call sites today.
- **`RepairClicksSpectral*` was rewritten** (it previously wrote a silent window-shaped hole — a new click). It is now bounded Papoulis-Gerchberg gap reconstruction that falls back to the existing cubic bridge if it misbehaves. This API is unreferenced in `src/` and `tests/`, and its audible quality is **unvalidated** — nobody has listened to it.

**UI / workflow**

- Pressing **Record while a previous take is still finalizing** now shows "The previous recording is still being finalized" instead of freezing the UI for 5+ seconds.
- **Opening the record dialog now stops playback** (previously it could record the playback monitor path through software playthrough).
- **Batch convert now prompts before overwriting** existing files in the output folder — one batch-wide Yes/No, so it is all-or-nothing; skipped files are marked and counted separately.
- **Remove Clicks / Remove Hum no longer show their parameter dialog** when there is no document open (previously the dialog appeared and did nothing).
- The **amplitude ruler repaints on every edit**, not just on zoom — strictly more redraws, which is the point of the fix.
- The **markers panel refreshes after any main-window edit**, which also clears its list selection.
- CD transfer / CD import / restoration workbench **close on the first X click** during a long operation instead of needing two.
- Preview status lines now say **"Preview is unavailable while recording audio is active…"** instead of falsely claiming playback.

---

## What could NOT be verified

Be aware these rest on inspection plus a clean compile only:

- **The entire CD subsystem.** No physical drive or disc, and there are no CD tests. This includes the riskiest single change: reusing the unmanaged scratch buffers and completion event in `WindowsCdAudioPlatform.InvokeIoControl`. The recycling rule is that buffers are detached (never reused) on both paths where `CancelIoEx` can leave an I/O pending, so nothing the kernel may still write into is recycled — but that reasoning has not been exercised at runtime. If you want to de-risk one thing before shipping, make it a full disc rip.
- **The session-gap fix is heuristic.** It subtracts ~11,400 sectors on any audio→data transition. That is right for multi-session CD-Extra discs (the bug). On a *single-session* disc whose data track sits last — rare but legal — the preceding audio track would be truncated by ~2.5 minutes. Distinguishing the two needs full-TOC session data the `ICdAudioDevice` abstraction does not expose.
- **Every WPF dialog path.** The app was never launched; no XAML binding, dialog flow, or render path was exercised. Bindings were checked against the real `.xaml` files by hand.
- **Audible DSP quality.** The tests assert numeric properties, not how anything sounds. The noise-reduction, limiter, stereo-width and hum-removal changes all warrant a listening pass.
- **Filter coefficient tearing is only fully fixed in two effects.** `FilterEffect` and `EqEffect` now publish immutable coefficient snapshots. Compressor, Gate, Delay, Trim, Hum and Stereo Width use the audit's `CopyCoefficientsFrom` approach, which preserves filter state across parameter changes but can still be read half-applied by the audio thread. Converting them is a mechanical follow-up.

---

## Cross-cutting sweeps completed

- **8 `foreach` over `Biquad[]` reset sites fixed** — `Biquad` is a struct, so every one of those resets was discarded. A folder-wide grep confirms none remain. Consider making `Biquad` a `sealed class`, or adding an analyzer rule, to stop this recurring.
- **7 whole-struct filter rebuilds** now copy coefficients instead, so delay state survives a parameter tweak.
- **3 unbounded UI-thread waits** now have timeouts, and `RecordingEngine` releases its finalize gate *before* the long document flatten.
- **2 modal-dialog reentrancy holes** closed by capturing the target document before `ShowDialog()` and re-validating after.
- **Test isolation:** `AppSettings.AppDataDir` is now redirectable, so `GuiActionStatusTests` no longer reads your real `settings.json` or writes into your real `%AppData%\WaveLab\Presets`. Three toothless assertions were given teeth and the wall-clock performance assertion was replaced with a deterministic one.

---

## Suggested next steps

1. `git diff` review — 59 files, largest changes in `Restoration.cs`, `Limiter.cs`, `LoudnessMeter.cs`, `RecordingEngine.cs`, and the codecs.
2. A listening pass on noise reduction, the limiter, stereo width and hum removal.
3. A full CD rip against a real disc before shipping the CD changes.
4. Re-check any saved Stereo Width presets.
