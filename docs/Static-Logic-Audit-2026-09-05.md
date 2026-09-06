# WaveLab execution-flow and state audit

## Resolution — implemented and verified

The subsequent fix pass addressed all 11 current-path findings and both latent API defects. The original audit below is retained as a historical diagnostic record; its line numbers describe the audited version.

| Finding | Resolution |
| --- | --- |
| F01 | Marker/region changes stay in the unsaved document until Save. Save and Save As persist the exact metadata snapshot captured with the written audio. Sidecars carry file-version information so a stale sidecar is rejected after audio replacement. |
| F02 | Audio history retains reversible anchor state, restoring collapsed marker positions and removed regions while preserving their object identities and names. |
| F03 | Live switches queue resets; the processing thread performs them before processing the next buffer. |
| F04 | CD-dialog operations and restoration-workbench operations register lifecycle leases. Shutdown cancels and joins them before disposing engines. CD completion dialogs are suppressed during shutdown. |
| F05 | Exact per-step timeline notifications remap anchors during history jumps, while waveform/history refresh remains batched. |
| F06 | Live playback drains effect latency and bounded tails before EOF; the presented playback position accounts for processing latency. |
| F07 | Head snapping uses the bounded trim operation. Montage validation rejects negative or overflowing timeline positions. |
| F08 | Loop changes are published to the active provider and preserve the current playback phase. |
| F09 | Apply Chain and sample-rate conversion recheck cancellation before committing/publishing results. |
| F10 | Clipboard size is validated before allocation; the old clipboard is replaced only after successful copying. |
| F11 | Deferred monitor failures check their session generation before changing current monitoring status. |
| L01 | A/B comparison stores the departing chain in its own slot and prepares the incoming chain before publishing the transition. |
| L02 | Removed the unused no-op `Processing.Crossfade` API. The working montage crossfade implementation remains available. |

Validation: **2,478 tests passed, zero failures, zero skips**, including **18 new regression cases** in [LogicAuditRegressionTests.cs](C:/Users/dhucu/source/repos/WaveLab/tests/WaveLab.Tests/LogicAuditRegressionTests.cs) and [LogicAuditWorkflowTests.cs](C:/Users/dhucu/source/repos/WaveLab/tests/WaveLab.Tests/LogicAuditWorkflowTests.cs). The final test build reported no warnings. Physical audio-device behavior and third-party plugin behavior were not exercised.

## Original audit

Audit date: 5 September 2026. Target: the current working-tree application, treated as a cohesive system. No git diff, commit history, or earlier audit report was used as the basis for the findings. Production source was not edited.

The repository contains 194 authored C# files and approximately 71,803 lines, including comments. This review traces the application lifecycle and the contracts between its controllers, documents, playback, recording, rendering, persistence, and modeless windows. It is not a line-by-line certification of every numerical kernel or a proof of third-party plugin/driver behavior. Generated build output and design mockups are outside the logic-audit scope.

There are **11 current-path findings and 2 latent API defects** below. Six current-path findings and the A/B API defect were reproduced by an isolated executable referencing the current application project. Other findings are supported by explicit control-flow/interleaving traces. The project compiled successfully as part of those checks. The full test suite, physical recording devices, and third-party plugins were not exercised.

P1 means a high-priority persistence, lifecycle, or shared-state defect. P2 means a functional defect that should be corrected. P3 identifies currently unused API logic.

## Execution sequence

| Phase | Observed sequence | Assessment |
| --- | --- | --- |
| Entry | `Program.Main` handles scanner requests before constructing WPF; normal launch initializes `App`, installs exception handlers, starts Media Foundation, and creates the main window. | Scanner and interactive entry paths are separated. No concrete ordering defect identified here. |
| Startup | The main window creates the view model/engines and timers; `Loaded` runs recovery, then command-line files or the remembered session. Recovery reads audio, restores metadata, builds peaks, opens tabs, secures replacement recovery snapshots, then retires old copies. | The recovery replacement ordering is conservative. Autosave waits for startup completion. |
| Editing | Commands capture the document/range; longer tools capture channel references and version, process private data, check identity/version, stop dependent playback, and commit an edit. `Changed` triggers anchor remapping, peak rebuilding, and marker persistence. | The audio snapshot pattern is useful, but metadata is neither fully undoable nor transactionally coupled to the audio on disk: F01, F02, F05. Some commit paths omit cancellation checks: F09. |
| Playback | Stop the old stream, drain pending cleanup, snapshot channels into a provider, configure the master, publish playback ownership, start output. Audio callbacks read the provider and run the enabled chain. | Live reset publication races with processing (F03), source exhaustion skips draining effects (F06), and loop state is copied only at stream creation (F08). |
| Recording | Configure the input, publish a session before capture starts, reject stale callbacks using session/data-state identities, accumulate blocks, stop/deactivate/detach, preserve a pending snapshot, flatten it, then publish a document. | Session matching, build claims, and retained snapshots address several important failure paths. The software-monitor failure continuation has a separate stale-owner gap: F11. Hardware stop/disposal completion was not tested. |
| Save/export | Ordinary save snapshots audio and metadata, writes a staged file, flushes marker writes, and marks clean only if versions still match. CD export stages a package and publishes files with rollback on caught failures. | File-level staging is useful, but sidecars can already describe unsaved audio (F01). CD rollback requires its worker to remain alive; shutdown does not join it (F04). |
| Shutdown | Main-window close checks hosted blocking work, startup, recording, and dirty documents. `OnCleanExitAsync` cancels/joins known operations, saves session settings, clears eligible recovery data, and disposes engines. | A modeless CD export is absent from those operation registries (F04). A local close veto does not supply application-level ownership. |

## Current-path findings

### F01 — P1: Unsaved audio edits persist marker positions against the wrong audio

Locations: [DocumentViewModel.OnDocChanged](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:570), [NotifyMarkersChanged](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:205), [QueueMarkerSave](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:219), [CloseTabAsync](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MainViewModel.cs:1659).

A length-changing edit remaps markers/regions and immediately queues a sidecar write to the original file's path. The audio file itself is still unchanged. Closing without saving flushes those sidecar writes and removes the recovery entry.

**Reproduction:** Save 1,000 frames with a marker at 500; delete the first 100 frames in memory; let marker persistence finish; reopen the original WAV without saving the audio. The WAV still has 1,000 frames, but its marker is now at 400. The isolated check reproduced exactly this result.

**Impact:** Discarding an audio edit, or crashing after the sidecar write, leaves persistent marker/CD-region positions inconsistent with the saved audio. This is a cross-file transaction failure, even if automatic persistence of independent marker edits is intentional.

**Correction:** Keep anchors derived from unsaved audio in document/recovery state. Commit the audio and its corresponding metadata together, or bind sidecar snapshots to the audio revision they describe and restore the saved anchor snapshot when audio changes are discarded.

### F02 — P1: Undo restores deleted samples but cannot restore deleted metadata

Locations: [anchor remapping](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:549), [region removal](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:587), [UndoCore](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/AudioDocument.cs:515), [Edit record](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/AudioDocument.cs:864).

Deletion collapses markers inside the deleted range to its start and removes regions that become empty. Undo stores audio and disc-signal state, but not the previous markers/regions. The inverse insertion then runs the same lossy mapping again.

**Reproduction:** With a marker at 150 and a region `[120,180)`, delete `[100,200)` and undo. The audio returns to its original length; the marker remains at 100 and the region remains absent. Reproduced.

**Impact:** An apparently reversible edit permanently changes document metadata. Redo/undo and the history panel cannot recover it.

**Correction:** Include before/after anchor state in the edit transaction, or retain sufficient per-edit metadata to invert deletions exactly.

### F03 — P1: An effect is enabled before its reset finishes

Locations: [RackEnabled](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:80), [MidSideMode](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:103), [SetEffectEnabled](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:354), [Read](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:472).

The UI thread publishes the new enabled/mid-side state under `_chainLock`, releases the lock, and then calls `ResetState`. The audio thread can acquire the lock and call `Process` while that reset is still modifying the same effect's delay buffers, filter state, envelopes, and indices. Mid-side changes leave effects active throughout.

**Interleaving:** Publish enabled → release lock → begin reset → audio callback acquires lock → process the resetting effect. An instrumented effect with a blocked reset reproduced overlapping `Process` and `ResetState` calls.

**Impact:** Nondeterministic audio discontinuities and internally inconsistent DSP state. The `Process` lock does not protect against mutations deliberately performed outside it.

**Correction:** Apply pending resets on the audio thread at a buffer boundary, or prepare a separate reset processing state and publish it atomically. Do not expose a live effect before its reset is complete.

### F04 — P1: Main-window shutdown can abandon an active CD export

Locations: [modeless dialog creation](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/CdTransferDialog.xaml.cs:194), [export](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/CdTransferDialog.xaml.cs:1291), [dialog Closed handler](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/CdTransferDialog.xaml.cs:255), [main close](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/MainWindow.xaml.cs:176), [shutdown joins](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MainViewModel.cs:3012).

CD export owns only dialog-local `_busy` and `_operation` state. It is not registered with the main progress host or the task sets joined during shutdown. Closing the main window therefore passes its operation checks while the export is writing.

The dialog's `Closing` veto cannot protect this path: WPF does not raise an owned modeless window's `Closing` event when its owner closes. Its `Closed` handler requests cancellation, but nothing awaits completion before application exit. See [Microsoft's Window.Closing documentation](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.closing?view=windowsdesktop-10.0).

**Impact:** Process termination can interrupt staging or multi-file publication before the export's catch/finally rollback completes. A package can be abandoned or partially published. This is a static lifecycle finding, not an observed forced-termination test.

**Correction:** Register modeless work in the application lifecycle; cancel and await every owned operation before disposing shared processing resources and approving the final main-window close.

### F05 — P2: A history jump changes anchors differently from the equivalent individual undos

Locations: [JumpToHistoryPosition.Compose](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/AudioDocument.cs:446), [composite Changed event](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/AudioDocument.cs:472), [anchor mapping](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/DocumentViewModel.cs:549).

The jump combines multiple splices into one enclosing `(start, removed, inserted)` event. That triple describes a changed audio envelope, but cannot describe the piecewise movement of anchors between separate edits. Consumers nevertheless use it as an exact coordinate transformation.

**Reproduction:** Start with 1,000 frames and a marker at 500. Insert 100 frames at 100, then another 100 at 800. Two individual undos return the marker to 500. `JumpToHistoryPosition(0)` restores the same audio but leaves the marker at 600. Reproduced.

**Correction:** Compose exact position maps or replay per-edit anchor transformations, while continuing to batch expensive waveform notifications. This is separate from F02: this example loses no anchor to a deletion.

### F06 — P2: Playback ends before latency-bearing effects release the final samples

Locations: [MasterSection.Read source exhaustion](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:464), [provider end handling](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/PlaybackEngine.cs:604), [limiter latency](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/Effects/LimiterEffect.cs:26), [offline latency drain](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:651).

When the source returns zero, `MasterSection.Read` returns immediately. It does not feed zeros through the chain to release latency buffers or effect tails. Offline rendering does account for latency, so audition and rendered output diverge at the end.

**Reproduction:** Feed 1,000 mono frames containing only a final sample of 0.25 through the built-in limiter. The first read returns 1,000 silent frames; the next returns zero. The input impulse is never output. Reproduced without audio hardware.

**Impact:** The limiter alone drops up to its 5 ms lookahead from playback's end; longer-latency effects can lose more. Reverb/delay tails also stop abruptly.

**Correction:** Distinguish source EOF from chain EOF. Drain the chain's latency and an explicit tail policy before returning zero, with transport timing consistent with what is audible.

### F07 — P2: Montage head snapping can place a clip before sample zero

Locations: [EndDrag head snap](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/Controls/MontageLaneView.cs:509), [TrimClip bounds](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MontageViewModel.cs:249), [validation](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/Montage/MontageDocument.cs:203), [render indexing](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/Montage/MontageRenderer.cs:127).

Dragging clamps the requested timeline position to zero, but release-time zero-crossing snapping adds an unrestricted negative source delta to `TimelineStart`. Validation does not reject a negative start; rendering checks only the upper timeline bound before indexing the output array.

**Reproduction:** A clip starts at timeline 0, reads source sample 100, and its selected crossing is at 90. The actual `EndDrag` path changes its timeline start to −10. `MontageRenderer.Render` then throws `IndexOutOfRangeException`. Reproduced through the control's release handler.

**Correction:** Constrain the snapped source position to a range that preserves nonnegative timeline coordinates and a nonempty clip. Validate those invariants before rendering as well.

### F08 — P2: Toggling Loop does not change the active stream

Locations: [IsLooping setter](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MainViewModel.cs:574), [engine Loop property](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/PlaybackEngine.cs:32), [provider construction](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/PlaybackEngine.cs:199), [provider loop branch](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/PlaybackEngine.cs:607).

The view model updates `Engine.Loop`, but that is an independent auto-property. The active provider received a copy when `Play` constructed it and continues reading its own `Loop` value.

**Trigger:** Start playback with looping off, then enable it; playback still ends. Start with looping on, then disable it; the active stream keeps looping. The control and engine setting report a state the stream is not using.

**Correction:** Publish the setting to the active provider with appropriate synchronization, or deliberately restart/reconfigure playback when the setting changes.

### F09 — P2: Apply Chain can commit after cancellation was requested

Locations: [ApplyChain worker and commit](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MainViewModel.cs:2621), [sample-rate conversion completion](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/MainWindow.xaml.cs:2798). Compare the correctly guarded [RunRangeTool completion](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Views/MainWindow.xaml.cs:969).

After awaiting the worker, `ApplyChain` checks document membership/version but never rechecks the cancellation token before replacing audio. A cancellation after the worker's final check, while its UI continuation is queued, does not turn a successfully completed task into a cancelled task. The sample-rate conversion path similarly adds its generated document without a final token check.

**Impact:** Clicking Cancel in that interval can still commit the effect chain or create the converted tab. This is a boundary-ordering finding established statically.

**Correction:** Check cancellation immediately after the await and immediately before the irreversible document publication/commit, following the existing range-tool pattern.

### F10 — P2: The clipboard size guard runs after the large allocation

Location: [CaptureSelectionAsync](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/ViewModels/MainViewModel.cs:1843).

The method allocates and copies every selected channel into `_clipboard` before calculating whether the selection exceeds `MaximumClipboardBytes`. It then throws the new clipboard away if it exceeds 512 MiB.

**Impact:** A selection that should be rejected cheaply first incurs the entire allocation/copy cost and can fail from memory pressure. A successfully copied but oversized selection also destroys the previous clipboard contents before reporting rejection.

**Correction:** Calculate the required byte count from captured channel count and selection length before allocation; validate it before starting the worker; publish the new clipboard only after successful completion.

### F11 — P2: A deferred monitor failure can overwrite a newer session's status

Locations: [SoftwareInputMonitor failure dispatch](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/SoftwareInputMonitor.cs:98), [new-session publication](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/SoftwareInputMonitor.cs:46).

The capture thread removes the failed monitor session using compare/exchange, then queues a worker. That worker later sets `_enabled = false` and `_lastError` without checking whether the user has enabled/configured a new monitor in the meantime.

**Interleaving:** Old session fails and is detached → user restarts monitoring and a new session is published → old failure worker acquires `_sync` → new session remains active, but monitoring is marked disabled with the old error.

**Impact:** Active-session state, user preference, and error status disagree; later configuration consults the stale disabled flag. This is a static interleaving finding; physical monitor restart was not tested.

**Correction:** Carry a generation/ownership token into the continuation and mutate shared status only if that failure still owns the monitor state. Cleanup of the retired session can proceed independently.

## Latent logic and dead-path observations

### L01 — P3: Repeated A/B comparison overwrites the wrong snapshot slot

Location: [MasterSection.ToggleCompare](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/MasterSection.cs:182).

With A captured at −6 dB and B at +6 dB, repeated toggling produced:

```text
B=+6, A=-6, B=+6, A=+6, B=-6, A=+6
```

The active chain is stored into the snapshot slot being restored, rather than consistently storing the departing side into its own slot. This corrupts the association between the A/B labels and their settings. Reproduced with the built-in trim effect.

No production callers of the capture/toggle API were found, so this is an unused API defect, not a demonstrated user-facing comparison failure. Correct the ownership/state-machine convention before wiring it into the interface.

### L02 — P3: Processing.Crossfade is a mathematical no-op

Location: [Processing.Crossfade](C:/Users/dhucu/source/repos/WaveLab/src/WaveLab/Audio/Processing.cs:188).

The multiplier is `sqrt(cos²(tπ/2) + sin²(tπ/2))`, which is one. No two signal regions are mixed. The method still copies data, records an undo step, and raises change notifications despite leaving the samples effectively unchanged.

No production caller was found. The separate montage crossfade implementation is not implicated by this observation. Remove the unused method or implement an actual overlap/mix operation before exposing it.

## Verification record and boundaries

An isolated .NET 10 executable referenced and built the current application project. It used small in-memory documents, the built-in limiter/trim, a finite sample provider, an instrumented effect, and a temporary WAV/sidecar. It did not start a real audio stream. The montage check invoked the actual control release handler without opening a window.

Relevant observed output:

```text
Undo deletion: length=1000, marker=100 (expected 150), regions=0 (expected 1)
History jump marker=600; repeated undo marker=500
A/B gains: B=6 A=-6 B=6 A=6 B=-6 A=6
Playback EOF: first read=1000, next=0, output maximum=0 (last input sample .25 lost)
Rack enable reset overlapped Process: True
Actual head-snap release: timeline=-10, source=90
Head-snap render: IndexOutOfRangeException
Reopen without audio save: frames=1000, marker=400 (expected 500)
```

The checks also examined clearing unsaved embedded markers and metadata loss through export, but these were not promoted to defects because their intended persistence/export semantics need clearer requirements. Merely observing behavior is not enough to label it incorrect.

The strongest corrective themes are: make audio and anchored metadata one edit/persistence transaction; give every asynchronous operation an application-level owner; and publish live DSP state only when it is ready for the audio thread. The remaining DSP algorithms, codec edge cases, and native plugin ABI deserve separate focused verification before claiming exhaustive correctness of the entire repository.
