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

**Every per-corpus figure below comes from one run of
`DeclipCorpusTests.TheChainBeatsLeavingTheDamageAlone` over all six corpora**, so they are
comparable with each other. Older numbers are not: figures taken while the withdrawn shoulder cap
was in the signal path, or before `DeclipCorpus.StableHash` replaced .NET's randomised string
hashing, differ by more than the changes being measured. Where one is still quoted it is marked as
superseded.

## Corpus 1 — a private collection of record transfers (11 recordings, 44 cells)

Soul and R&B transferred from records — David's own vinyl transfers, WAV, 44.1 kHz stereo float.
Not redistributable; named for the record only.

Measured against the shipped chain on 2026-08-24: **44 of 44 cells beat leaving the damage alone,
mean +6.37 dB, worst +0.81, none below do-nothing**. Corpus 2 reproduced its recorded figures to
the decimal in the same run, which is the check that the harness itself did not move.

### The corpus's membership has changed twice, and figures do not carry across either change

**First change:** the folder once held ten WAV files that were a different population entirely —
recorded and streamed off the internet, badly — and they were deleted; the corpus was then AIFF-only,
and every figure taken between the two changes is from those AIFFs (9 recordings, 36 cells, mean
+6.40 dB, worst +0.23, with one track carrying genuine clipping on channel 1).

**Second change (2026-08-24):** the AIFF transfers were replaced by newer WAV transfers of the same
records, and the AIFFs are gone. The harness now accepts the WAVs as corpus 1, by David's decision —
the aiff-only guard was aimed at the deleted internet WAVs, and keeping it once the folder held only
genuine transfers left every corpus-1 harness empty (the wow harness, which stands entirely on this
corpus, ran with zero cells). The old guard's lesson still stands as a rule about *populations*, not
extensions: nothing recorded off the internet goes into this corpus.

Corpus-1 figures from before either change **do not reproduce against the current audio and must
not be compared with fresh runs** — an earlier split table already failed to reproduce on `raw`, the
SNR of the damaged file before any repair runs, which no change to the repair code can move. This
note is the second time that has been true; prefer a fresh measurement over any quoted corpus-1
number whose date is unclear.

## Corpus 2 — `C:\Windows\Media` (38 files, 152 cells)

Ships with Windows, so it is available on any machine this is developed on. A different
production origin entirely, including 22.05 kHz material and peaks from -10 to -26 dBFS. The
chain beats leaving the damage alone in **152 of 152 cells, mean +13.21 dB, worst +1.57**.
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

It earned that immediately. **The chain does well on speech — 64 of 64 cells, mean +10.88 dB — but
the thinnest surviving margin in the whole 532-cell set is here, +0.10 dB**, where the worst over
the first three corpora was +1.57. And the shoulder cap, which gains on corpora 1 and 2 and leaves 3
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

## Corpus 5 — Musopen classical (32 works, 128 cells)

Public Domain Mark 1.0, from `MusopenCollectionAsFlac` on the Internet Archive: recordings Musopen
commissioned and released outright, so the licence is unambiguous. Solo piano, string quartets and
orchestral, one movement per work.

**Chosen to be discriminating rather than merely new.** Corpus 4 showed the sparse reconstruction
cap losing on spoken word, and the question was whether that was about speech or about sparsity.
Classical is sparse, tonal and wide in dynamic range but is not speech, so it separates the two.
It answered clearly: **the cap loses 33.4 dB here**, six times what it lost on speech, and the cap
was withdrawn as a result.

The chain itself does well — **128 of 128 cells, mean +11.24 dB, worst +5.47** — and this is the
only corpus where the already-clipped screen has fired: **two Schubert piano sonatas arrived with
19 and 14 clipping events before any damage** and are excluded, since a repair cannot be scored
against a reference that is itself clipped.

```
https://archive.org/download/MusopenCollectionAsFlac/<work>/<track>.mp3
```

Works used:

  `Bach_GoldbergVariations`, `Beethoven_CoriolanOverture`, `Beethoven_EgmontOvertureOp.84`
  `Beethoven_StringQuartetNo.6inBFlatMajorOp.18`, `Beethoven_SymphonyNo.3Eroica`, `Borodin_InTheSteppesOfCentralAsia`
  `Borodin_StringQuartetNo.1inAMajor`, `Borodin_StringQuartetNo.2inDMajor`, `Brahms_SymphonyNo.1inCMinor`
  `Brahms_SymphonyNo.2inDMajor`, `Brahms_SymphonyNo.3inFMajor`, `Brahms_SymphonyNo.4inEMinor`
  `Dvorak_StringQuartetNo.10inEFlatOp.51`, `Dvorak_StringQuartetNo.12inFMajorOp.96`, `Greig_PeerGynt`
  `Haydn_StringQuartetInDMajorOp.64`, `Mendelssohn_Hebrides`, `Mendelssohn_ItalianSymphony`
  `Mendelssohn_ScottishSymphony`, `Mendelssohn_StringQuartetNo.6inFMinorOp.80`, `Mozart_MagicFluteOverture`
  `Mozart_MarriageOfFigaro`, `Mozart_StringQuartetNo.15inDMinorK421`, `Mozart_StringQuartetNo.19inCMajorK465`
  `Mozart_SymphonyNo.40inGMinor`, `Schubert_SonataInAMajorD.664`, `Schubert_SonataInAMinorD.784`
  `Schubert_SonataInAMinorD.845`, `Schubert_SonataInAMinorD.959`, `Schubert_SonataInCMinorD.958`
  `Schubert_SonataInDMajorD.850`, `Schubert_SonataInEFlatMajorD.568`, `Suk_Meditation`
  `Tchaikovsky_SymphonyPathetique`

## Corpus 6 — Creative Commons netlabel music (32 tracks, 17 usable, 68 cells)

One track per release from the Internet Archive's `netlabels` collection, drawn across four genre
buckets — rock/punk/metal, hip hop, techno/electro/house, drum & bass/breakbeat/dubstep — so the
corpus spans many labels and mastering chains rather than one. All 44.1 kHz, 203–517 s. Licences
vary by item and are stated on each: 29 of the 32 carry a Creative Commons licence, most often
BY-NC-ND 3.0, and three (`OnorezdiLP014`, `LostChildren051`,
`freemusiccharts2007top10januar`) state none. Nothing is vendored; only identifiers are named, as
with corpora 3 to 5.

### It was chosen for a measured property, not a genre

Every corpus so far is either sparse and tonal, speech, or an old transfer. **None of them is
loud.** Measured as crest factor — peak over RMS, which is what mastering compression takes away —
the six populations separate cleanly:

| corpus | crest min / median / max | clipped at 0.70 of peak | mean plateau |
| --- | --- | --- | --- |
| 1 record transfers | 15.6 / 16.3 / 18.8 dB | 0.03% | 4.5 |
| 2 Windows Media | 11.9 / 15.2 / 25.6 dB | 0.82% | 7.6 |
| 3 shellac 78s | 24.4 / 30.3 / 37.5 dB | 0.35% | 3.2 |
| 4 spoken word | 16.8 / 21.1 / 26.3 dB | 0.04% | 5.3 |
| 5 classical | 17.0 / 21.1 / 24.8 dB | 0.01% | 6.4 |
| **6 netlabel music** | **8.9 / 12.9 / 23.3 dB** | **1.46%** | 6.6 |

Corpus 6 is eight decibels below the shellac median and two and a half below the next lowest, and
clipping it at the same relative level destroys nearly twice as many samples as anything else. That
is the point: the open question the declip work left was whether A-SPADE's behaviour at light damage
is about sparsity, and the way to answer it is a population that is measurably *not* sparse.

### Half of it arrives already clipped, which is a finding rather than an inconvenience

**15 of the 32 files are excluded by the already-clipped screen** — one of them with 3138 clipping
events before any damage is applied. Over the first five corpora that screen had fired twice in 464
cells, both of them Schubert piano sonatas. Loud mastering is not a stylistic description here; it
is half a corpus with no clean reference left to score a repair against. It is also the strongest
argument yet for the screen: without it those fifteen would have quietly contributed nonsense to
every average in this document.

### It broke the standing claim on the declip chain

The 17 usable files give 68 cells: **mean +4.96 dB, worst −13.87, and four cells below leaving the
damage alone**. Across the previous five corpora the chain had beaten do-nothing in all 464.

All four losses are at **0.70, the mildest severity**, with **0.01% to 1.04% of samples clipped and
mean plateaus of 6.8 to 8.2 samples** — A-SPADE asked to rebuild programme that was very nearly
intact. Forcing the other method shows it is a routing failure rather than a repair failure: the
arch wins three of the four outright.

| cell | clipped | plateau | chain | arch |
| --- | --- | --- | --- | --- |
| `SOSLP008` @0.70 | 0.12% | 8.2 | −13.87 | −10.66 |
| `mia049` @0.70 | 0.01% | 8.0 | −2.52 | +0.11 |
| `Bostaurus.Demo` @0.70 | 1.04% | 7.0 | −1.97 | +1.58 |
| `DWK217` @0.70 | 0.08% | 6.8 | −1.10 | +0.81 |

**Two rules would divert exactly these cells, and both are measured dead ends.** A damage floor was
shipped twice and withdrawn — barely-clipped real programme has long plateaus, which is where
A-SPADE wins, and a guard at 0.02% of samples costs 19.8 dB. A short-plateau exception was fitted,
validated three ways, shipped, and destroyed by a second corpus at a cost of 668.7 dB. So the four
cells are recorded as a characterised defect rather than bought at that price, and
`DeclipCorpusTests.TheChainBeatsLeavingTheDamageAlone` was weakened to what is still true: where
there is real damage the repair never loses, every population gains by a wide margin, and losses
stay rare and stay at the mildest severity.

Away from those four the corpus is unremarkable — 64 of 68 cells gain, up to +13.67 dB, and the
chooser's regret is 11.9 dB, in line with corpora 1, 4 and 5.

### Every other tool came through it clean

| tool | corpus 6 | worst cell |
| --- | --- | --- |
| click repair | 40 cells, +12.82 dB, 96% of planted clicks found | +2.33 |
| crackle repair | 40 cells, +16.44 dB | +4.15 |
| spectral heal | 30 cells, +12.50 dB | +1.19 |

### It is the worst false-positive material the click detector has met

Corpus 6 is digital-born, so every click reported in it is false. It reads a **median of 1.45 events
a second and a maximum of 11.8**, above Windows Media's 10.7, which had been the worst in the set.
It also shows the trend-relative recovery gate costing more than was recorded: that change takes
this corpus from **0.35 to 1.45 a second median and 3.1 to 11.8 worst**, and the record transfers
from 1.21 to 2.56. The cost of that gate had been measured as falling on speech; dense percussion is
click-shaped too, and none of the first five corpora contained much of it.

```
https://archive.org/download/<identifier>/<file>
```

 1. `OnorezdiLP014` / `EXILE - SLUM VILLAGE (PROD THEDEEPR EDIT) - TIME HAS COME.mp3`
 2. `ca200_cjazz` / `108_Broken_Quartet__Popsong.mp3`
 3. `TFR100-VA-TornFleshRecordsPresentsVestigialSickness` / `08-Syphilic-Wombheadft.AngelOchoa.mp3`
 4. `afm004_allmyfaults_neonoir` / `06thecourseoftrueloveneverdid_vbr.mp3`
 5. `siro463SisterSoleil-HauntedEp` / `03-SisterSoleil-EyesmewarkRemix.mp3`
 6. `Lethargie.LP` / `Lethargie-st_lp-03-Same_Shit.mp3`
 7. `Bostaurus.Demo` / `Bostaurus-Demo2006-02-The_Sky_Full_Of_Shadows.mp3`
 8. `LostChildren051` / `03_-_Anoice_-_Glitch.mp3`
 9. `DWK123` / `ProleteR_-_08_-_The_Misfit_Song.mp3`
10. `DWK031` / `Aydio_-_02_-_Deltitnu.mp3`
11. `dystopiaq029` / `216-3DGTimesreChangin.mp3`
12. `DWK149` / `Boogie_Belgique_-_08_-_The_Getaway.mp3`
13. `mia049` / `mia49a_aphilas_-_lifelong_fiction.mp3`
14. `DWK217` / `Boogie_Belgique_-_03_-_Week-End.mp3`
15. `DWK127` / `Kova_-_04_-_Jungle_Boogie.mp3`
16. `DWK155` / `Poldoore_-_03_-_Eazy_Livin.mp3`
17. `stqk011` / `STQK011_06_-_Zero_Call_-_Sirius.mp3`
18. `WkBw0034` / `04-Monochromatic-SongForYou-.mp3`
19. `BSOG0008` / `05-DoingTime.mp3`
20. `afm020_noctiflora_0407` / `03-neorama_vbr.mp3`
21. `freemusiccharts2007top10januar` / `hungrylucy-harvest.mp3`
22. `quiz050` / `quiz050-05-submit-closing_in_vbr.mp3`
23. `freemusiccharts.songs2012` / `2012-01-tBird-nicklesAndDimes.mp3`
24. `mtcomp001` / `mtk019-hoffman-assault-systems.mp3`
25. `Hfr011-mizukisLastChance-tacticalAssault` / `AnOrisonOfSonmi451.mp3`
26. `Helaku_IndianIndianWhatDidYouDieFor` / `12 - Helaku - Elevatormusic (On Acid).mp3`
27. `starfrosch-mostwanted` / `Starfrosch-SleepingAlonefeat.SkyTheDog.mp3`
28. `SOSLP008` / `SOSLP008_01_BEBOP_DONT_BELIEVE_THE_HYPE-Fidget_Kaos.mp3`
29. `mz001_Mademoi_Selle` / `04_Himitsu.mp3`
30. `tkep010` / `tkep010-a-cubicle-there_are_days.mp3`
31. `tkep009` / `tkep009-a-d_fender_-_mess.mp3`
32. `PsicotropicodeliaMusicVol.3jan232008` / `2.03_-_THE_MECHANICAL_GOD_-_Grand_Royal__Die_Hard__.mp3`
