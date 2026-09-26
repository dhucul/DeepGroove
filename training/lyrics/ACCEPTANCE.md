# Corrected singing-model acceptance check

Completed: the candidate did not meet the lyric-upgrade criteria. See the full
[results and decision](RESULTS.md). The existing single Release app was retained.

The candidate is the fixed 200-step checkpoint selected by the corrected run's
42-clip development score. The 600-step checkpoint performs worse and is not used.
The model, inference settings, and fresh-test selection are fixed before fresh-song
or speech results are inspected. No test audio is used for gradient updates.

## Checks and decision

1. Repeat all 504 development examples with the corrected candidate. Reuse the
   original model's unchanged predictions only after validating the base revision,
   manifest hash, generation cap, example order, labels, and selection flags.
2. Rerun the union of capped outputs for both models with a 440-token limit.
3. Compare the original and candidate models on the user's local regression song
   with the existing vocal-isolation, timing, and recovery workflow.
4. Run the same complete-song workflow on five fresh songs from the official test
   partition. Select one song per artist using seed 20260925, excluding every artist
   present in training or development. Do not change the selection after scoring.
5. Compare 24 fresh read-speech recordings from eight speakers through the
   application's existing Speech mode.

A replacement needs improved word recognition and omission rates on fresh songs,
without a material increase in invented instrumental words or regression of the
user-confirmed chorus. Speech must retain its existing accuracy; if a singing
specialization is accepted, ordinary Speech mode can retain the original model.
No release is justified by the small development score alone. Failure of these
checks leaves the existing Release model in place. Results are recorded in
[RESULTS.md](RESULTS.md).

## Fresh music

The deterministic selection is:

- Angels In Amplifiers — I'm Alright
- M.E.R.C. Music — Knockout
- Girls Under Glass — We Feel Alright
- Juliet's Rescue — Heartbeats
- Mu — Too Bright

The complete recordings are transcribed, but scoring uses only 160 trusted,
nonoverlapping intervals. Uncertain and conflicting-vocal annotations are excluded,
as are intervals overlapping another annotation. Word midpoints select recognized
words inside each interval; identical repeated word/timestamp records are counted
once. Reports provide both interval-level WER and WER after joining each song's
trusted intervals, so small boundary errors can be distinguished from wrong words.
Instrumental hallucinations are also counted separately.

These public reference lyrics are manually transcribed but not guaranteed to be
correct. See the authors' [dataset description](https://zenodo.org/records/3989267).
They are restricted to noncommercial research. Audio comes from
[MUSDB18-HQ](https://zenodo.org/records/3338373). This test is fresh relative to this
fine-tuning project; the original pretrained model's exposure is unknown.

## Fresh speech

The [LibriSpeech test-clean set](https://www.openslr.org/12) supplies 24 recordings,
three per selected speaker, each 2–25 seconds long. The source archive's published
MD5 is verified before extracting selected regular audio files. Selection uses the
same fixed seed and precedes model evaluation. LibriSpeech is licensed CC BY 4.0
and is derived from LibriVox audiobook speech; this small read-speech check does not
cover every conversational, noisy, accented, or multilingual use case.

## Reproduction

Run from the repository root with the research Python environment:

```powershell
$python = (Resolve-Path artifacts/lyrics-training/environment/Scripts/python.exe).Path
& $python training/lyrics/prepare_acceptance.py music `
  --training-manifest artifacts/lyrics-training/musdb/manifest.jsonl `
  --output artifacts/lyrics-training/acceptance-data/music
& $python training/lyrics/prepare_acceptance.py speech `
  --output artifacts/lyrics-training/acceptance-data/speech
```

Use `evaluate_adapter.py --reuse-base-from <completed-original-comparison>` for the
corrected development comparison. This option cannot be combined with a cap recheck.
Use `compare_pipeline.py` from the bundled inference Python for each complete song,
then `evaluate_speech.py` for the speech examples. Finally, `score_acceptance.py`
uses the research tokenizer's English normalization and Jiwer to score the saved
outputs. It expects fresh-song directories named `song-01` through `song-05` in
selection order and speech outputs in `speech`.

All audio, transcripts, and model files remain in ignored `artifacts/lyrics-training/`.
The branch contains tools, methodology, and aggregate results only. The corrected
evaluation directories are `evaluations/nospeech-full-validation`,
`evaluations/nospeech-cap-check`, and `evaluations/nospeech-acceptance`.
