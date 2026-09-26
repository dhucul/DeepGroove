# Lyrics fine-tuning research results — 2026-09-25

The first pilot improves recognition on many singing excerpts, but **is not ready
to replace the bundled model**. Instrumental hallucinations become longer and
the uncertain opening line in the user's regression song is not resolved.
Release has not been changed. A fresh run with corrected instrumental supervision
has been started; its accuracy has not yet been established.

## First pilot

- Base: `openai/whisper-large-v3`, revision
  `06f233fe06e710322aca913c1bc4249a0d71fce1`.
- 600 optimizer updates; checkpoint 500 selected using 42 development clips.
- MUSDB18-HQ official training partition plus the lyrics extension. 81 songs used
  for gradient updates; 14 songs reserved by artist. No official test data used.
- Full comparison: 504 examples = 227 annotated singing intervals in both mixture
  and original-vocal forms, plus 50 instrumental mixtures. The two audio views are
  correlated, not independent recordings. There are 1,792 normalized reference
  words per singing view (3,584 when both views are counted).
- Fixed three-beam, English transcription, BF16, 128-token generation limit. This
  measures raw short-excerpt generation; it does not use the application's vocal
  separation, repetition fallback, timing, or recovery passes.

| Measure | Original model | First trained model |
|---|---:|---:|
| Word error rate, 42 selection clips | 81.13% | 29.06% |
| Word error rate, all 504 examples | 38.31% | 44.42% |
| Word error rate, annotated singing in mixtures | 42.41% | 33.04% |
| Word error rate, original vocal stems | 19.42% | 11.61% |
| Deleted words, both singing views | 253 | 146 |
| Instrumental excerpts with invented words (of 50) | 30 | 16 |
| Total words invented on instrumental excerpts | 265 | 792 |
| Generations hitting the 128-token limit | 15 | 13 |

The small selection subset overstates the overall gain. There are fewer missing
words and fewer instrumental false-positive events, but those remaining events
can produce much longer invented passages. In the 426 examples whose intervals
were not used for checkpoint selection, WER increases from 34.15% to 48.04%.

Both models were rerun on the union of the 26 capped examples with a 440-token
limit. Replacing those predictions in the complete set gives 81.84% versus 84.93%
WER and 794 versus 1,955 invented words on instrumental excerpts. The original
model still hits the longer cap in 15 cases; the trained model ends within the cap
but can produce long invented passages. These are bounded generation measurements,
not evidence that either model achieves that WER in the application. Both cap
settings and all raw predictions are preserved locally for inspection.

## Full-song application-engine regression

The selected adapter was merged on CPU and converted to FP16 CTranslate2. The
export was verified to change decoder weights, preserve the sampled frozen encoder
weight, and retain alignment heads and 128-bin features. Both models then ran the
unchanged vocal-isolation and timed-lyrics recovery workflow on the local regression
song. No lyric hints or reference words were provided to inference.

The trained model retains the user-confirmed wording in the main chorus passages,
but both models produce essentially the same uncertain opening wording. The trained
model groups more phrases into longer segments and adds questionable words near the
ending. Line counts (15 versus 7) do not measure word omissions because segmentation
differs. Without a verified complete reference, this is a qualitative regression
check, not a song-level accuracy score.

## Correction and current status

The first recipe incorrectly supervised instrumental audio as an empty English
transcript, including an English-language target after the start token. That can
conflict with Whisper's no-speech classification. The corrected recipe supervises
the no-speech token there and EOS under the supplied transcription prompt, while
masking the contradictory intermediate targets. The acoustic cache is reused but
its old labels are ignored. Old checkpoints cannot resume under the new recipe.

Eleven pipeline tests passed. The real large-v3 tokenizer verified the target IDs,
and a two-update GPU check containing both positive and instrumental examples
completed with finite losses and gradients. This verifies execution, not accuracy.
The new 600-step run starts from the original base, not the first adapter or smoke
checkpoint, and saves under `runs/large-v3-lora-nospeech-20260925`.

These reserved songs have now informed a recipe correction and should be regarded
as development data. An independent final test, complete-song checks, speech
regression checks, and dataset-use review are still required before promotion.

## Local evidence

All paths below are relative to `artifacts/lyrics-training/` and are excluded from Git:

- `runs/large-v3-lora-pilot-20260925/`: first training, baseline, checkpoints, metrics.
- `evaluations/large-v3-pilot-full-validation/`: complete paired 128-token comparison.
- `evaluations/large-v3-pilot-cap-check/`: longer-cap check and combined predictions.
- `evaluations/cant-catch-me-pipeline/`: complete-song original/adapted transcripts.
- `exports/pilot-best-step500/`: research-only converted model and provenance.
- `runs/no-speech-label-check/`: real positive/negative training smoke test.
- `runs/large-v3-lora-nospeech-20260925/`: corrected training run.

Dataset sources: [MUSDB18-HQ](https://zenodo.org/records/3338373) and
[lyrics extension](https://zenodo.org/records/3989267). No audio, reference lyrics,
or trained weights are published in this branch. This remains an
educational/noncommercial research pilot. The no-speech training target follows
the [Whisper paper](https://cdn.openai.com/papers/whisper.pdf); the extra conditional
empty-transcript target is a local adaptation for the application's prompting.
