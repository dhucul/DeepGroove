"""Compare a finished lyrics adapter with its base on every reserved excerpt.

Writes research results only. Does not install or publish a model.
"""
import argparse
from contextlib import nullcontext
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time
import traceback

from train_adapter import atomic_json, audio_interval, feature_key, validate_manifest


def select_validation(rows, selection_predictions):
    validate_manifest(rows)
    selected = {(r["song"], r["start"], r["end"]) for r in selection_predictions}
    first = {}
    for row in rows:
        if row["split"] == "validation" and row["text"]:
            first[row["song"]] = min(first.get(row["song"], float("inf")), row["start"])
    result = []
    for row in rows:
        if row["split"] != "validation":
            continue
        result.append({**row, "used_for_selection": (row["song"], row["start"], row["end"]) in selected,
                       "first_annotated_phrase": bool(row["text"]) and row["start"] == first[row["song"]]})
    if not result:
        raise ValueError("No reserved examples")
    return sorted(result, key=lambda r: (r["song"], r["source"], r["start"]))


def metrics(rows, normalize):
    import jiwer
    references = [normalize(r["text"]) for r in rows]
    predictions = [normalize(r["prediction"]) for r in rows]
    if not rows:
        return None
    stats = jiwer.process_words(references, predictions)
    words = stats.hits + stats.substitutions + stats.deletions
    negatives = [i for i, reference in enumerate(references) if not reference]
    return {"examples": len(rows), "songs": len({r["song"] for r in rows}),
            "reference_words": words, "wer": stats.wer if words else None,
            "deletion_rate": stats.deletions / words if words else None,
            "substitutions": stats.substitutions, "deletions": stats.deletions, "insertions": stats.insertions,
            "negative_examples": len(negatives),
            "negative_false_positives": sum(bool(predictions[i]) for i in negatives),
            "words_on_negatives": sum(len(predictions[i].split()) for i in negatives),
            "truncated_generations": sum(r["truncated"] for r in rows)}


def summarize(rows, normalize):
    groups = {"all": rows,
              "unseen_excerpts": [r for r in rows if not r["used_for_selection"]],
              "first_annotated_phrases": [r for r in rows if r["first_annotated_phrase"]]}
    for source in ("mixture", "vocals"):
        group = [r for r in rows if r["source"] == source]
        groups[source] = group
        groups[source + "_positive"] = [r for r in group if r["text"]]
        groups[source + "_unseen"] = [r for r in group if not r["used_for_selection"]]
    groups["instrumental"] = [r for r in rows if not r["text"]]
    return {"groups": {name: metrics(group, normalize) for name, group in groups.items()},
            "songs": {song: metrics([r for r in rows if r["song"] == song], normalize)
                      for song in sorted({r["song"] for r in rows})}}


def excerpt_id(row):
    return row["song"], row["source"], row["start"], row["end"]


def capped_excerpt_ids(previous):
    return {excerpt_id(row) for rows in previous.values() for row in rows if row["truncated"]}


def validate_reused_base(rows, predictions, prior, manifest_hash, metadata, generation_cap):
    if (prior["manifest_sha256"] != manifest_hash or prior["base_model"] != metadata["base_model"]
            or prior["base_revision"] != metadata["base_revision"] or prior["generation_cap"] != generation_cap):
        raise ValueError("Cached baseline must use the same data, base model, and decoding cap")
    if len(rows) != len(predictions):
        raise ValueError("Cached baseline has a different number of examples")
    for row, prediction in zip(rows, predictions):
        if any(prediction.get(key) != value for key, value in row.items()):
            raise ValueError("Cached baseline examples or reference labels differ")
        if not isinstance(prediction.get("prediction"), str) or not isinstance(prediction.get("truncated"), bool):
            raise ValueError("Cached baseline is incomplete")


def wait_for_training(path, update, timeout):
    import psutil
    started = time.monotonic()
    while True:
        status = json.loads((path / "status.json").read_text(encoding="utf-8"))
        phase = status["phase"]
        if phase == "completed":
            return status
        if phase in ("failed", "stopped") or not psutil.pid_exists(status["pid"]):
            raise RuntimeError(f"Training is not running/completed: {phase}")
        if time.monotonic() - started > timeout:
            raise TimeoutError("Training did not finish before the evaluation timeout")
        update(phase="waiting_for_training", training_step=status.get("step", 0), training_phase=phase)
        time.sleep(10)


def run(args, update):
    training = wait_for_training(args.training_run, update, args.wait_seconds)
    pointer = json.loads((args.training_run / "best-checkpoint.json").read_text(encoding="utf-8"))
    checkpoint = Path(pointer["path"]).resolve()
    meta = json.loads((checkpoint / "checkpoint.json").read_text(encoding="utf-8"))
    manifest_hash = hashlib.sha256(args.manifest.read_bytes()).hexdigest()
    if meta["manifest_sha256"] != manifest_hash or training["manifest_sha256"] != manifest_hash:
        raise ValueError("Evaluation must use the original, unchanged split manifest")
    if meta["base_model"] != training["base_model"] or meta["base_revision"] != training["base_revision"]:
        raise ValueError("Checkpoint does not match the training base")
    selection = json.loads((args.training_run / "baseline.json").read_text(encoding="utf-8"))["predictions"]
    rows = select_validation([json.loads(line) for line in args.manifest.read_text(encoding="utf-8").splitlines()
                              if line.strip()], selection)
    previous_predictions = None
    reused_base = None
    if args.reuse_base_from:
        prior = json.loads((args.reuse_base_from / "comparison.json").read_text(encoding="utf-8"))
        prior_status = json.loads((args.reuse_base_from / "status.json").read_text(encoding="utf-8"))
        if prior_status["phase"] != "completed":
            raise ValueError("Cached baseline run has not completed")
        prior["generation_cap"] = prior.get("generation_cap", prior_status.get("max_new_tokens"))
        reused_base = json.loads((args.reuse_base_from / "base-predictions.json")
                                .read_text(encoding="utf-8"))["predictions"]
        validate_reused_base(rows, reused_base, prior, manifest_hash, meta, args.max_new_tokens)
    if args.only_capped_from:
        prior = json.loads((args.only_capped_from / "comparison.json").read_text(encoding="utf-8"))
        if prior["manifest_sha256"] != manifest_hash or Path(prior["checkpoint"]).resolve() != checkpoint:
            raise ValueError("Cap check must use the same dataset and checkpoint")
        previous_predictions = {name: json.loads((args.only_capped_from / f"{name}-predictions.json")
                                .read_text(encoding="utf-8"))["predictions"] for name in ("base", "adapted")}
        capped = capped_excerpt_ids(previous_predictions)
        rows = [row for row in rows if excerpt_id(row) in capped]
        if not rows:
            raise ValueError("No capped generations need rechecking")
    update(phase="loading_model", checkpoint=str(checkpoint), checkpoint_step=pointer["step"],
           manifest_sha256=manifest_hash, base_model=meta["base_model"], base_revision=meta["base_revision"],
           examples=len(rows), songs=len({r["song"] for r in rows}), max_new_tokens=args.max_new_tokens)
    import numpy as np
    import scipy.signal
    import soundfile as sf
    import torch
    from transformers import WhisperForConditionalGeneration, WhisperProcessor
    from peft import PeftModel
    torch.manual_seed(42)
    np.random.seed(42)
    torch.set_num_threads(4)
    if not torch.cuda.is_available() or not torch.cuda.is_bf16_supported():
        raise RuntimeError("This evaluation recipe requires a BF16-capable NVIDIA GPU")
    processor = WhisperProcessor.from_pretrained(args.base_model, local_files_only=True,
                                                 language="english", task="transcribe")
    base = WhisperForConditionalGeneration.from_pretrained(args.base_model, local_files_only=True,
        torch_dtype=torch.bfloat16, attn_implementation="sdpa", low_cpu_mem_usage=True)
    base.generation_config.forced_decoder_ids = None
    model = PeftModel.from_pretrained(base, checkpoint).to("cuda").eval()
    cache = args.output / "features"
    cache.mkdir(exist_ok=True)

    def features(row):
        key = feature_key({k: v for k, v in row.items() if k not in ("used_for_selection", "first_annotated_phrase")})
        path = cache / (key + ".pt")
        if path.exists():
            return torch.load(path, map_location="cpu", weights_only=True)
        if args.reuse_base_from:
            previous_feature = args.reuse_base_from / "features" / (key + ".pt")
            if previous_feature.exists():
                return torch.load(previous_feature, map_location="cpu", weights_only=True)
        with sf.SoundFile(row["audio"]) as audio:
            start, end = audio_interval(row["start"], row["end"], audio.samplerate, len(audio))
            rate = audio.samplerate
            audio.seek(start)
            samples = audio.read(end - start, dtype="float32", always_2d=True)
        mono = samples.mean(axis=1)
        energies = np.mean(samples.astype(np.float64) ** 2, axis=0)
        strongest = int(np.argmax(energies))
        if float(np.mean(mono.astype(np.float64) ** 2)) < .25 * energies[strongest]:
            mono = samples[:, strongest]
        divisor = math.gcd(rate, 16000)
        mono = scipy.signal.resample_poly(mono, 16000 // divisor, rate // divisor).astype(np.float32)
        if not np.isfinite(mono).all():
            raise ValueError("Non-finite audio")
        value = processor.feature_extractor(mono, sampling_rate=16000, return_tensors="pt", return_attention_mask=True)
        result = {"features": value.input_features.half(), "mask": value.attention_mask}
        torch.save(result, path)
        return result

    summaries = {}
    for name in ("base", "adapted"):
        if name == "base" and reused_base is not None:
            atomic_json(args.output / "base-predictions.json", {"predictions": reused_base})
            summaries[name] = summarize(reused_base, processor.tokenizer.normalize)
            atomic_json(args.output / "base-metrics.json", summaries[name])
            update(phase="reused_baseline", baseline_source=str(args.reuse_base_from), completed=len(rows), total=len(rows))
            continue
        predictions = []
        # The disabled-adapter context uses exactly the same frozen base weights.
        with (model.disable_adapter() if name == "base" else nullcontext()), torch.inference_mode():
            for index, row in enumerate(rows):
                value = features(row)
                generated = model.generate(input_features=value["features"].to("cuda", dtype=torch.bfloat16),
                    attention_mask=value["mask"].to("cuda"), language="en", task="transcribe",
                    return_timestamps=False, max_new_tokens=args.max_new_tokens, num_beams=3, use_cache=True,
                    return_dict_in_generate=True)
                # Whisper's plain tensor return strips EOS (and can be empty).
                # The raw generation output preserves EOS for a valid cap check.
                tokens = generated.sequences
                prediction = processor.tokenizer.batch_decode(tokens, skip_special_tokens=True)[0]
                truncated = int(tokens[0, -1]) != processor.tokenizer.eos_token_id
                predictions.append({**row, "prediction": prediction, "truncated": truncated})
                if (index + 1) % 8 == 0 or index + 1 == len(rows):
                    atomic_json(args.output / f"{name}-predictions.json", {"predictions": predictions})
                    update(phase=name, completed=index + 1, total=len(rows))
        if previous_predictions is not None:
            replacements = {excerpt_id(row): row for row in predictions}
            predictions = [replacements.get(excerpt_id(row), row) for row in previous_predictions[name]]
            atomic_json(args.output / f"{name}-all-predictions.json", {"predictions": predictions})
        summaries[name] = summarize(predictions, processor.tokenizer.normalize)
        atomic_json(args.output / f"{name}-metrics.json", summaries[name])
    atomic_json(args.output / "comparison.json", {
        "checkpoint": str(checkpoint), "checkpoint_step": pointer["step"],
        "manifest_sha256": manifest_hash, "base_model": meta["base_model"], "base_revision": meta["base_revision"],
        "scope": "All reserved short excerpts; same artists as development validation. Not an independent final test or the full application pipeline.",
        "generation_cap": args.max_new_tokens,
        "cap_recheck_of": str(args.only_capped_from) if args.only_capped_from else None,
        "reused_base_from": str(args.reuse_base_from) if args.reuse_base_from else None,
        "results": summaries})
    update(phase="completed", results=summaries, deployment="Research evaluation only; Release model unchanged")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--training-run", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--base-model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--wait-seconds", type=int, default=3600)
    parser.add_argument("--max-new-tokens", type=int, default=128)
    parser.add_argument("--only-capped-from", type=Path)
    parser.add_argument("--reuse-base-from", type=Path)
    args = parser.parse_args()
    if args.only_capped_from and args.reuse_base_from:
        parser.error("Baseline reuse and a cap recheck cannot be combined")
    if not 1 <= args.max_new_tokens <= 440:
        parser.error("Generation cap must be between 1 and 440")
    args.output.mkdir(parents=True, exist_ok=True)
    if (args.output / "status.json").exists():
        raise RuntimeError("Use a new evaluation directory to preserve existing results")
    log = (args.output / "evaluation.log").open("a", encoding="utf-8", buffering=1)
    sys.stdout = sys.stderr = log
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    state = {"pid": os.getpid(), "started_at": time.strftime("%Y-%m-%dT%H:%M:%S%z")}
    def update(**values):
        state.update(values)
        state["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        atomic_json(args.output / "status.json", state)
        print(json.dumps(values, allow_nan=False), flush=True)
    update(phase="starting")
    try:
        run(args, update)
    except BaseException as error:
        traceback.print_exc()
        update(phase="failed", error=f"{type(error).__name__}: {error}")
        raise


if __name__ == "__main__":
    main()
