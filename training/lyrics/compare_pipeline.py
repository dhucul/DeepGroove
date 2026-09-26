"""Exercise the shipped timed-lyrics pipeline with base and research models.

Run with the bundled inference Python. Input stays local and outputs stay in artifacts.
"""
import argparse
import gc
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
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    repo = Path(__file__).resolve().parents[2]
    if not output.is_relative_to(repo / "artifacts") or output == repo / "artifacts":
        raise ValueError("Research output must be inside artifacts")
    if output.exists():
        raise ValueError("Output already exists; preserve the previous comparison")
    if not (args.candidate / "model.bin").is_file():
        raise ValueError("Candidate conversion is missing")
    output.mkdir(parents=True)
    sys.stdout = sys.stderr = (output / "pipeline.log").open("a", encoding="utf-8", buffering=1)
    state = {"pid": os.getpid(), "input": str(args.input.resolve()), "candidate": str(args.candidate.resolve())}
    def save(path, value):
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, allow_nan=False, indent=2), encoding="utf-8")
        temporary.replace(path)
    def update(**values):
        state.update(values)
        state["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        save(output / "status.json", state)
    update(phase="starting")
    try:
        os.environ["HF_HOME"] = str(args.bundle / "models")
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
        os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
        worker_path = repo / "src/WaveLab/Transcription/lyrics_worker.py"
        spec = importlib.util.spec_from_file_location("lyrics_worker", worker_path)
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
        update(phase="isolating_vocals")
        worker.run(SimpleNamespace(check=False, input=args.input, output=output / "separation.json",
            model="large-v3", device="auto", language="en", hints="", isolate=True,
            vocals_only=True, speech=False, compare=True))
        import torch
        import numpy as np
        import ctranslate2
        from faster_whisper import WhisperModel
        dll_handle = os.add_dll_directory(str(Path(torch.__file__).parent / "lib")) if os.name == "nt" else None
        if not torch.cuda.is_available() or ctranslate2.get_cuda_device_count() < 1:
            raise RuntimeError("GPU required for this application-engine comparison")
        original, _, _ = worker.decode_for_transcription(args.input)
        vocals, _, _ = worker.decode_for_transcription(output / "vocals.wav")
        summary = {}
        for name, source in (("base", "large-v3"), ("adapted", str(args.candidate.resolve()))):
            gc.collect()
            torch.cuda.empty_cache()
            worker.seed_inference(np, torch, ctranslate2)
            update(phase=name, progress=0)
            started = time.monotonic()
            model = WhisperModel(source, device="cuda", compute_type="float16", local_files_only=True,
                                 cpu_threads=4)
            def report(message, fraction=None):
                update(phase=name, message=message, progress=fraction)
            lines, language = worker.recovery.transcribe(model, vocals, original, "en", False, True, True, "", report)
            save(output / f"{name}.json", {"schema_version": 1, "language": language, "model": source,
                "isolated_vocals": True, "device": "cuda", "lines": lines})
            summary[name] = {"lines": len(lines), "review_lines": sum(bool(r["needs_review"]) for r in lines),
                             "recovered_lines": sum(bool(r.get("recovered")) for r in lines),
                             "seconds": round(time.monotonic() - started, 2)}
            del model
        save(output / "comparison.json", {"input": str(args.input.resolve()), "results": summary,
             "scope": "Qualitative full-song regression comparison; no verified complete reference transcript."})
        update(phase="completed", results=summary)
    except BaseException as error:
        traceback.print_exc()
        update(phase="failed", error=f"{type(error).__name__}: {error}")
        raise


if __name__ == "__main__":
    main()
