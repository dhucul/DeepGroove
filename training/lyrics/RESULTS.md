# Lyrics fine-tuning research results — 2026-09-25

The first pilot improves recognition on many singing excerpts, but **is not ready
to replace the bundled model**. Instrumental hallucinations become longer and
the uncertain opening line in the user's regression song is not resolved.
The corrected run also **does not qualify for a Release upgrade**. Its independent
complete-song word score is essentially tied, with worse timed-lyric performance
and no reduction in omissions. Speech and instrumental handling improve, but the
main lyric-recovery goal is not met. The existing single Release app and model
remain in place. Both completed evaluations are documented below.

A later, separately evaluated [pause-aware retry update](PHRASE-BOUNDARIES.md)
improved error rates modestly and was applied to the existing Release scripts.
It uses the original model; neither fine-tuned model described here was installed.

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

## Correction after the first pilot

The first recipe incorrectly supervised instrumental audio as an empty English
transcript, including an English-language target after the start token. That can
conflict with Whisper's no-speech classification. The corrected recipe supervises
the no-speech token there and EOS under the supplied transcription prompt, while
masking the contradictory intermediate targets. The acoustic cache is reused but
its old labels are ignored. Old checkpoints cannot resume under the new recipe.

Eleven pipeline tests passed. The real large-v3 tokenizer verified the target IDs,
and a two-update GPU check containing both positive and instrumental examples
completed with finite losses and gradients. This verifies execution, not accuracy.
The corrected 600-step run started from the original base, not the first adapter or smoke
checkpoint, and saves under `runs/large-v3-lora-nospeech-20260925`.

These reserved songs have now informed a recipe correction and should be regarded
as development data. Independent complete-song and speech checks were therefore
required before considering promotion; those completed results are reported below.

## Corrected pilot: completed development evaluation

The corrected run completed 600 steps. Checkpoint 200 was selected at 29.81% WER
on the 42 selection clips. The final checkpoint degraded to 166.79% WER, despite
lower training loss, and was rejected. The following checks use checkpoint 200.

The original 504-example baseline was reused after checking the model revision,
manifest, generation limit, example order, references, and selection membership.
The corrected candidate was newly evaluated on every example.

| Measure, 128-token cap | Original model | Corrected candidate |
|---|---:|---:|
| Word error rate, all 504 examples | 38.31% | 31.56% |
| Word error rate, annotated singing in mixtures | 42.41% | 36.33% |
| Word error rate, original vocal stems | 19.42% | 11.61% |
| Deleted words, both singing views | 253 | 133 |
| Instrumental false-positive excerpts (of 50) | 30 | 17 |
| Invented words on instrumental excerpts | 265 | 272 |
| Generations hitting the limit | 15 | 9 |

The union of 23 capped cases was rerun for both models with a 440-token limit.
Combined full-set WER was 81.84% versus 51.17%, with 794 versus 764 invented
instrumental words. The original still reached the longer cap 15 times and the
corrected model twice. Repetition remains a limitation, but the improvement is not
solely an artifact of stopping generation early. These remain raw short-excerpt
metrics, not application-level or independent-test accuracy claims.

On the local regression song, the corrected application-engine candidate retained
four occurrences of the user-confirmed chorus ending, with no return of the
previously reported incorrect wording. The opening wording remains essentially
unchanged and unverified. Segmentation differs (15 original-model lines versus
9 candidate lines), which is not itself evidence of word omissions.

## Independent acceptance results: do not promote

All checks in the fixed [acceptance protocol](ACCEPTANCE.md) completed successfully.
This means the tests executed successfully; the candidate did not pass the quality
criteria for replacing the lyric model. No inference settings or model selection
were changed in response to these fresh results.

Five complete, previously unused songs were processed by the application's existing
vocal-isolation and transcription workflow. Scoring covered 160 nonoverlapping,
trusted intervals, including 47 instrumental intervals, with 1,490 reference words.
The speech check used 24 recordings from eight speakers and 586 reference words.

| Independent measure | Original model | Corrected candidate |
|---|---:|---:|
| Song WER, trusted intervals joined per song | 18.12% | 18.05% |
| Word errors in that joined comparison | 270 | 269 |
| Word deletions in that joined comparison | 34 | 35 |
| WER including lyric-interval timing boundaries | 20.54% | 21.95% |
| Word deletions within timed intervals | 52 | 63 |
| Instrumental intervals with invented words (of 47) | 11 | 4 |
| Invented words in those instrumental intervals | 14 | 4 |
| Speech WER | 3.07% | 1.02% |

Joining the intervals avoids counting a correctly recognized word as wrong solely
because it falls across an adjacent line boundary. That comparison improves by
only one error across 1,490 words, and omissions do not improve. The stricter timed
comparison gets worse. Together these results do not demonstrate a meaningful
lyric-recognition upgrade, despite better speech and instrumental suppression.

| Fresh song: timed-interval WER | Original | Candidate |
|---|---:|---:|
| Angels In Amplifiers — I'm Alright | 23.68% | 26.97% |
| M.E.R.C. Music — Knockout | 13.93% | 14.64% |
| Girls Under Glass — We Feel Alright | 19.01% | 30.28% |
| Juliet's Rescue — Heartbeats | 24.56% | 21.93% |
| Mu — Too Bright | 31.69% | 31.69% |

The user's regression song still retains all four confirmed chorus endings, but
the uncertain opening is not resolved. No complete verified reference exists for
that local song, so it is not included in the numerical benchmark.

**Fine-tuning decision:** retain the original bundled model. No experimental model
or extra app copy was installed in Release. All trained weights
remain research artifacts. Fifteen tool/metric tests passed, and all real GPU
comparisons completed. The measured regression is a model-quality result, not a
failure to run the pipeline. Further model changes need a new evaluation before
any promotion; these results do not justify automatic replacement.

## Local evidence

All paths below are relative to `artifacts/lyrics-training/` and are excluded from Git:

- `runs/large-v3-lora-pilot-20260925/`: first training, baseline, checkpoints, metrics.
- `evaluations/large-v3-pilot-full-validation/`: complete paired 128-token comparison.
- `evaluations/large-v3-pilot-cap-check/`: longer-cap check and combined predictions.
- `evaluations/cant-catch-me-pipeline/`: complete-song original/adapted transcripts.
- `exports/pilot-best-step500/`: research-only converted model and provenance.
- `runs/no-speech-label-check/`: real positive/negative training smoke test.
- `runs/large-v3-lora-nospeech-20260925/`: corrected training run.
- `evaluations/nospeech-full-validation/`: corrected 504-example comparison.
- `evaluations/nospeech-cap-check/`: corrected longer-output comparison.
- `evaluations/nospeech-acceptance/`: fresh complete-song and speech checks.
- `acceptance-data/`: fixed fresh-test selection, public audio, and references.

Dataset sources: [MUSDB18-HQ](https://zenodo.org/records/3338373) and
[lyrics extension](https://zenodo.org/records/3989267). No audio, reference lyrics,
or trained weights are published in this branch. This remains an
educational/noncommercial research pilot. The no-speech training target follows
the [Whisper paper](https://cdn.openai.com/papers/whisper.pdf); the extra conditional
empty-transcript target is a local adaptation for the application's prompting.
