"""Run suggestion-only research against an unchanged pause-aware transcript."""
import argparse
import gc
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
import traceback
from types import SimpleNamespace


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--baseline-runs", type=Path)
    parser.add_argument("--vocals-cache", type=Path)
    parser.add_argument("--user-song", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--reuse-proposals", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    sys.stdout = sys.stderr = (args.output / "evaluation.log").open("w", encoding="utf-8", buffering=1)
    state = {"pid": os.getpid()}
    def save(path, value):
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2), encoding="utf-8")
        tmp.replace(path)
    def update(**values):
        state.update(values)
        state["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        save(args.output / "status.json", state)
    update(phase="loading")
    try:
        os.environ["HF_HOME"] = str(args.bundle / "models")
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
        os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
        import torch
        dll = str(Path(torch.__file__).parent / "lib")
        os.environ["PATH"] = dll + os.pathsep + os.environ.get("PATH", "")
        handle = os.add_dll_directory(dll) if os.name == "nt" else None
        import numpy as np
        import ctranslate2
        from faster_whisper import WhisperModel
        repo = Path(__file__).resolve().parents[2]
        def load(name):
            folder = "training/lyrics" if name == "lyrics_opinions" else "src/WaveLab/Transcription"
            spec = importlib.util.spec_from_file_location(name, repo / folder / (name + ".py"))
            value = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(value)
            return value
        worker, opinions = load("lyrics_worker"), load("lyrics_opinions")
        save(args.output / "provenance.json", {"model": str(args.model.resolve()),
            "opinion_code_sha256": hashlib.sha256((repo / "training/lyrics/lyrics_opinions.py").read_bytes()).hexdigest(),
            "selection_sha256": hashlib.sha256(args.data.read_bytes()).hexdigest(), "confirmation_audio": "original_mix"})
        if args.reuse_proposals:
            old = json.loads((args.reuse_proposals / "provenance.json").read_text(encoding="utf-8"))
            if old["model"] != str(args.model.resolve()) or old["selection_sha256"] != hashlib.sha256(args.data.read_bytes()).hexdigest():
                raise ValueError("Cached proposals use a different model or data")
        songs = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
        cases = [(f"song-{i:02d}", Path(row["audio"])) for i, row in enumerate(songs, 1)]
        if args.user_song:
            cases.insert(0, ("user-song", args.user_song))
        for case, audio_path in cases:
            output = args.output / case
            output.mkdir()
            if args.baseline_runs:
                if not args.vocals_cache:
                    raise ValueError("Cached baselines require the matching vocal-cache root")
                cache = args.vocals_cache / case
                status = json.loads((cache / "status.json").read_text(encoding="utf-8"))
                if status["phase"] != "completed" or Path(status["input"]).resolve() != audio_path.resolve():
                    raise ValueError("Cached audio identity does not match")
                baseline = json.loads((args.baseline_runs / case / "retry.json").read_text(encoding="utf-8"))
                vocals_path = cache / "vocals.wav"
            else:
                update(phase="main_transcription", case=case, progress=0)
                worker.run(SimpleNamespace(check=False, input=audio_path, output=output / "base.json", model="large-v3",
                    device="auto", language="en", hints="", isolate=True, vocals_only=False, speech=False, compare=True,
                    no_second_opinion=True))
                baseline = json.loads((output / "base.json").read_text(encoding="utf-8"))
                vocals_path = output / "vocals.wav"
            save(output / "base.json", baseline)
            audio, _, _ = worker.decode_for_transcription(vocals_path)
            original, _, _ = worker.decode_for_transcription(audio_path)
            gc.collect()
            torch.cuda.empty_cache()
            before = json.dumps(baseline["lines"], ensure_ascii=False, sort_keys=True)
            if args.reuse_proposals:
                proposals = json.loads((args.reuse_proposals / case / "opinions.json").read_text(encoding="utf-8"))["suggestions"]
            else:
                model = WhisperModel(str(args.model.resolve()), device="cuda", compute_type="float16", local_files_only=True, cpu_threads=4)
                worker.seed_inference(np, torch, ctranslate2)
                update(phase="second_opinion", case=case, progress=0)
                proposals = opinions.suggest(model, audio, baseline["lines"], baseline["language"], worker.recovery,
                    lambda message, fraction=None: update(phase="second_opinion", case=case, progress=fraction, message=message))
                del model
                gc.collect()
                torch.cuda.empty_cache()
            save(output / "raw-opinions.json", {"suggestions": proposals})
            model = WhisperModel("large-v3", device="cuda", compute_type="float16", local_files_only=True, cpu_threads=4)
            worker.seed_inference(np, torch, ctranslate2)
            update(phase="cross_check", case=case, progress=0)
            proposals = opinions.cross_check(model, original, baseline["lines"], proposals, baseline["language"], worker.recovery,
                lambda message, fraction=None: update(phase="cross_check", case=case, progress=fraction, message=message))
            if before != json.dumps(baseline["lines"], ensure_ascii=False, sort_keys=True):
                raise RuntimeError("Suggestion pass changed the main transcript")
            review = opinions.attach_suggestions(baseline["lines"], proposals)
            if [r["text"] for r in review if not r.get("possible_missing")] != [r["text"] for r in baseline["lines"]]:
                raise RuntimeError("Review data changed the main transcript")
            save(output / "opinions.json", {"main_unchanged": True, "suggestions": proposals})
            save(output / "review.json", {**baseline, "lines": review})
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
