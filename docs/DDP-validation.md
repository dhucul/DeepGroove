DDP export interoperability

The writer emits DDP 2.00 for stereo CD-DA. `DDPID` is 128 bytes; `DDPMS` consists of 128-byte stream descriptors; `PQDESCR` consists of 64-byte PQ records. The image is little-endian interleaved 16-bit PCM and includes the initial 150-sector pause, which the map declares as already present. Later track pregaps remain embedded in the image and have separate index 00/01 records.

CD-TEXT uses one Latin-1 language block, with a global sequence number, terminated strings, three size-information packs, and complemented CRC-16. Text that cannot be represented, control characters, or content exceeding the block's 256-pack limit is rejected before output audio is written. `CHECKSUM.MD5` covers every descriptor, the optional CD-TEXT file, and the audio. The existing image-only checksum is retained as well.

The fixed CD-TEXT bytes in `DdpInteropTests` were generated independently with [Andreas Ruge's DDP tools](http://ddp.andreasruge.de/), version 1.1, from an author-created cue sheet using Disc/Artist and Track One, Track Two, Track Three. No external implementation is bundled into the application. The Windows tool archive used for validation had SHA-256 `34190170A407F7523742F444F7BFE9470BD19C3CED65E894D79C0EE15CB04C4B`.

To retain the regression test's package for an external reader, set `WAVELAB_DDP_INTEROP_OUTPUT` to a disposable output directory and run:

```powershell
dotnet test tests/WaveLab.Tests/WaveLab.Tests.csproj --filter FullyQualifiedName~DdpInteropTests.ReferenceProgrammeHasInteroperableRecordsAndCdText
```

The test writes the package under `ddp` and its expected programme audio as `expected.wav`. With the independent tool available:

```powershell
ddpinfo --verify <output-directory>/ddp
ddpinfo --wave <output-directory>/decoded.wav <output-directory>/ddp
```

Validation on 26 September 2026: the independent reader accepted all descriptors, verified all checksums, read the titles and performers, recovered both ISRCs, and placed the second track's pre-emphasis flag correctly. The decoded cue sheet retained both two-second inter-track gaps. Its extracted programme matched all **3,351,600 PCM bytes** of `expected.wav`: 837,900 stereo frames at 44.1 kHz. The reader correctly excluded the initial pause during WAV extraction.

This validates interchange with that reader and the stored fixture. No pressing-plant submission or physical disc burn was performed.
