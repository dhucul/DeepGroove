"""Local-only lyrics inference. stdout is JSONL progress; the result is committed atomically.

Keep heavyweight imports inside run(): protocol/quality tests require only Python's stdlib.
Audio is never uploaded. Model weights may be downloaded on first use.
"""
import argparse
import gc
import json
import math
import os
from pathlib import Path
import sys
import traceback


def emit(message, fraction=None):
    print(json.dumps({"message": message, "fraction": fraction}, ensure_ascii=False), flush=True)


def needs_review(segment, words):
    return (not words or segment.avg_logprob < -0.8 or segment.no_speech_prob > 0.5
            or segment.compression_ratio > 2.4
            or any(w.probability < 0.5 for w in words))


def line_from_segment(segment, duration, offset=0):
    start = max(0, min(duration, segment.start + offset))
    end = max(start, min(duration, segment.end + offset))
    text = segment.text.strip()
    if not text or end <= start or not math.isfinite(start + end):
        return None
    words = list(segment.words or [])
    return {"start": start, "end": end, "text": text, "model_text": text,
            "needs_review": needs_review(segment, words), "alternative_text": "",
            "words": [{"start": max(start, min(end, w.start + offset)),
                       "end": max(start, min(end, w.end + offset)),
                       "word": w.word, "probability": max(0, min(1, w.probability))}
                      for w in words]}


def save_result(path, result):
    temporary = path.with_suffix(".partial")
    temporary.write_text(json.dumps(result, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    os.replace(temporary, path)


def decode_for_transcription(path):
    """Keep stereo channels separate until we know a mono mix will retain their energy.

    Returns mono samples, the peak across the source channels, and the channel selected
    if cancellation required a fallback. The caller also uses that selection to protect
    Demucs, whose normalization reference is itself a mono channel average.
    """
    import numpy as np
    import soundfile as sf
    from faster_whisper.audio import decode_audio

    # Upmixing mono to stereo in FFmpeg attenuates it by 3 dB. Keep the existing
    # mono path so this stereo safeguard does not change ordinary mono recordings.
    if sf.info(str(path)).channels == 1:
        mono = decode_audio(str(path), sampling_rate=16000)
        peak = max(abs(float(mono.min())), abs(float(mono.max()))) if len(mono) else 0.0
        return mono, peak, None
    left, right = decode_audio(str(path), sampling_rate=16000, split_stereo=True)
    if not len(left):
        return left, 0.0, None
    peak = max(abs(float(channel.min())) for channel in (left, right))
    peak = max(peak, *(abs(float(channel.max())) for channel in (left, right)))
    mono = (left + right) * 0.5
    # Double-precision reductions avoid accumulating rounding error on long selections.
    energies = [float(np.einsum("i,i->", channel, channel, dtype=np.float64)) for channel in (left, right)]
    strongest = 0 if energies[0] >= energies[1] else 1
    mixed_energy = float(np.einsum("i,i->", mono, mono, dtype=np.float64))
    # Averaging with an empty channel already costs 6 dB. Only fall back when
    # destructive interference loses more than that; ordinary stereo keeps both sides.
    if mixed_energy < 0.25 * energies[strongest]:
        return (left, right)[strongest].copy(), peak, strongest
    return mono, peak, None


def run(args):
    emit("Loading the local transcription engine…")
    # PyTorch's CUDA wheels contain cuBLAS/cuDNN. Make them visible to CTranslate2 on Windows.
    import torch
    dll_handle = None
    if os.name == "nt":
        dll_path = str(Path(torch.__file__).parent / "lib")
        os.environ["PATH"] = dll_path + os.pathsep + os.environ.get("PATH", "")
        dll_handle = os.add_dll_directory(dll_path)
    import numpy as np
    import soundfile as sf
    import ctranslate2
    from faster_whisper import WhisperModel

    if args.check:
        import demucs.api
        emit("Local engine ready. NVIDIA GPU available." if torch.cuda.is_available()
             else "Local engine ready. Using CPU.")
        return

    torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
    np.random.seed(0)
    torch.manual_seed(0)
    use_cuda = args.device != "cpu" and torch.cuda.is_available() and ctranslate2.get_cuda_device_count() > 0
    device = "cuda" if use_cuda else "cpu"
    original, source_peak, selected_channel = decode_for_transcription(args.input)
    duration = len(original) / 16000
    if not 0 < duration <= 1800.25:
        raise ValueError("Select between a fraction of a second and 30 minutes of audio.")
    result = {"schema_version": 1, "language": args.language or "", "model": args.model,
              "device": device, "isolated_vocals": False, "lines": []}
    # Do not feed digital silence to a language model, which can invent words from it.
    if source_peak < 1e-5:
        save_result(args.output, result)
        emit("No audible voice was found in this range.", 1)
        return

    if selected_channel is not None:
        emit("Stereo cancellation detected; using the stronger channel for analysis…")

    audio = original
    if args.isolate:
        from demucs.api import Separator
        emit("Loading the vocal isolation model (first use downloads model files)…", 0.03)

        def separation_progress(state):
            if state["state"] != "end":
                return
            # Four fine-tuned models and two shift passes; keep progress monotonic in the host.
            portion = (state["model_idx_in_bag"] +
                       (state["shift_idx"] + min(1, state["segment_offset"] / max(1, state["audio_length"]))) / 2)
            emit("Isolating the singing voice…", 0.05 + 0.40 * portion / state["models"])

        separator = Separator(model="htdemucs_ft", device=device, shifts=2, overlap=0.5,
                              segment=7, callback=separation_progress)
        samples, rate = sf.read(str(args.input), dtype="float32", always_2d=True)
        # separate_tensor only converts channel count when sample rate changes.
        if selected_channel is not None and samples.shape[1] == 2:
            # Preserve the original file. Only the analysis tensor is made dual mono.
            samples = np.repeat(samples[:, selected_channel:selected_channel + 1], 2, axis=1)
        elif samples.shape[1] == 1:
            samples = np.repeat(samples, 2, axis=1)
        elif samples.shape[1] != 2:
            samples = np.repeat(samples.mean(axis=1, keepdims=True), 2, axis=1)
        wave = torch.from_numpy(samples.T.copy())
        with torch.inference_mode():
            _, stems = separator.separate_tensor(wave, rate)
        vocal_path = args.output.parent / "vocals.wav"
        sf.write(str(vocal_path), stems["vocals"].cpu().numpy().T, separator.samplerate, subtype="FLOAT")
        audio, _, _ = decode_for_transcription(vocal_path)
        result["isolated_vocals"] = True
        del separator, stems, samples, wave
        gc.collect()
        if use_cuda:
            torch.cuda.empty_cache()

    emit("Loading the speech model (first use downloads model files)…", 0.48)
    compute = "float16" if use_cuda else "int8"
    model = WhisperModel(args.model, device=device, compute_type=compute,
                         cpu_threads=max(1, min(8, os.cpu_count() or 1)))
    options = dict(beam_size=5, best_of=5, temperature=[0.0, 0.2, 0.4],
                   condition_on_previous_text=False, word_timestamps=True,
                   # Speech VAD can discard held vowels and soft singing; music deliberately bypasses it.
                   vad_filter=args.speech, no_speech_threshold=0.6,
                   log_prob_threshold=-1.0, compression_ratio_threshold=2.4,
                   hallucination_silence_threshold=2.0, hotwords=args.hints or None)
    segments, info = model.transcribe(audio, language=args.language, task="transcribe", **options)
    result["language"] = info.language
    for segment in segments:
        line = line_from_segment(segment, duration)
        if line:
            part = audio[int(line["start"] * 16000):int(line["end"] * 16000)]
            # Omit only effectively silent output, not quiet but intelligible syllables.
            if len(part) and float(np.sqrt(np.mean(part.astype(np.float64) ** 2))) >= 1e-5:
                result["lines"].append(line)
        emit("Transcribing words and timing…", 0.50 + 0.39 * min(1, segment.end / duration))

    if args.compare and args.isolate:
        uncertain = [line for line in result["lines"] if line["needs_review"]]
        for index, line in enumerate(uncertain):
            emit("Checking a difficult line against the original mix…", 0.90 + 0.09 * index / max(1, len(uncertain)))
            start, end = int(line["start"] * 16000), int(line["end"] * 16000)
            alternatives, _ = model.transcribe(original[start:end], language=info.language,
                                                task="transcribe", **{**options, "vad_filter": False})
            alternative = " ".join(s.text.strip() for s in alternatives).strip()
            if alternative and alternative.casefold() != line["text"].casefold():
                line["alternative_text"] = alternative
    save_result(args.output, result)
    emit("Transcription complete. Listen through the lines marked Review.", 1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--input", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--model", choices=["large-v3", "turbo", "tiny"], default="large-v3")
    parser.add_argument("--device", choices=["auto", "cpu"], default="auto")
    parser.add_argument("--language")
    parser.add_argument("--hints", default="")
    parser.add_argument("--isolate", action="store_true")
    parser.add_argument("--speech", action="store_true")
    parser.add_argument("--compare", action="store_true")
    args = parser.parse_args()
    if not args.check and (not args.input or not args.output):
        parser.error("--input and --output are required")
    try:
        run(args)
    except Exception:
        # stderr stays separate from the progress protocol and the host offers a CPU retry.
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
