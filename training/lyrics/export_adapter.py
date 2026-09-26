"""Merge a research adapter and convert it for offline application-engine testing."""
import argparse
import gc
import hashlib
import json
import os
from pathlib import Path

from train_adapter import atomic_json, BASE_ID, BASE_REVISION


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--base-model", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    artifacts = Path(__file__).resolve().parents[2] / "artifacts"
    if not output.is_relative_to(artifacts) or output == artifacts:
        raise ValueError("Research exports must stay under artifacts, separate from the Release engine")
    if output.exists():
        raise ValueError("Export destination already exists; preserve it and use a new path")
    metadata = json.loads((args.checkpoint / "checkpoint.json").read_text(encoding="utf-8"))
    if metadata["base_model"] != BASE_ID or metadata["base_revision"] != BASE_REVISION:
        raise ValueError("Checkpoint uses a different base model")
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    import torch
    from transformers import WhisperForConditionalGeneration, WhisperProcessor
    from peft import PeftModel
    from ctranslate2.converters import TransformersConverter
    torch.set_num_threads(4)
    output.mkdir(parents=True)
    print("Merging adapter on CPU for research evaluation.", flush=True)
    # Merge in FP32, then cast to the inference engine's FP16 format.
    # This process never allocates CUDA tensors or changes the source checkpoint.
    base = WhisperForConditionalGeneration.from_pretrained(args.base_model, local_files_only=True,
        torch_dtype=torch.float32, low_cpu_mem_usage=True)
    model = PeftModel.from_pretrained(base, args.checkpoint)
    merged = model.merge_and_unload(safe_merge=True).half()
    merged.config.use_cache = True
    hf_path = output / "merged-hf"
    merged.save_pretrained(hf_path, safe_serialization=True)
    WhisperProcessor.from_pretrained(args.base_model, local_files_only=True).save_pretrained(hf_path)
    del merged, model, base
    gc.collect()
    print("Converting the merged model to the application engine's format.", flush=True)
    converter = TransformersConverter(str(hf_path), load_as_float16=True, low_cpu_mem_usage=True,
                                      copy_files=["tokenizer.json", "preprocessor_config.json"])
    converter.convert(str(output / "ctranslate2"), quantization="float16")
    adapter = args.checkpoint / "adapter_model.safetensors"
    atomic_json(output / "export.json", {
        **metadata, "checkpoint": str(args.checkpoint.resolve()),
        "adapter_sha256": hashlib.sha256(adapter.read_bytes()).hexdigest(),
        "quantization": "float16", "merge_precision": "float32",
        "usage": "Local research evaluation only. Not installed in Release or approved for redistribution."})
    print("Research export ready.", flush=True)


if __name__ == "__main__":
    main()
