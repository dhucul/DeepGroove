"""Compare base and candidate with WaveLab's existing speech-mode pipeline."""
import argparse
import gc
import importlib.util
import json
import os
from pathlib import Path
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    sys.stdout = sys.stderr = (args.output / "speech.log").open("w", encoding="utf-8", buffering=1)
    def save(name, value):
        path = args.output / name
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
        temporary.replace(path)
    def update(**values):
        save("status.json", {"pid": os.getpid(), "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), **values})
    update(phase="loading")
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
        worker_path = Path(__file__).resolve().parents[2] / "src/WaveLab/Transcription/lyrics_worker.py"
        spec = importlib.util.spec_from_file_location("lyrics_worker", worker_path)
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
        data = json.loads(args.data.read_text(encoding="utf-8"))["examples"]
        for name, source in (("base", "large-v3"), ("adapted", str(args.candidate.resolve()))):
            model = WhisperModel(source, device="cuda", compute_type="float16", local_files_only=True, cpu_threads=4)
            predictions = []
            for index, row in enumerate(data):
                worker.seed_inference(np, torch, ctranslate2)
                audio, _, _ = worker.decode_for_transcription(Path(row["audio"]))
                lines, language = worker.recovery.transcribe(model, audio, audio, "en", True, False, False, "", lambda *args: None)
                predictions.append({**row, "prediction": " ".join(line["text"] for line in lines), "lines": lines})
                save(name + ".json", {"predictions": predictions})
                update(phase=name, completed=index + 1, total=len(data))
            del model
            gc.collect()
            torch.cuda.empty_cache()
        update(phase="completed", examples=len(data))
    except BaseException as error:
        import traceback
        traceback.print_exc()
        update(phase="failed", error=str(error))
        raise


if __name__ == "__main__":
    main()
