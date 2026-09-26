# Local lyrics fine-tuning pilot

This trains a small decoder LoRA adapter for `openai/whisper-large-v3` on public
singing recordings. Training runs locally on an NVIDIA GPU, requires no API key,
and never modifies the application or its bundled Release engine. All downloads,
features, logs, checkpoints, and transcript reports stay under ignored `artifacts/`.

See [RESULTS.md](RESULTS.md) for the first pilot's measured gains and regressions.
It was not promoted to Release. A corrected training run was started after evaluation.

## Data and evaluation

- Audio: [MUSDB18-HQ](https://zenodo.org/records/3338373).
- Labels: [MUSDB18 lyrics extension](https://zenodo.org/records/3989267),
  `train_lyrics.zip`, published MD5 `dc89e2175edb94eca26dd504a2eef1c7`.
- Only the official **training partition** is downloaded. The official test
  partition remains untouched. Each selected song supplies its mixture and vocals.
- Labels are manual author transcriptions, not independently verified ground
  truth. Rows marked uncertain (`*`, `?`) and overlapping different words (`c`)
  are excluded. Same-word harmonies (`b`) are retained. Instrumental negatives
  (`d`) use the mixture only and are limited to about 10% of examples.
- A deterministic artist split reserves 14 songs for validation and uses 81 songs
  for training: 3,231 training examples and 504 validation examples, including the
  two audio views. These are not 3,735 independent recordings.
- The annotation/audio boundary tolerates at most 100 ms of timestamp rounding at
  the end of a file; larger errors stop training.
- Baseline and periodic validation use the same fixed, song-balanced subset of
  original mixtures (42 clips with the default 48-clip cap). Word error rate
  records substitutions, deletions, and insertions. Lower is better; scores can
  exceed 100% when the model inserts many words. Empty references also count
  hallucinated words as insertions.

The dataset is restricted to educational/noncommercial research. This is a local
research pilot, with no trained-model publication or production deployment. See
the linked dataset terms and downloaded `ANNOTATION-LICENSE.txt` before other use.

## Reproduce (PowerShell, from the repository root)

Use Python 3.11 and an isolated environment, **never the Release inference engine**.
An RTX 5060 with 8 GB VRAM passed the two-step large-v3 training check, with about
3.7 GiB peak tensor allocation. Longer lyrics can use more memory. The recipe
requires CUDA and BF16 support and fails explicitly without them.

```powershell
py -3.11 -m venv artifacts/lyrics-training/environment
$python = (Resolve-Path artifacts/lyrics-training/environment/Scripts/python.exe).Path
& $python -m pip install torch==2.8.0 --index-url https://download.pytorch.org/whl/cu128
& $python -m pip install -r training/lyrics/requirements.txt
& $python training/lyrics/download_model.py --output artifacts/lyrics-training/base-large-v3
& $python training/lyrics/prepare_musdb.py --output artifacts/lyrics-training/musdb
& $python -B -m unittest discover -s tests/training -v

$run = 'artifacts/lyrics-training/runs/large-v3-lora-pilot'
& $python training/lyrics/train_adapter.py `
  --manifest artifacts/lyrics-training/musdb/manifest.jsonl `
  --base-model artifacts/lyrics-training/base-large-v3 `
  --output $run
```

The model revision is pinned to
`06f233fe06e710322aca913c1bc4249a0d71fce1`. Data preparation downloads about 5.31 GiB
of compressed audio using archive range requests; extracted audio, the base model,
cached features, and checkpoints require additional disk space. Completed audio
members are reused on retry. New members are checked with the ZIP CRC and length
before being renamed into place. Network access is disabled during training.

Default training is 600 optimizer steps with eight examples accumulated per step,
rank-16 decoder attention adapters, learning rate 0.0001, 5% warmup, and fixed seed
42. The encoder stays frozen. Checkpoints are saved every 25 steps, and the same
validation clips are checked every 100 steps. No data from the user's music library
is used for this pilot.

The current label recipe is `whisper-nospeech-conditional-eos-v2`. Instrumentals
teach `<|nospeech|>` immediately after the start token, following Whisper's
[training format](https://cdn.openai.com/papers/whisper.pdf). They also teach EOS
under an explicitly supplied language/transcription prompt, since that is how the
application decodes. Language/task targets on instrumental inputs are masked so
they do not contradict the no-speech target. Explicit decoder inputs keep this
conditioning separate from the supervised labels. This additional conditional-EOS
objective is our research adaptation, not a claim about OpenAI's original recipe.

Acoustic features can be reused, but labels/decoder inputs are regenerated when
loading the cache. Resume rejects checkpoints from the old label recipe. Start
fresh from the base model when switching recipes; do not resume the first pilot.

## Inspect, stop, and resume

For the initial background run started on September 25, 2026, use
`artifacts/lyrics-training/runs/large-v3-lora-pilot-20260925` as `$run`.
The corrected run uses
`artifacts/lyrics-training/runs/large-v3-lora-nospeech-20260925`.
The trainer owns its log file; `status.json` is replaced atomically after every
optimizer step. A running job cannot reuse an existing output directory.

```powershell
Get-Content "$run/status.json"
Get-Content "$run/training.log" -Tail 15

# Request a checkpoint and graceful stop at the next optimizer boundary.
# A baseline/validation pass may finish first.
New-Item -ItemType File -Path "$run/STOP" -Force
```

Wait for phase `stopped` before resuming. For a graceful stop, read
`stopped-checkpoint.json`; after a crash, use the newest complete numbered
checkpoint, normally referenced by `latest-checkpoint.json`. Remove only the
`STOP` file, then repeat the training command with `--resume <checkpoint-folder>`.
Keep the same output directory, manifest, model, step count, learning rate, and
gradient accumulation. Checkpoints include optimizer, scheduler, and torch random
states. Dataset/model/accumulation mismatches are rejected. Source audio and cached
features must not change during a run. Concurrent jobs using the same feature cache
are unsupported.

## Promotion criteria

`best-checkpoint.json` names the best adapter on the development validation subset;
`final-checkpoint.json` names the final step. Neither is automatically loaded by
WaveLab. `improved_over_base` means only that this subset's WER improved; it does not
establish an improvement on complete songs or the bundled transcription pipeline.

Before considering a replacement model, evaluate all reserved songs using the
application's mixture/vocal workflow, inspect missing opening lines and repeated
choruses, check hallucinations and speech regressions, and resolve redistribution
rights. Keep an independent test set for final evaluation after model selection.

## Broader validation and application-engine check

After the pilot, evaluate all 504 reserved mixture/vocal examples against the frozen
base. The evaluator waits for the training run to complete, then selects the saved
best checkpoint. Both comparisons use the same base weights, preprocessing, and
three-beam decoding as the development check. Generation is capped at 128 new
tokens; the report counts any truncated outputs, which need a longer-cap rerun
before drawing conclusions. No training examples enter evaluation.

```powershell
& $python training/lyrics/evaluate_adapter.py `
  --training-run $run `
  --manifest artifacts/lyrics-training/musdb/manifest.jsonl `
  --base-model artifacts/lyrics-training/base-large-v3 `
  --output artifacts/lyrics-training/evaluations/full-validation
```

Read `status.json` for progress and `comparison.json` for grouped results. Reports
separate mixtures, clean vocal stems, missing words, instrumental hallucinations,
first annotated phrases, and individual songs. `unseen_excerpts` excludes every
interval used for model selection, including its paired vocal/mixture view. These
are still the same reserved artists as development validation, not an independent
final test. Ground-truth vocal stems also differ from estimated Demucs vocals.

If either model reaches the output cap, recheck the union of those examples with a
440-token cap. Both models rerun the same affected excerpts. This command writes
the original full-set metrics again with those predictions replaced; its
`*-all-predictions.json` files preserve the combined complete set. Any remaining
cap hits must still be reported as bounded/repetitive output.

```powershell
& $python training/lyrics/evaluate_adapter.py `
  --training-run $run `
  --manifest artifacts/lyrics-training/musdb/manifest.jsonl `
  --base-model artifacts/lyrics-training/base-large-v3 `
  --only-capped-from artifacts/lyrics-training/evaluations/full-validation `
  --max-new-tokens 440 `
  --output artifacts/lyrics-training/evaluations/full-validation-cap-check
```

For an application-engine regression check, install `requirements-export.txt` into
the research environment and export the selected checkpoint. Export merges on CPU
in FP32 before converting to FP16 CTranslate2. Only `artifacts/` destinations are
accepted; the Release model and source checkpoint are never overwritten.

```powershell
$checkpoint = (Get-Content "$run/best-checkpoint.json" -Raw | ConvertFrom-Json).path
& $python -m pip install -r training/lyrics/requirements-export.txt
& $python training/lyrics/export_adapter.py `
  --checkpoint $checkpoint `
  --base-model artifacts/lyrics-training/base-large-v3 `
  --output artifacts/lyrics-training/exports/pilot-best

$bundle = (Resolve-Path src/WaveLab/bin/Release/net10.0-windows/Transcription/Engine).Path
& "$bundle/python/python.exe" -I training/lyrics/compare_pipeline.py `
  --bundle $bundle --input 'C:/path/to/song.wav' `
  --candidate artifacts/lyrics-training/exports/pilot-best/ctranslate2 `
  --output artifacts/lyrics-training/evaluations/song-regression
```

Run this GPU comparison after the broader evaluation finishes. It isolates vocals
once and applies the unchanged timed-lyrics recovery pipeline with both models,
including the original-mix and focused-replay passes. No lyric hints or reference
words are fed into the recognizer. The resulting transcripts require comparison
with a verified reference or human listening; line counts alone do not establish
accuracy. Speech regressions and an independent final set remain separate checks.
