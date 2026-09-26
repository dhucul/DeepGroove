"""Compare vocal separators with the unchanged, bundled transcription pipeline.

Research only. A supervisor uses separate processes for the two Python runtimes
and each GPU stage, keeping research dependencies out of the application bundle.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
from types import SimpleNamespace


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    temporary.replace(path)


def worker_module():
    path = Path(__file__).resolve().parents[2] / "src/WaveLab/Transcription/lyrics_worker.py"
    spec = importlib.util.spec_from_file_location("lyrics_worker", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def mel(args):
    import numpy as np
    import soundfile as sf
    import torch
    import yaml
    from scipy.signal import resample_poly
    import random
    torch.set_num_threads(8)
    random.seed(0)
    np.random.seed(0)
    torch.manual_seed(0)
    provenance = json.loads((args.assets / "provenance.json").read_text(encoding="utf-8"))
    if sha(args.assets / "MelBandRoformer.ckpt") != provenance["checkpoint_sha256"]:
        raise ValueError("Separator checkpoint checksum mismatch")
    for name, expected in provenance["source_sha256"].items():
        if sha(args.assets / "source" / name) != expected:
            raise ValueError(f"Separator source checksum mismatch: {name}")
    sys.path.insert(0, str(args.assets / "source"))
    from models.mel_band_roformer import MelBandRoformer
    from utils import demix_track
    config_path = args.assets / "source/configs/config_vocals_mel_band_roformer.yaml"
    config = yaml.load(config_path.read_text(encoding="utf-8"), Loader=yaml.FullLoader)
    # Freeze before evaluation: published 8-second chunks, four overlaps.
    config["inference"]["num_overlap"] = 4
    samples, rate = sf.read(args.input, dtype="float32", always_2d=True)
    if args.seconds:
        samples = samples[:round(args.seconds * rate)]
    if samples.shape[1] == 1:
        samples = np.repeat(samples, 2, axis=1)
    if samples.shape[1] != 2 or not len(samples) or not np.isfinite(samples).all():
        raise ValueError("Expected finite mono/stereo audio")
    original_duration = len(samples) / rate
    if rate != 44100:
        divisor = math.gcd(rate, 44100)
        samples = resample_poly(samples, 44100 // divisor, rate // divisor, axis=0).astype(np.float32)
    model = MelBandRoformer(**config["model"])
    model.load_state_dict(torch.load(args.assets / "MelBandRoformer.ckpt", map_location="cpu", weights_only=True), strict=True)
    model.eval().to("cuda")
    torch.cuda.reset_peak_memory_stats()
    config = SimpleNamespace(**{key: SimpleNamespace(**value) for key, value in config.items()})
    stems, _ = demix_track(config, model, torch.from_numpy(samples.T.copy()), "cuda")
    vocals = stems["vocals"].T
    if vocals.shape != samples.shape or not np.isfinite(vocals).all():
        raise ValueError("Separator must retain every sample and return finite stereo audio")
    sf.write(args.output / "mel-vocals.wav", vocals, 44100, subtype="FLOAT")
    return {"frames": len(vocals), "sample_rate": 44100, "original_seconds": original_duration,
            "peak": float(np.max(np.abs(vocals))), "rms": float(np.sqrt(np.mean(vocals.astype(np.float64)**2))),
            "cuda_peak_allocated_mib": torch.cuda.max_memory_allocated() / 1024**2,
            "chunk_samples": 352800, "overlaps": 4, "checkpoint_sha256": provenance["checkpoint_sha256"]}


def transcribe(args):
    os.environ.update(HF_HOME=str(args.bundle / "models"), HF_HUB_OFFLINE="1",
                      HF_HUB_DISABLE_TELEMETRY="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1")
    import torch
    dll_path = str(Path(torch.__file__).parent / "lib")
    os.environ["PATH"] = dll_path + os.pathsep + os.environ.get("PATH", "")
    dll_handle = os.add_dll_directory(dll_path) if os.name == "nt" else None
    import numpy as np
    import ctranslate2
    from faster_whisper import WhisperModel
    worker = worker_module()
    if not args.vocals:
        # Exact current app path, including HTDemucs-ft, original-mix comparison,
        # pause-aware retries, and seed zero.
        worker.run(SimpleNamespace(check=False, input=args.input, output=args.output / "demucs.json",
            model="large-v3", device="auto", language="en", hints="", isolate=True,
            vocals_only=False, speech=False, compare=True))
        return {"variant": "demucs"}
    torch.set_num_threads(8)
    worker.seed_inference(np, torch, ctranslate2)
    original, _, _ = worker.decode_for_transcription(args.input)
    vocals, _, _ = worker.decode_for_transcription(args.vocals)
    if abs(len(original) - len(vocals)) > 2:
        raise ValueError("Separated audio duration drift")
    model = WhisperModel("large-v3", device="cuda", compute_type="float16", local_files_only=True, cpu_threads=8)
    lines, language = worker.recovery.transcribe(model, vocals, original, "en", False, True, True, "",
                                               worker.emit, phrase_mode="retry")
    save(args.output / "mel.json", {"schema_version": 1, "model": "large-v3", "language": language,
        "device": "cuda", "isolated_vocals": True, "lines": lines})
    return {"variant": "mel"}


def run(args):
    repo = Path(__file__).resolve().parents[2]
    provenance = {"data_sha256": sha(args.data), "script_sha256": sha(Path(__file__)),
        "worker_sha256": sha(repo / "src/WaveLab/Transcription/lyrics_worker.py"),
        "recovery_sha256": sha(repo / "src/WaveLab/Transcription/lyrics_recovery.py"),
        "separator": json.loads((args.assets / "provenance.json").read_text(encoding="utf-8")),
        "settings": {"seed": 0, "language": "en", "model": "large-v3", "phrase_mode": "retry",
                     "mel_overlap": 4, "mel_chunk_samples": 352800},
        "user_song": str(args.user_song) if args.user_song else None}
    for name in ("lyrics_worker.py", "lyrics_recovery.py"):
        if sha(repo / "src/WaveLab/Transcription" / name) != sha(args.bundle.parent / name):
            raise ValueError("Source transcription pipeline differs from installed Release")
    path = args.output / "provenance.json"
    if path.exists() and json.loads(path.read_text(encoding="utf-8")) != provenance:
        raise ValueError("Cannot resume an evaluation with changed inputs, code or settings")
    save(path, provenance)
    songs = json.loads(args.data.read_text(encoding="utf-8"))["songs"]
    cases = [(f"song-{i:02d}", row["song"], Path(row["audio"])) for i, row in enumerate(songs, 1)]
    if args.user_song:
        cases.insert(0, ("user-song", "Local regression song", args.user_song))
    for case, title, audio in cases:
        output = args.output / case
        output.mkdir(exist_ok=True)
        for stage in ("demucs", "separate-mel", "mel"):
            record = output / (stage + "-stage.json")
            if record.exists():
                previous = json.loads(record.read_text(encoding="utf-8"))
                outputs = ("demucs.json", "vocals.wav") if stage == "demucs" else (("mel-vocals.wav",) if stage == "separate-mel" else ("mel.json",))
                if previous["input_sha256"] != sha(audio) or any(not (output / name).exists() for name in outputs):
                    raise ValueError("Cached stage does not match input or has missing outputs")
                continue
            save(args.output / "status.json", {"phase": stage, "case": case, "song": title,
                 "updated_at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "pid": os.getpid()})
            python = sys.executable if stage == "separate-mel" else str(args.bundle / "python/python.exe")
            command = [python, "-B", "-X", "utf8", "-u", str(Path(__file__).resolve()),
                "--action", "mel" if stage == "separate-mel" else "transcribe", "--input", str(audio),
                "--output", str(output), "--bundle", str(args.bundle), "--assets", str(args.assets), "--stage", stage]
            if stage == "mel":
                command += ["--vocals", str(output / "mel-vocals.wav")]
            print(f"{case}: {stage}: {title}", flush=True)
            with (output / (stage + ".log")).open("w", encoding="utf-8") as log:
                subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
    save(args.output / "status.json", {"phase": "completed", "cases": len(cases)})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--action", choices=("run", "mel", "transcribe"), default="run")
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--data", type=Path)
    parser.add_argument("--user-song", type=Path)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--vocals", type=Path)
    parser.add_argument("--stage", default="smoke")
    parser.add_argument("--seconds", type=float)
    args = parser.parse_args()
    for field in ("assets", "bundle", "output", "data", "user_song", "input", "vocals"):
        if getattr(args, field):
            setattr(args, field, getattr(args, field).resolve())
    args.output.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    try:
        result = {"run": run, "mel": mel, "transcribe": transcribe}[args.action](args)
        if args.action != "run":
            save(args.output / (args.stage + "-stage.json"), {**result, "input_sha256": sha(args.input),
                 "seconds": round(time.monotonic() - started, 2)})
    except BaseException as error:
        save(args.output / "failure.json", {"error": str(error), "traceback": traceback.format_exc()})
        raise


if __name__ == "__main__":
    main()
