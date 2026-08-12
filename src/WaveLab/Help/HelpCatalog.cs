namespace WaveLab.Help;

public sealed record HelpSection(string Title, string Body);

public sealed record HelpTopic(
    string Id,
    string Category,
    string Title,
    string Summary,
    string Keywords,
    IReadOnlyList<HelpSection> Sections)
{
    internal string SearchText { get; } = string.Join(' ',
        Category, Title, Summary, Keywords,
        string.Join(' ', Sections.Select(section => $"{section.Title} {section.Body}")));
}

/// <summary>The user-facing source of truth for WaveLab's built-in help.</summary>
public static class HelpCatalog
{
    public const string StartTopicId = "getting-started";
    public const string RecordingTopicId = "recording-levels";
    public const string ShortcutsTopicId = "shortcuts";

    public static IReadOnlyList<HelpTopic> Topics { get; } =
    [
        Topic("getting-started", "START HERE", "Getting started",
            "The shortest path from opening audio to saving a finished file.",
            "overview screen workflow first steps",
            Section("A safe first workflow",
                "Open a file with File > Open, or drag an audio file onto WaveLab. Drag across the waveform to select a range. Use Edit or Process commands for permanent, undoable edits. Use the Master Rack for real-time processing, then choose Render in Place or Render Copy when the result is ready. Save keeps the editable WAV or AIFF document; Export creates a delivery copy in the chosen format."),
            Section("What is on the main window",
                "The top transport controls playback, recording, position, selection times, and zoom. File tabs hold every open document; the amber dot means unsaved changes. The large center area is the overview, time ruler, and waveform editor. Analysis views sit below the waveform. The Master Rack and its meters are on the right. The bottom status bar shows the audio device, sample count, selection length, autosave state, zoom, CPU, and memory."),
            Section("Selection or whole file",
                "Most edit, process, restoration, and Render in Place commands affect the current selection when one exists; otherwise they affect the whole file. Render Copy always renders the whole source into a new tab. Check the SEL IN, SEL OUT, and LENGTH readouts before applying a destructive operation."),
            Section("Undo and source safety",
                "Normal edits and Render in Place are undoable. Render Copy and channel conversions create a new tab and leave the source unchanged. Export also leaves the open document unchanged. Save important recordings before closing WaveLab.")),

        Topic("files", "FILES", "Files, tabs, Save, and Export",
            "Open source audio, manage document tabs, and choose the right output command.",
            "open save save as export recent drag drop bit depth wav aif aiff aifc mp3 flac m4a wma tabs",
            Section("Open and Open As Bit Depth",
                "Open accepts WAV, classic AIFF PCM, and common uncompressed AIFF-C PCM/float variants; availability of other compressed media formats depends on Windows codecs. AIFF-C is import-only. Open As Bit Depth creates a converted working copy as 16-bit PCM with or without dither, 24-bit PCM, or 32-bit float. Conversion does not improve the quality of a lower-resolution source."),
            Section("Save versus Save As",
                "Save writes a WaveLab-created WAV or AIFF document back to its existing path. Imported AIFF and AIFF-C files require Save As to a different path because WaveLab does not preserve every ancillary AIFF metadata chunk. Choose .aif or .aiff for classic AIFF output; .aifc output is not supported. New recordings and generated documents also require a path. Save As chooses a new WAV or classic AIFF path and encoding. Saving marks that document clean; it does not close the tab."),
            Section("Export",
                "Export creates a separate delivery file without changing the open document. Choose WAV or AIFF at the offered bit depths, FLAC when available, MP3, AAC/M4A, or WMA. Lossy formats expose a bitrate. You can keep the current sample rate or convert to 44.1, 48, 88.2, or 96 kHz, and export the whole file or the current selection."),
            Section("Tabs and recent files",
                "Each tab is an independent document. Click a tab to make it active, or its close button to close it. WaveLab asks before discarding unsaved work. File > Recent Files reopens a previously used path. Session reopening is controlled in Settings > General."),
            Section("Bit-depth guidance",
                "Use 32-bit float WAV for an intermediate master that may be processed again, 24-bit PCM WAV or AIFF for high-resolution delivery, and dithered 16-bit PCM for CD-compatible output. MP3, AAC, and WMA are smaller lossy listening copies. FLAC is lossless.")),

        Topic("editor", "EDITING", "Waveform, selection, cursor, and zoom",
            "Navigate precisely and define the range that editing commands will use.",
            "waveform overview time ruler cursor selection mouse wheel zoom amplitude pan",
            Section("Waveform gestures",
                "Click in the waveform to place the cursor or seek. Drag across it to create a selection. The overview bar represents the entire document and helps move the visible window quickly. The ruler shows time and marker positions."),
            Section("Zoom and pan",
                "Ctrl+= zooms in, Ctrl+- zooms out, and Ctrl+0 fits the whole file. Zoom to Selection fills the editor with the selected range. The mouse wheel zooms horizontally, Shift+wheel pans, and Ctrl+wheel changes waveform amplitude zoom. View > Reset Amplitude Zoom restores the normal vertical scale."),
            Section("Selection readouts",
                "SEL IN and SEL OUT are the selection boundaries; LENGTH is their difference. With no selection, commands that support a fallback usually operate on the entire file or at the cursor. Select All explicitly selects the whole document."),
            Section("Sample-level work",
                "Zoom in far enough to inspect individual samples and edit boundaries. Smooth Edit Points applies short smoothing around selection boundaries to reduce clicks at joins. Use it after cuts, pastes, or punch inserts when a discontinuity is audible.")),

        Topic("transport", "PLAYBACK", "Transport, playback, loop, and Arm",
            "Control playback and understand the difference between Record Setup and armed recording.",
            "play stop loop home transport arm record device",
            Section("Playback controls",
                "Play/Pause or Space starts playback at the current position, then pauses or resumes it. Stop ends the active transport. Home returns to the beginning. Loop repeats the selection when a selection exists; otherwise it repeats the playable document range."),
            Section("Arm Recording Input",
                "Arm is a fast path. When Arm is off, Record opens the full Record Audio dialog. When Arm is on, Record immediately captures from the input selected in Settings. Press Record again to stop and place the new unsaved recording in a tab. Arm resets when WaveLab starts so recording cannot begin unexpectedly."),
            Section("When controls are unavailable",
                "Playback is disabled while a recording is active or being finalized. Recording setup is disabled while a capture is active, finalizing, or waiting to be recovered. Let finalization finish before starting another transport operation.")),

        Topic("recording-levels", "RECORDING", "Recording and the Level Assistant",
            "Choose an input, check the loudest passage, avoid clipping, and start a clean take.",
            "record vinyl input device mix format check levels true peak projected rms crest clipping gain count-in punch loudness lufs noise floor hum dc clicks history",
            Section("Input device and format",
                "Input Device chooses the Windows recording endpoint. 'Uses the Windows input-device format' means the device supplies its configured sample rate and channel count, such as 48 kHz stereo. WaveLab captures in 32-bit float working precision. When saving the recording, you choose one WAV encoding: 16-bit, 24-bit, or 32-bit float. It does not create all three."),
            Section("Check Levels",
                "Cue the loudest musical passage on the record side, press Check Levels, and play at least 10 active seconds; 30 to 60 seconds is safer. Monitoring audio is discarded. Pressing Start Recording switches to a fresh retained take, so the level-check passage is not included. Restart Check clears the measurements after changing hardware gain."),
            Section("Meters and target band",
                "Peak shows the highest current sample, RMS shows average signal energy, and held peak remembers the highest measured level. The shaded target band moves with the remaining safety reserve. Avoid any clipping indication. Adjust the interface or preamp before capture; lowering digital gain later cannot repair an overloaded analogue stage or converter."),
            Section("Assistant readouts",
                "True Peak estimates intersample peaks in dBTP, including isolated artifacts. Prog Peak is the click-resistant programme peak that drives the recommendation; narrow vinyl clicks are excluded so one pop cannot set the gain for the whole side. Clicks Above shows how far those artifacts sit above the music. Projected adds a safety reserve for louder peaks not yet heard; the reserve shrinks as the scan matures and grows for high-crest (dynamic) programme. Prog RMS is average active-program level in dBFS; Loudness is the passage's EBU R128 integrated LUFS. Noise Floor estimates the quiet-passage level (surface noise and hiss). Crest is the difference between programme peak and average level. L/R reports persistent channel imbalance. DC Offset warns about constant waveform bias that wastes headroom. Hum names a 50 or 60 Hz mains component dominating quiet passages. Clipping reports digital rail hits, invalid device data, or repeated flat tops that may indicate upstream clipping."),
            Section("History strip and device memory",
                "The Recent Input Level strip scrolls the last 30 seconds of input against the target band, so you can confirm the loudest passage was actually scanned. When a check settles, WaveLab remembers the outcome for that input device and shows it under the device picker next time. A take started from a settled check carries the check summary into the new tab's status message."),
            Section("Suggested input change",
                "A reduction is safety guidance based on the hotter linked channel. An optional increase is deliberately conservative: only use it when the analogue chain has known headroom and noise is a real problem. With 24-bit or 32-bit-float capture, a somewhat low clean recording is normally safer than clipping and can be normalized later. Never adjust left and right independently from a short musical passage."),
            Section("Count-in, click, and punch",
                "Count-in waits the selected number of bars at the entered BPM before capture. Click while recording keeps the metronome audible during the take. If recording setup was opened while a document selection existed, Insert into selection on stop replaces that selection with the take and smooths the joins."),
            Section("Stopping and recovery",
                "Stop waits for the capture tail and then creates an unsaved document. Do not exit during finalization. If the input device stops unexpectedly, WaveLab attempts to preserve the buffered take and reports the failure. Save the new tab as soon as practical.")),

        Topic("editing-commands", "EDITING", "Edit and Process commands",
            "What every permanent editing command does.",
            "undo redo cut copy paste delete trim gain normalize fade reverse dc silence smooth channel",
            Section("Edit menu",
                "Undo and Redo step through document changes. Cut copies the selection then removes it. Copy places the selected audio on WaveLab's internal audio clipboard. Paste inserts it at the cursor or replaces the selection. Delete removes the selection without copying. Trim to Selection removes everything outside the selection. Select All selects the full document."),
            Section("Level and direction",
                "Gain +3 dB and Gain -3 dB apply a fixed level change. Normalize to -0.3 dBFS scales the range so its highest sample reaches that peak. Fade In and Fade Out apply an equal-power transition across the range. Reverse reverses sample order. Remove DC Offset removes a constant waveform bias."),
            Section("Silence and edit points",
                "Insert 1 s Silence at Cursor adds one second without overwriting audio. Detect Silences creates markers using the chosen threshold and duration. Trim Silences removes qualifying silent parts. Split by Silence creates named regions around detected programme sections. Smooth Edit Points softens boundaries to prevent clicks."),
            Section("Channels submenu",
                "Swap Left/Right exchanges stereo channels. Phase invert changes sample polarity for all, left, or right channels. Channel Balance applies a controlled left/right level adjustment. Mix Down to Mono, Mono to Stereo, and Extract Left/Right create new tabs so the original stays unchanged."),
            Section("Avoiding overload",
                "Gain, normalization, mixing, effects, and channel conversion can create peaks above full scale. Check the meters and statistics after processing. Use a limiter only when peak control is intended; lowering gain is the transparent option.")),

        Topic("markers", "EDITING", "Markers and regions",
            "Label positions, define tracks, and navigate a long recording.",
            "marker region sidecar cue split navigation manager",
            Section("Markers",
                "A marker labels one sample position. Add Marker uses the selection start, the live playhead when playing, or the cursor. Previous/Next Marker moves between them. Markers appear on the ruler and are useful for notes and edit locations."),
            Section("Regions",
                "A region has a start, end, and name. Add Region from Selection requires a non-empty selection. Regions can represent songs, sides, chapters, or export ranges and can be synchronized with the Audio CD preparation workflow."),
            Section("Manage and persistence",
                "Manage Markers & Regions lists both types and lets you jump, rename, or delete. Clear All removes every marker and region from the active document. Marker data is saved alongside supported source files in a sidecar file; keep that sidecar with the audio file when moving a project.")),

        Topic("restoration", "RESTORATION", "Vinyl restoration and cleanup",
            "Repair clicks, clipped peaks, noise, and hum while protecting the source.",
            "vinyl restore clicks pops declip noise profile denoise hum workbench preview",
            Section("Vinyl Restoration & CD Transfer",
                "The workbench first analyzes the selection or file. It estimates clicks, flat-topped peaks, a quiet noise passage, and hum. Gentle, Balanced, and Strong presets set a starting point. The source is untouched until Apply; Apply & Prepare CD continues directly into track preparation."),
            Section("Clicks and declipping",
                "Click repair detects short impulsive defects and reconstructs them from nearby audio. Sensitivity changes what is detected; strength changes repair amount. Declip attempts to reconstruct repeated flattened peaks and needs headroom. Aggressive settings can soften real percussion, so use the preview and start gently."),
            Section("Noise and hum",
                "Learn Noise Profile from Selection captures a representative noise-only passage for later reduction. Reduce Noise uses that profile or automatic analysis. Noise reduction amount controls attenuation and sensitivity controls detection. Hum Removal notches 50 or 60 Hz and selected harmonics; use the frequency matching local mains power."),
            Section("Preview and apply",
                "A/B Preview compares dry and processed audio without editing the source. Wet/dry mix and Bypass help detect artifacts. Apply commits one undoable edit to the selection or file. Keep an untouched source recording until restoration choices are approved."),
            Section("Analyze & Tune",
                "Analyze & Tune Vinyl Cleanup and Clean Transfer inspect representative quiet and active passages, explain recommendations, and build a custom rack preset. Apply to Rack changes the live rack only; it does not render the document. Preview is bounded and cancellable.")),

        Topic("cd", "CD", "Extracting and preparing Audio CDs",
            "Import tracks from a disc or build a burner-ready package from a recording.",
            "cd cdda extract optical drive prepare tracks cue 44100 16 bit 588 frames gapless",
            Section("Extract Audio CD",
                "File > Extract Audio CD discovers Windows optical drives and audio tracks. Choose a drive, refresh if the disc changed, select the tracks to use, and start extraction. Imported CD audio opens as 44.1 kHz stereo lossless documents. Save or export the new tabs to keep them."),
            Section("Prepare Tracks for Audio CD",
                "Open a continuous recording, then use Restore > Prepare Tracks for Audio CD. Analyze proposes track boundaries from quiet gaps using the selected threshold. Use Selection adds the editor selection as a track. Reorder, remove, split, and edit In/Out values before export."),
            Section("Regions and preview",
                "Sync Regions writes the current track plan back to document regions. Preview Track plays the selected track using the same dry or rack-rendered path chosen for export. Render the current rack includes rack processing in preview and output; leave it off for the unprocessed source."),
            Section("CD package output",
                "Export CD Package creates gapless, 44.1 kHz, 16-bit, CD-frame-aligned WAV tracks and a CUE sheet. It does not burn a physical disc. Use disc-authoring software to burn the generated CUE/WAV package. Validate track order, names, boundaries, and total duration first.")),

        Topic("master-rack", "MASTERING", "Master Rack and rendering",
            "Build a real-time processing chain, save presets, and commit it safely.",
            "master rack effects preset bypass render in place copy latency meters",
            Section("Signal flow",
                "Enabled rack effects process from top to bottom. Move buttons change that order, the switch bypasses one effect, reset returns it to defaults, and remove deletes it from the chain. The rack switch bypasses the entire chain without losing individual settings. Add Effect opens the available processors."),
            Section("Presets and analysis",
                "The preset list loads a stored chain. Record to CD - Gentle Clarity is the safest starting point for a dark transfer; Dull Source Rescue applies a stronger presence-and-air lift, while Warm Record Open-Up adds low-end body as well. These tonal presets avoid automatic denoising, reserve headroom before the final true-peak limiter, and leave the source unchanged until rendering. Save Preset captures the current order, enabled states, and parameters under a name. Reset Chain returns to the default EQ and limiter layout. The sparkle button opens Analyze & Tune for Vinyl Cleanup or Clean Transfer."),
            Section("Render in Place",
                "Processes the current selection, or the whole file when there is no selection, and replaces it in the same tab as one undoable edit. Processing latency is compensated. Save after reviewing the result."),
            Section("Render Copy",
                "Processes the entire file into a new tab and leaves the source unchanged. This is the safest way to compare or create a master while retaining the original. The new document is unsaved."),
            Section("Meters",
                "Peak/RMS meters show channel level and held peaks. Integrated LUFS estimates overall programme loudness; short-term LUFS follows recent loudness. True Peak estimates intersample peaks. LRA describes loudness range. Correlation near +1 is strongly mono-compatible, near 0 is wide or unrelated, and negative values may cancel in mono. Reset clears accumulated meter history.")),

        Topic("effects", "MASTERING", "Rack effects reference",
            "A concise description of every effect available from Add Effect.",
            "eq compressor normalizer trim mono stereo width balance denoise dehum gate reverb delay chorus saturation filters limiter",
            Section("Tone and level",
                "Studio EQ adjusts broad low, mid, and high tonal ranges. Gain & Trim applies controlled level and trimming. Level Normalizer follows programme level toward a target within boost/cut limits. Compressor reduces dynamic range above its threshold and may add makeup gain. Precision Limiter catches peaks at a ceiling; it is peak protection, not a repair for clipped input."),
            Section("Stereo and channels",
                "Mono-to-Stereo Enhancer creates decorrelated width from a mono source while protecting bass and level. Stereo Width narrows or widens an existing stereo image and can keep low frequencies mono. Channel Balance & Alignment adjusts left/right level and timing alignment. Check phase correlation and mono compatibility after using these."),
            Section("Cleanup and control",
                "Noise & Hiss Reduction attenuates material identified as noise. Hum Removal suppresses a mains fundamental and harmonics. Noise Gate closes below a threshold to reduce noise between events. High-Pass removes low frequencies below its cutoff; Low-Pass removes high frequencies above its cutoff."),
            Section("Creative effects",
                "Reverb adds simulated space. Stereo Delay adds timed echoes. Chorus adds modulated doubling and width. Saturation adds harmonic colour and can increase peak or perceived loudness. Use mix controls conservatively when mastering archival material."),
            Section("Order matters",
                "Cleanup commonly comes before EQ and dynamics; a limiter normally comes last. There is no universal order. Compare at matched loudness, watch true peak, and bypass individual stages to confirm each one helps.")),

        Topic("analysis", "ANALYSIS", "Meters, graphs, and Audio Statistics",
            "Read WaveLab's visual analysis without confusing peak, average level, and loudness.",
            "spectrum spectrogram loudness history phase correlation statistics rms lufs true peak clipping",
            Section("Spectrum and spectrogram",
                "Spectrum shows the current frequency balance during playback. Spectrogram shows frequency over time for the visible range; Refresh Spectrogram rebuilds it after an edit or view change. Bright or strong areas indicate more energy, not automatically a defect."),
            Section("Loudness History",
                "The history graph tracks loudness across playback. Momentary and short-term values react at different speeds; integrated LUFS accumulates across the measured programme. Reset meters before measuring a new song or master."),
            Section("Phase view",
                "The phase/goniometer view shows stereo relationship. A narrow vertical pattern is mono-like, a wider pattern is more spacious, and strong horizontal or negative-correlation content may cancel in mono. Panned music is not necessarily a fault."),
            Section("Audio Statistics",
                "Analyze > Audio Statistics scans the document and reports duration, format, sample peak, RMS, DC offset, clipping/full-scale counts, and related channel data. Copy places the report on the clipboard. Statistics describe the stored document, while live rack meters describe playback through the current chain.")),

        Topic("tools", "TOOLS", "Time, pitch, sample rate, tuner, BPM, and batch conversion",
            "Use the utility commands and know which operations create or modify audio.",
            "time stretch pitch shift resample sample rate tuner bpm tempo batch converter",
            Section("Time Stretch and Pitch Shift",
                "Time Stretch changes duration while aiming to preserve pitch. Pitch Shift changes pitch while aiming to preserve duration. Both process the selection or whole file and are undoable. Extreme changes can create transient, phase, or texture artifacts."),
            Section("Convert Sample Rate",
                "Resamples the document using a windowed-sinc converter. Sample-rate conversion changes the number of samples per second, not musical speed. Use 44.1 kHz for Audio CD output when preparing it manually, or let the CD package workflow convert automatically."),
            Section("Tuner and BPM",
                "Detect Pitch estimates a dominant musical pitch in the selected or active audio; complex chords and noise may not produce a stable note. Detect Tempo estimates BPM from rhythmic content; use a representative section with clear beats."),
            Section("Batch Converter",
                "Add multiple files, choose an output folder and format, optionally normalize by peak or LUFS, and optionally apply a saved effect-chain preset. Start processes the queue without opening every source. Existing source/output collisions are rejected. Cancel stops after the current cancellable work yields."),
            Section("Command Palette",
                "Ctrl+Shift+P opens a searchable command list. Type part of a command name, use the arrow keys to choose, and press Enter to run it. Esc closes it.")),

        Topic("settings", "SETTINGS", "Settings, autosave, and recovery",
            "Configure devices and defaults, and understand what WaveLab can recover.",
            "settings general audio hardware input output buffer latency wasapi shared exclusive event polling endpoint role diagnostics test autosave recovery export default undo",
            Section("General",
                "Reopen last session files restores previously open paths on launch. Undo History Limit is a memory budget: once exceeded, the oldest edit history is discarded. It does not limit the source audio or current document."),
            Section("Audio",
                "Audio Hardware chooses the playback and default recording endpoints. Follow Windows default tracks the selected endpoint role: Multimedia for music/media, Console for general interactive audio, or Communications for calls and headsets. Explicit device selections stay pinned until the endpoint disappears or you change them."),
            Section("WASAPI engine modes",
                "Shared mode uses the Windows mixer and endpoint mix format, permits other applications to use the device, and is recommended. Exclusive mode bypasses the mixer and can block other applications; playback succeeds only when the current document's float sample rate and channel format is accepted by the endpoint. Event-driven scheduling is normally the efficient, precise choice. Polling is a compatibility fallback for unusual drivers."),
            Section("Buffers, test, and diagnostics",
                "Playback and Capture Request are requested stream-buffer durations from 3 to 500 ms. They are not guaranteed round-trip latency: driver periods and processing add delay. Lower values improve responsiveness but may fail or glitch; higher values improve safety. Refresh re-enumerates endpoints. Test Output opens the selected output with the unsaved controls and plays a quiet tone. Test Input opens the selected capture path for 1.5 seconds without retaining audio and reports peak and RMS. Live diagnostics report the resolved endpoint, shared-mode mix format, default/minimum engine period, endpoint volume and hardware controls, endpoint ID, and a non-exhaustive probe of standard exclusive float rates. Audio changes apply to the next playback, level check, or recording stream."),
            Section("Autosave",
                "Autosave stores recovery copies of modified files at the chosen interval. After an unclean shutdown, WaveLab offers recoverable work. Normal exit removes its temporary recovery data. Autosave is not a replacement for Save: named project copies and finished recordings should still be saved normally."),
            Section("Export defaults",
                "Default Format preselects the Export dialog. Default Bitrate applies to lossy formats. You can override both for each export. Restore Defaults changes the controls to factory values; Save commits settings, while Cancel leaves existing settings unchanged."),
            Section("Window and recent state",
                "WaveLab remembers window size and position after a clean exit, plus recent files and selected devices where available. If a stored audio endpoint disappears, WaveLab falls back to the current Windows default.")),

        Topic("formats", "REFERENCE", "Audio terms and formats",
            "Plain-language definitions for the measurements and format choices used throughout WaveLab.",
            "glossary sample rate bit depth pcm float dbfs dbtp rms lufs lra dither clipping headroom",
            Section("Sample rate",
                "Sample rate is the number of audio frames per second. 44.1 kHz is standard for Audio CD; 48 kHz is common for Windows and video. A higher number does not restore detail missing from the source. Converting rate is resampling."),
            Section("Bit depth and float",
                "16-bit and 24-bit PCM store fixed-resolution integer samples. 32-bit float is WaveLab's high-headroom working representation and is useful between processing stages. It cannot undo clipping that happened in the analogue input or ADC. Exporting to a higher bit depth does not add source detail."),
            Section("dBFS, dBTP, RMS, and LUFS",
                "dBFS measures digital sample amplitude, where 0 dBFS is the integer full-scale ceiling. dBTP estimates reconstructed intersample peak and can exceed the highest stored sample. RMS describes average electrical signal energy. LUFS estimates perceived programme loudness; integrated LUFS covers the measured programme, while short-term and momentary values react faster."),
            Section("Dither",
                "Dither is very low noise added when reducing bit depth so quiet quantization distortion becomes less correlated and less objectionable. Use dither once, at the final reduction to 16-bit. Choose undithered 16-bit only when a downstream process will dither or when explicitly required."),
            Section("Headroom and clipping",
                "Headroom is space between peaks and the ceiling. Digital clipping occurs when samples hit the available rail. True-peak over means reconstructed output may exceed 0 dBTP even when stored samples do not. Upstream clipping can occur in a cartridge preamp, mixer, or interface before WaveLab and cannot be repaired by lowering software volume afterward.")),

        Topic("shortcuts", "REFERENCE", "Keyboard and mouse shortcuts",
            "All shortcuts currently available from the main window.",
            "keyboard hotkeys mouse gestures keys f1",
            Section("Help and files",
                "F1 - open this help. Ctrl+O - Open. Ctrl+S - Save. Ctrl+Shift+S - Save As. Ctrl+E - Export. Ctrl+W - Close File. Ctrl+Shift+P - Command Palette."),
            Section("Transport",
                "Space - Play/Pause. Home - Go to Start. Ctrl+R - Record or Stop Recording. Alt+Left - Previous Marker. Alt+Right - Next Marker."),
            Section("Editing",
                "Ctrl+Z - Undo. Ctrl+Y - Redo. Ctrl+X - Cut. Ctrl+C - Copy. Ctrl+V - Paste. Delete - Delete Selection. Ctrl+A - Select All. Ctrl+M - Add Marker. Ctrl+Shift+M - Add Region from Selection."),
            Section("Zoom and mouse",
                "Ctrl+= - Zoom In. Ctrl+- - Zoom Out. Ctrl+0 - Zoom to Fit. Mouse wheel - horizontal zoom. Shift+wheel - pan. Ctrl+wheel - waveform amplitude zoom. Drag the waveform - select a range."),
            Section("Inside Help",
                "Ctrl+F focuses the help search box. Up/Down changes the selected topic after focusing the topic list. Esc closes Help.")),

        Topic("troubleshooting", "SUPPORT", "Troubleshooting",
            "Practical checks when playback, recording, exporting, or performance does not behave as expected.",
            "problem no sound record device clipping dropouts export codec recovery crash slow cpu",
            Section("No playback or wrong device",
                "Open Settings > Audio and choose the intended Output Device. Stop playback and start it again because device and buffer changes apply to the next playback. Confirm Windows has not disconnected, disabled, or exclusively occupied the endpoint."),
            Section("Cannot record or no input level",
                "Open Settings > Audio or Record Setup and choose the correct input. Confirm Windows microphone privacy allows desktop apps, the interface input is enabled, and the physical preamp is supplying signal. Use Check Levels. If the device vanished, refresh by reopening the dialog or select the Windows default input."),
            Section("Clicks, dropouts, or high CPU",
                "Increase the Playback Buffer in Settings. Bypass expensive rack effects to isolate the cause. Close other real-time audio applications, avoid editing while long analysis or rendering is running, and use a stable local drive. Input clipping is not a buffer problem; reduce hardware gain."),
            Section("Export format unavailable or failed",
                "Compressed export depends on Windows encoders; FLAC is shown only when available. Try WAV to separate codec problems from document problems. Verify the destination folder is writable and has free space. Batch Converter rejects output paths that would overwrite a source."),
            Section("Unsaved recording or recovery warning",
                "Wait for FINALIZING to finish. If WaveLab reports that a buffered capture needs retry, press Record/Stop again to retry preservation and do not exit unless you intentionally discard it. After recovery, immediately Save As to a known location."),
            Section("Clipping warning",
                "Digital clipping: lower interface input gain and replay the passage. Upstream or flat-top clipping: reduce gain earlier in the analogue chain. True-peak over without sample clipping: lower final level or use a suitable limiter. A clean conservative capture is preferable to a clipped one."))
    ];

    public static HelpTopic GetTopic(string? id) =>
        Topics.FirstOrDefault(topic => string.Equals(topic.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Topics[0];

    public static IReadOnlyList<HelpTopic> Search(string? query)
    {
        string[] terms = (query ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return Topics;
        return Topics.Where(topic => terms.All(term =>
            topic.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static HelpTopic Topic(
        string id, string category, string title, string summary, string keywords,
        params HelpSection[] sections) => new(id, category, title, summary, keywords, sections);

    private static HelpSection Section(string title, string body) => new(title, body);
}
