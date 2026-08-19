# Declip validation corpora

The declip chain is measured against real recordings, not only signal generators. Every
calibration of `DeclipMethodChooser` that was fitted to synthetic material alone turned out
wrong, so the corpora are listed here to make the measurements reproducible.

Only `demo_track.wav` lives in the repository — it is what `RealAudioDeclipTests` uses, so the
suite has real audio in it everywhere. The rest are external and are named rather than vendored.

## Method

A repair can only be scored against a clean reference, so each recording is clipped
synthetically and the repair measured against the original over the samples clipping destroyed.
Two details are load-bearing. The clipped signal is **rescaled so the plateau sits at the rail**,
because a genuinely clipped recording ran out of numbers rather than being quiet, and without
this `ClippingAnalysisOptions.MinimumPeakLevel` skips the channel. And on 78rpm material the
level is taken from a **click-resistant programme peak** (99.95th percentile), not the absolute
peak: a shellac transfer's loudest sample is a surface click, measured here at 15.6 dB above the
music, so clipping relative to it barely touches the programme. `RecordingLevelAnalyzer` guards
the same trap for the same reason. Finally, **a recording that is already clipped is excluded and
the exclusion reported**: it has no clean reference, so every gain measured against it is measured
against the wrong thing, and a corpus that quietly shrinks is one nobody can reproduce.

**Verify the harness against `Restoration.RepairClipping` before believing its numbers.** A probe
that reproduces the repair inline - to sweep a parameter without re-running the solver each time -
is only worth as much as its agreement with the shipped path. The A-SPADE ceiling sweep was checked
this way at both severities and every setting and agreed to 1e-4 dB, which is the only reason its
negative result is quotable.

## Corpus 1 — a private collection of record transfers (9 recordings, 36 cells)

Soul and R&B transferred from records, AIFF, 44.1 kHz, 110-322 s. Not redistributable; named for
the record only. One track carries genuine clipping and is detected as such; the other eight
report none.

Measured against the shipped chain: **36 of 36 cells beat leaving the damage alone, mean
+6.84 dB, worst +2.31**. Every one of the nine at 0.70 of peak is a cell where the shoulders now
cap the sparse reconstruction, worth 1.4 to 2.8 dB each; before that rule the mean was +6.40 and
the thinnest cell only +0.22. The chooser sends every cell to A-SPADE; the arch would have won 9 of
36 outright, which costs the chooser 14.0 dB of regret against per-cell oracle choice, where
always choosing the arch would cost 62.7.

### The WAV files are gone and are excluded from every figure

This corpus used to hold ten WAV files as well, and they were a different population entirely:
**recorded and streamed off the internet, badly**, rather than transferred from records. They
arrive already degraded, they are not what this workbench is for, and they have been **deleted**.
Every number above is AIFF-only and they must not be folded back into any average.

An earlier split table recorded here (record transfers at +7.16 dB, one cell below do-nothing at
-1.15, arch winning 14% of cells) **does not reproduce** and has been removed. A fresh run
disagrees on `raw` - the SNR of the damaged file before any repair runs, which no change to the
repair code can move - while corpus 2 reproduces to the decimal, so the difference is in the data
rather than the method. Prefer a fresh measurement over any corpus-1 figure quoted from before
this note.

## Corpus 2 — `C:\Windows\Media` (38 files, 152 cells)

Ships with Windows, so it is available on any machine this is developed on. A different
production origin entirely, including 22.05 kHz material and peaks from -10 to -26 dBFS. The
chain beats leaving the damage alone in **152 of 152 cells, mean +13.42 dB, worst +1.58**.
Files over 200 kB.

## Corpus 3 — Great 78 Project, Internet Archive (21 recordings, 84 cells)

Public-domain shellac transfers, all pre-1923 and so in the US public domain under the Music
Modernization Act. This is the closest thing measured here to what the restoration workbench is
actually for: real transfer chains, surface noise, and 788-6457 detected clicks per side. The
chain beats leaving the damage alone in **84 of 84 cells, mean +4.42 dB, worst +1.88**, and the
chooser is right on every one of them.
Fetched as the VBR MP3 derivative (the 24-bit FLAC is ~64 MB a side).

```
https://archive.org/download/<identifier>/<file>.mp3
```

 1. `78_1-aloha-oe-2-wa-like-no-a-like_louise-ferera_gbia0430117b`  (1920-01)
 2. `78_1-aloha-oe-2-wa-like-no-a-like_louise-ferera_gbia3020041b`  (1920-01)
 3. `78_1-don-giovanniserenata-deh-vieni-alla-finestra-open-thy-window-love-2-f_gbia7028557a`  (1909)
 4. `78_1-gavotte-2-tambourin_mischa-elman-percy-b-kahn-gretry-gossec_gbia0288946a`  (1911)
 5. `78_1-hungarian-dance-no-20-d-minor-2-hungarian-dance-no-21-e-minor_efrem-zimb_gbia0520151a`  (1916)
 6. `78_1-le-cygne-the-swan-2-waltz_efrem-zimbalist-eugene-lutsky-saint-sans-chopin_gbia0520150b`  (1915)
 7. `78_1-minuet-in-g-2-gavotte-in-d_efrem-zimbalist-sam-chotzinoff-beethoven-gossec_gbia0520150a`  (1915)
 8. `78_1-moment-musicale-2-tambourin_fritz-kreisler-george-falkentein-1-schubert-_gbia0334974a`  (1919)
 9. `78_1-namluvy-wooing-2-divici-popevek-a-maidens-song_emmy-destinn-ad-wenig-o_gbia0051608a`  (1920)
10. `78_1-the-bee-2-minute-waltz_maud-powell-george-falkenstein-schubert-chopin-op-6_gbia0177789a`  (1911)
11. `78_1-the-next-market-day-2-a-ballynure-ballad_john-mccormack-edwin-schneider-herb_gbia0058662b`  (1920)
12. `78_1-wiegenlied-cradle-song-2-gavotte_hans-kindler-rosario-bourdon-1-schubert_gbia0478715a`  (1921)
13. `78_12th-street-rag_imperial-marimba-band-euday-l-bowman_gbia0083619a`  (1921)
14. `78_12th-street-rag_rega-dance-orchestra-joe-green-euday-l-bowman_gbia0454222b`  (1920)
15. `78_12th-street-rag_rega-dance-orchestra-joe-green_gbia0298056b`  (1920)
16. `78_1863-medley_gbia0377711a`  (1907)
17. `78_1920-song-hits-medley-part-2_the-silver-stars-band-albert-w-ketelbey_gbia3035814b`  (1921)
18. `78_25th-farewell-to-merut-tulloch-gorn-otulloch_gbia3038441a`  (1914)
19. `78_2nd-air-varie_mr-chas-draper-j-mohr_gbia0038941b`  (1912)
20. `78_2nd-regiment-connecticut-march_banner-military-band-d-w-reeves_gbia3013767a`  (1921-08)
21. `78_2nd-regiment-connecticut-march_lieut-francis-sutherland-and-his-7th-regiment-band_gbia0089957b`  (1922)
22. `78_2nd-regiment-connecticut-national-guard-march_gbia0474460a`  (1910)

## Corpus 4 — LibriVox spoken word (16 recordings, 64 cells)

Public-domain readings, fetched as one chapter each. **Speech is the signal class the other three
corpora have none of**: a single source, long silences, formant structure and no percussion at all.
It was added because three corpora make leave-one-corpus-out a three-fold test, and three of the
four refinements measured against this chain passed leave-one-recording-out and failed
leave-one-corpus-out — a fourth population is worth more than further tuning against the first
three.

It earned that immediately. **The chain does well on speech — 64 of 64 cells, mean +10.80 dB — but
the thinnest margin in the whole 336-cell set is here, +0.10 dB**, where the worst over the first
three corpora was +1.57. And the shoulder cap, which gains on corpora 1 and 2 and leaves 3
untouched, **loses 5.18 dB on this one**.

Many readers, rooms and microphones, plus three languages besides English, so the within-corpus
variety is high. Fetched as the MP3 derivative:

```
https://archive.org/download/<identifier>/<file>.mp3
```

 1. `0_sense_and_sensibility_librivox` - Sense and Sensibility, Austen
 2. `101_mexican_dishes_2303_librivox` - 101 Mexican Dishes, Southworth
 3. `10thanniversarycollection_1508_librivox` - LibriVox 10th Anniversary Collection
 4. `11theses_librivox` - Eleven Theses on Feuerbach, Marx
 5. `12thanniversarycollection_1708_librivox` - LibriVox 12th Anniversary Collection
 6. `13thanniversarycollection_1808_librivox` - LibriVox 13th Anniversary Collection
 7. `1601_0903_librivox` - 1601, Twain
 8. `19thanniversary_2408_librivox` - LibriVox 19th Anniversary Collection
 9. `1chronicles_jc_librivox` - Bible (KJV) 1 Chronicles
10. `1corinthians_ylt_2111_librivox` - Bible (YLT) 1 Corinthians
11. `1henryIV_0804_librivox` - King Henry IV Part 1, Shakespeare
12. `20000_mijlen_1003_librivox` - 20.000 Mijlen onder Zee, Verne (Dutch)
13. `21stanniversarycollection_2608_librivox` - LibriVox 21st Anniversary Collection
14. `2br02b_0801_librivox` - 2 B R 0 2 B, Vonnegut
15. `2corinthians_analyticallyexpounded_2402_librivox` - 2 Corinthians, Dickson
16. `2corinthianswnt_1502_librivox` - 2 Corinthians (WNT)
