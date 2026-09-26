"""Local, resumable decoder-LoRA research pilot. Never changes the Release model."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import random
import re
import sys
import time
import traceback

BASE_ID = "openai/whisper-large-v3"
BASE_REVISION = "06f233fe06e710322aca913c1bc4249a0d71fce1"
LABEL_RECIPE = "whisper-nospeech-conditional-eos-v2"


def atomic_json(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False), encoding="utf-8")
    temporary.replace(path)


def validate_manifest(rows):
    if not rows or not any(row["split"] == "train" for row in rows) or not any(row["split"] == "validation" for row in rows):
        raise ValueError("Both training and validation examples are required")
    for key in ("song", "artist"):
        def identity(row):
            value = row[key].strip().casefold()
            return re.split(r"\s+(?:feat\.?|ft\.?|featuring)\s+", value)[0] if key == "artist" else value
        train = {identity(row) for row in rows if row["split"] == "train"}
        validation = {identity(row) for row in rows if row["split"] == "validation"}
        if train & validation:
            raise ValueError(f"Training/validation {key} leakage")
    for row in rows:
        if row["split"] not in ("train", "validation") or not 1 <= row["end"]-row["start"] <= 30:
            raise ValueError("Invalid split or segment duration")
        if row["kind"] not in ("a", "b", "d") or "*" in row["text"] or "?" in row["text"]:
            raise ValueError("Uncertain or overlapping annotations must not train the model")
        if bool(row["text"].strip()) == (row["kind"] == "d"):
            raise ValueError("Vocal labels need text; instrumental labels must be empty")


def feature_key(row):
    return hashlib.sha256(json.dumps(row, sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def audio_interval(start_seconds, end_seconds, sample_rate, frame_count):
    """Allow up to 100 ms of rounding at EOF in the manual annotations."""
    start, end = round(start_seconds * sample_rate), round(end_seconds * sample_rate)
    if start < 0 or start >= frame_count or end <= start or end > frame_count + round(.1 * sample_rate):
        raise ValueError("Annotation extends outside audio")
    return start, min(end, frame_count)


def training_targets(tokenizer, row):
    start = tokenizer.convert_tokens_to_ids("<|startoftranscript|>")
    if row["kind"] == "d":
        # Whisper predicts no-speech immediately after SOT, before language/task.
        # An empty English transcript teaches the wrong target at that position.
        no_speech = tokenizer.convert_tokens_to_ids("<|nospeech|>")
        if no_speech is None or no_speech == tokenizer.unk_token_id:
            raise ValueError("Tokenizer is missing Whisper's no-speech token")
        prompt = tokenizer("").input_ids
        if prompt[0] != start or prompt[-1] != tokenizer.eos_token_id or len(prompt) < 3:
            raise ValueError("Missing transcription prompt for instrumental supervision")
        # Also teach EOS when English/transcribe is explicitly supplied, as in
        # WaveLab. Do not supervise language/task tokens on instrumental audio:
        # that would counteract the no-speech target at the SOT position.
        decoder = prompt[:-1]
        labels = [-100] * len(decoder)
        labels[0], labels[-1] = no_speech, tokenizer.eos_token_id
        return labels, decoder
    labels = tokenizer(row["text"]).input_ids
    if labels[0] == start:
        labels = labels[1:]
    if len(labels) > 448:
        raise ValueError("Training transcript exceeds decoder context")
    return labels, [start] + labels[:-1]


def run(args, update):
    import numpy as np
    import scipy.signal
    import soundfile as sf
    import torch
    from transformers import WhisperForConditionalGeneration, WhisperProcessor
    from peft import LoraConfig, PeftModel, get_peft_model
    import jiwer

    random.seed(42)
    np.random.seed(42)
    torch.manual_seed(42)
    torch.set_num_threads(4)
    if not torch.cuda.is_available():
        raise RuntimeError("This training job requires the NVIDIA GPU; no silent CPU fallback")
    dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    if dtype != torch.bfloat16:
        raise RuntimeError("This recipe requires BF16 support; use a separately validated FP16 recipe on older GPUs")
    rows = [json.loads(line) for line in args.manifest.read_text(encoding="utf-8").splitlines() if line.strip()]
    validate_manifest(rows)
    manifest_hash = hashlib.sha256(args.manifest.read_bytes()).hexdigest()
    train_rows = [row for row in rows if row["split"] == "train"]
    validation_rows = [row for row in rows if row["split"] == "validation"]
    # WER uses a fixed song-balanced subset of original mixes, including some negatives.
    by_song = {}
    for row in validation_rows:
        if row["source"] == "mixture":
            by_song.setdefault(row["song"], []).append(row)
    evaluation = []
    for song, examples in sorted(by_song.items()):
        examples = list(examples)
        random.Random(44).shuffle(examples)
        evaluation.extend(examples[:max(1, args.eval_samples // max(1, len(by_song)))])
    evaluation = evaluation[:args.eval_samples]
    processor = WhisperProcessor.from_pretrained(args.base_model, local_files_only=True,
                                                 language="english", task="transcribe")
    processor.tokenizer.set_prefix_tokens(language="english", task="transcribe", predict_timestamps=False)
    cache = args.output.parent.parent / "feature-cache" / (args.model_id.replace("/", "_") + "-" + manifest_hash[:16])
    cache.mkdir(parents=True, exist_ok=True)

    def features(row):
        label_ids, decoder_ids = training_targets(processor.tokenizer, row)
        labels = torch.tensor(label_ids, dtype=torch.long)
        decoder = torch.tensor(decoder_ids, dtype=torch.long)
        path = cache / (feature_key(row) + ".pt")
        if path.exists():
            value = torch.load(path, map_location="cpu", weights_only=True)
            # Cached acoustics can be reused; always regenerate labels for this recipe.
            value["labels"] = labels
            value["decoder"] = decoder
            return value
        with sf.SoundFile(row["audio"]) as audio:
            start, end = audio_interval(row["start"], row["end"], audio.samplerate, len(audio))
            rate = audio.samplerate
            audio.seek(start)
            samples = audio.read(end-start, dtype="float32", always_2d=True)
        mono = samples.mean(axis=1)
        energies = np.mean(samples.astype(np.float64)**2, axis=0)
        strongest = int(np.argmax(energies))
        if float(np.mean(mono.astype(np.float64)**2)) < .25 * energies[strongest]:
            mono = samples[:, strongest]
        divisor = math.gcd(rate, 16000)
        mono = scipy.signal.resample_poly(mono, 16000//divisor, rate//divisor).astype(np.float32)
        if not np.isfinite(mono).all():
            raise ValueError("Audio contains non-finite samples")
        encoded = processor.feature_extractor(mono, sampling_rate=16000, return_tensors="pt", return_attention_mask=True)
        value = {"features": encoded.input_features[0].half(), "mask": encoded.attention_mask[0],
                 "labels": labels, "decoder": decoder}
        temporary = path.with_suffix(".tmp")
        torch.save(value, temporary)
        temporary.replace(path)
        return value

    update(phase="loading_model", train_examples=len(train_rows), validation_examples=len(validation_rows),
           evaluation_examples=len(evaluation), train_songs=len({r['song'] for r in train_rows}),
           validation_songs=len({r['song'] for r in validation_rows}), manifest_sha256=manifest_hash,
           base_model=args.model_id, base_revision=args.model_revision, planned_steps=args.steps,
           label_recipe=LABEL_RECIPE)
    base = WhisperForConditionalGeneration.from_pretrained(args.base_model, local_files_only=True,
        torch_dtype=dtype, attn_implementation="sdpa", low_cpu_mem_usage=True)
    base.config.use_cache = False
    base.config.apply_spec_augment = False
    base.generation_config.forced_decoder_ids = None
    if args.resume:
        model = PeftModel.from_pretrained(base, args.resume, is_trainable=True)
    else:
        config = LoraConfig(r=16, lora_alpha=32, lora_dropout=.05, bias="none",
            target_modules=r".*decoder\.layers\.\d+\.(self_attn|encoder_attn)\.(q_proj|v_proj)")
        model = get_peft_model(base, config)
    model.to("cuda")
    encoder = model.get_base_model().model.encoder
    if any(parameter.requires_grad for parameter in encoder.parameters()):
        raise RuntimeError("This memory-bounded pilot must keep the encoder frozen")
    parameters = [parameter for parameter in model.parameters() if parameter.requires_grad]
    if not parameters:
        raise RuntimeError("No adapter parameters are trainable")
    trainable = sum(parameter.numel() for parameter in parameters)
    update(trainable_parameters=trainable, total_parameters=sum(p.numel() for p in model.parameters()),
           gpu=torch.cuda.get_device_name(), precision=str(dtype))
    optimizer = torch.optim.AdamW(parameters, lr=args.learning_rate, weight_decay=.01)
    warmup = max(1, args.steps//20)
    scheduler = torch.optim.lr_scheduler.LambdaLR(optimizer, lambda step:
        min((step+1)/warmup, max(0.0, (args.steps-step)/max(1, args.steps-warmup))))
    step, micro_step, best_wer = 0, 0, float("inf")
    order = list(range(len(train_rows)))
    random.Random(42).shuffle(order)

    def evaluate(label):
        model.eval()
        references, predictions, loss_sum = [], [], 0.0
        details = []
        with torch.inference_mode():
            for index, row in enumerate(evaluation):
                value = features(row)
                mel = value["features"].unsqueeze(0).to("cuda", dtype=dtype)
                mask = value["mask"].unsqueeze(0).to("cuda")
                labels = value["labels"].unsqueeze(0).to("cuda")
                output = model(input_features=mel, attention_mask=mask, labels=labels,
                               decoder_input_ids=value["decoder"].unsqueeze(0).to("cuda"))
                loss_sum += float(output.loss)
                tokens = model.generate(input_features=mel, attention_mask=mask, language="en", task="transcribe",
                                        return_timestamps=False, max_new_tokens=128, num_beams=3, use_cache=True)
                predicted = processor.tokenizer.batch_decode(tokens, skip_special_tokens=True)[0]
                normalize = processor.tokenizer.normalize
                references.append(normalize(row["text"]))
                predictions.append(normalize(predicted))
                details.append({"song": row["song"], "start": row["start"], "end": row["end"],
                                "reference": row["text"], "prediction": predicted})
                if index % 8 == 0:
                    update(phase=label, evaluation_progress=index+1, evaluation_total=len(evaluation), step=step)
        stats = jiwer.process_words(references, predictions)
        metrics = dict(wer=stats.wer, substitutions=stats.substitutions, deletions=stats.deletions,
                       insertions=stats.insertions, reference_words=stats.hits+stats.substitutions+stats.deletions,
                       loss=loss_sum/len(evaluation), examples=len(evaluation), step=step)
        atomic_json(args.output / f"{label}.json", {"metrics": metrics, "predictions": details})
        model.train()
        encoder.eval()
        return metrics

    def checkpoint(name):
        path = args.output / f"checkpoint-{step:05d}"
        path.mkdir(exist_ok=True)
        model.save_pretrained(path, safe_serialization=True)
        torch.save({"optimizer": optimizer.state_dict(), "scheduler": scheduler.state_dict(),
                    "step": step, "micro_step": micro_step, "best_wer": best_wer,
                    "manifest_sha256": manifest_hash, "accumulation": args.accumulation,
                    "model_id": args.model_id, "model_revision": args.model_revision,
                    "label_recipe": LABEL_RECIPE,
                    "torch_rng": torch.get_rng_state(),
                    "cuda_rng": torch.cuda.get_rng_state_all()}, path / "training-state.pt")
        atomic_json(path / "checkpoint.json", {"step": step, "micro_step": micro_step,
            "base_model": args.model_id, "base_revision": args.model_revision, "manifest_sha256": manifest_hash,
            "label_recipe": LABEL_RECIPE})
        atomic_json(args.output / f"{name}-checkpoint.json", {"path": str(path), "step": step})
        return str(path)

    processor.save_pretrained(args.output / "processor")
    if args.resume:
        state = torch.load(args.resume / "training-state.pt", map_location="cpu", weights_only=True)
        if state["manifest_sha256"] != manifest_hash:
            raise ValueError("Cannot resume against a different dataset")
        if state.get("label_recipe") != LABEL_RECIPE:
            raise ValueError("Cannot resume a checkpoint trained with a different label recipe; start fresh")
        if state.get("accumulation") != args.accumulation or state.get("model_id") != args.model_id or state.get("model_revision") != args.model_revision:
            raise ValueError("Resume settings must match the original model and gradient accumulation")
        optimizer.load_state_dict(state["optimizer"])
        scheduler.load_state_dict(state["scheduler"])
        step, micro_step, best_wer = state["step"], state["micro_step"], state["best_wer"]
        torch.set_rng_state(state["torch_rng"])
        torch.cuda.set_rng_state_all(state["cuda_rng"])
    baseline_path = args.output / "baseline.json"
    if baseline_path.exists():
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))["metrics"]
    else:
        baseline = evaluate("baseline")
    update(baseline=baseline, phase="training", step=step)
    model.train()
    encoder.eval()
    optimizer.zero_grad(set_to_none=True)
    losses = []
    started = time.monotonic()
    current_epoch = -1
    while step < args.steps:
        if (args.output / "STOP").exists() and micro_step % args.accumulation == 0:
            update(phase="stopped", checkpoint=checkpoint("stopped"), step=step)
            return
        epoch = micro_step // len(train_rows)
        if epoch != current_epoch:
            order = list(range(len(train_rows)))
            random.Random(42+epoch).shuffle(order)
            current_epoch = epoch
        row = train_rows[order[micro_step % len(order)]]
        value = features(row)
        with torch.autocast("cuda", dtype=dtype):
            result = model(input_features=value["features"].unsqueeze(0).to("cuda", dtype=dtype),
                           attention_mask=value["mask"].unsqueeze(0).to("cuda"),
                           labels=value["labels"].unsqueeze(0).to("cuda"),
                           decoder_input_ids=value["decoder"].unsqueeze(0).to("cuda"))
            loss = result.loss
        if not torch.isfinite(loss):
            raise RuntimeError("Non-finite loss; refusing to continue")
        (loss / args.accumulation).backward()
        losses.append(float(loss.detach()))
        micro_step += 1
        if micro_step % args.accumulation:
            continue
        norm = torch.nn.utils.clip_grad_norm_(parameters, 1.0)
        if not torch.isfinite(norm):
            raise RuntimeError("Non-finite gradients; refusing to continue")
        optimizer.step()
        scheduler.step()
        optimizer.zero_grad(set_to_none=True)
        step += 1
        update(phase="training", step=step, micro_step=micro_step,
               train_loss=sum(losses)/len(losses), learning_rate=scheduler.get_last_lr()[0],
               epoch=round(micro_step/len(train_rows), 3), elapsed_training_seconds=round(time.monotonic()-started, 1),
               gpu_peak_gib=round(torch.cuda.max_memory_allocated()/2**30, 3))
        losses.clear()
        if step % args.checkpoint_steps == 0:
            update(checkpoint=checkpoint("latest"))
        if step % args.eval_steps == 0 or step == args.steps:
            metrics = evaluate(f"validation-{step:05d}")
            if metrics["wer"] < best_wer:
                best_wer = metrics["wer"]
                update(best_checkpoint=checkpoint("best"), best_validation=metrics)
            update(phase="training", validation=metrics)
    final = checkpoint("final")
    update(phase="completed", checkpoint=final, best_wer=best_wer,
           improved_over_base=best_wer < baseline["wer"],
           deployment="Not deployed. Requires evaluation against the Release model and dataset-use review.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--base-model", type=Path, required=True)
    parser.add_argument("--model-id", default=BASE_ID)
    parser.add_argument("--model-revision", default=BASE_REVISION)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--steps", type=int, default=600)
    parser.add_argument("--accumulation", type=int, default=8)
    parser.add_argument("--learning-rate", type=float, default=1e-4)
    parser.add_argument("--eval-samples", type=int, default=48)
    parser.add_argument("--eval-steps", type=int, default=100)
    parser.add_argument("--checkpoint-steps", type=int, default=25)
    parser.add_argument("--resume", type=Path)
    args = parser.parse_args()
    if min(args.steps, args.accumulation, args.eval_samples, args.eval_steps, args.checkpoint_steps) < 1:
        parser.error("Counts must be positive")
    args.output.mkdir(parents=True, exist_ok=True)
    import psutil
    status = args.output / "status.json"
    if status.exists():
        previous = json.loads(status.read_text(encoding="utf-8"))
        if psutil.pid_exists(previous.get("pid", -1)) and previous.get("phase") not in ("completed", "failed", "stopped"):
            raise RuntimeError("A training process is already using this output directory")
        if not args.resume:
            raise RuntimeError("Use a new output directory or explicitly resume an existing checkpoint")
    # The child owns its log handle, so it survives the terminal/launcher closing.
    log = (args.output / "training.log").open("a", encoding="utf-8", buffering=1)
    sys.stdout = sys.stderr = log
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    state = {"phase": "starting", "pid": os.getpid(), "started_at": time.strftime("%Y-%m-%dT%H:%M:%S%z")}
    def update(**values):
        state.update(values)
        state["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        atomic_json(args.output / "status.json", state)
        print(json.dumps(values, ensure_ascii=False, allow_nan=False), flush=True)
    update()
    try:
        run(args, update)
    except BaseException as error:
        traceback.print_exc()
        update(phase="failed", error=f"{type(error).__name__}: {error}")
        raise


if __name__ == "__main__":
    main()
