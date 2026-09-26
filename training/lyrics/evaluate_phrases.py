"""Evaluate fixed phrase strategies with the original bundled model, locally."""
import argparse
import gc
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import sys
import time
import traceback
from types import SimpleNamespace


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--baseline-runs", type=Path)
    parser.add_argument("--user-song", type=Path)
    parser.add_argument("--modes", nargs="+", choices=("primary", "retry"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    sys.stdout = sys.stderr = (args.output / "evaluation.log").open("w", encoding="utf-8", buffering=1)
    state = {"pid": os.getpid(), "modes": args.modes}
    def save(path, value):
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
        temporary.replace(path)
    def update(**values):
        state.update(values)
        state["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        save(args.output / "status.json", state)
    update(phase="starting")
    try:
        os.environ["HF_HOME"] = str(args.bundle / "models")
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
        os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
        import torch
        dll_path = str(Path(torch.__file__).parent / "lib")
        os.environ["PATH"] = dll_path + os.pathsep + os.environ.get("PATH", "")
        dll_handle = os.add_dll_directory(dll_path) if os.name == "nt" else None
        import numpy as np
        import ctranslate2
        from faster_whisper import WhisperModel
        repo = Path(__file__).resolve().parents[2]
        spec = importlib.util.spec_from_file_location("lyrics_worker", repo / "src/WaveLab/Transcription/lyrics_worker.py")
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
        save(args.output / "provenance.json", {"modes": args.modes,
            "recovery_sha256": hashlib.sha256((repo / "src/WaveLab/Transcription/lyrics_recovery.py").read_bytes()).hexdigest(),
            "selection_sha256": hashlib.sha256(args.data.read_bytes()).hexdigest(),
            "base_model": "large-v3", "baseline_cache": str(args.baseline_runs) if args.baseline_runs else None})
        songs = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
        cases = [(f"song-{index:02d}", Path(song["audio"])) for index, song in enumerate(songs, 1)]
        if args.user_song:
            cases.insert(0, ("user-song", args.user_song))
        for case, audio_path in cases:
            target = args.output / case
            target.mkdir()
            baseline = args.baseline_runs / case if args.baseline_runs else None
            if baseline:
                status = json.loads((baseline / "status.json").read_text(encoding="utf-8"))
                if status["phase"] != "completed" or Path(status["input"]).resolve() != audio_path.resolve():
                    raise ValueError("Baseline song does not match input")
                shutil.copyfile(baseline / "base.json", target / "base.json")
                vocals_path = baseline / "vocals.wav"
            else:
                update(phase="isolating_vocals", case=case)
                worker.run(SimpleNamespace(check=False, input=audio_path, output=target / "separation.json",
                    model="large-v3", device="auto", language="en", hints="", isolate=True,
                    vocals_only=True, speech=False, compare=True))
                vocals_path = target / "vocals.wav"
            original, _, _ = worker.decode_for_transcription(audio_path)
            vocals, _, _ = worker.decode_for_transcription(vocals_path)
            save(target / "windows.json", {"windows": worker.recovery.phrase_windows(vocals)})
            # Load after separation, so the two large models never share GPU memory.
            gc.collect()
            torch.cuda.empty_cache()
            model = WhisperModel("large-v3", device="cuda", compute_type="float16", local_files_only=True, cpu_threads=4)
            modes = args.modes if baseline else ["off"] + args.modes
            for mode in modes:
                name = "base" if mode == "off" else mode
                worker.seed_inference(np, torch, ctranslate2)
                started = time.monotonic()
                update(phase=name, case=case, progress=0)
                def report(message, fraction=None):
                    update(phase=name, case=case, progress=fraction, message=message)
                lines, language = worker.recovery.transcribe(model, vocals, original, "en", False, True, True, "", report,
                                                             phrase_mode=mode)
                save(target / f"{name}.json", {"schema_version": 1, "model": "large-v3", "language": language,
                    "device": "cuda", "isolated_vocals": True, "lines": lines,
                    "phrase_mode": mode, "seconds": round(time.monotonic() - started, 2)})
            del model
            gc.collect()
            torch.cuda.empty_cache()
        update(phase="completed", cases=len(cases), progress=1)
    except BaseException as error:
        traceback.print_exc()
        update(phase="failed", error=str(error))
        raise


if __name__ == "__main__":
    main()
