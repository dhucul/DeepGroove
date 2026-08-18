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
the same trap for the same reason.

## Corpus 1 — a private collection (19 recordings, 76 cells)

Soul and R&B, 44.1 and 48 kHz, 110-322 s. Not redistributable; named for the record only. One
track carries genuine clipping and is detected as such; the other eighteen report none.

**Treat this as two corpora, not one.** The extension is not a format detail here: the nine AIFFs
are transfers from records, and the ten WAVs came off the internet and are badly recorded. They
measure differently and the difference is not small.

| | files | cells | mean gain | cells below do-nothing | arch wins | mean damage |
|---|---|---|---|---|---|---|
| AIFF — from records  |  9 | 36 | +7.16 dB | 1 (worst -1.15) | 14% |  3.98% |
| WAV — from the internet | 10 | 40 | +4.87 dB | 4 (worst -3.87) | 22% | 14.67% |

The record transfers behave like corpus 3, where A-SPADE wins outright. Four of the five known
bad cells are internet material, which arrives already degraded and is not what the workbench is
for. Any single average over all nineteen under-reports the population the tool actually targets.

## Corpus 2 — `C:\Windows\Media` (38 files, 152 cells)

Ships with Windows, so it is available on any machine this is developed on. A different
production origin entirely, including 22.05 kHz material and peaks from -10 to -26 dBFS.
Files over 200 kB.

## Corpus 3 — Great 78 Project, Internet Archive (21 recordings, 83 cells)

Public-domain shellac transfers, all pre-1923 and so in the US public domain under the Music
Modernization Act. This is the closest thing measured here to what the restoration workbench is
actually for: real transfer chains, surface noise, and 788-6457 detected clicks per side.
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
