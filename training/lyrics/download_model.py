import argparse
from pathlib import Path
from huggingface_hub import snapshot_download

parser = argparse.ArgumentParser()
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--model-id", default="openai/whisper-large-v3")
parser.add_argument("--revision", default="06f233fe06e710322aca913c1bc4249a0d71fce1")
args = parser.parse_args()
snapshot_download(args.model_id,
                  revision=args.revision,
                  allow_patterns=["*.json", "*.txt", "*.model", "model.safetensors", "README.md", "LICENSE*"],
                  local_dir=args.output)
print("Trainable base model downloaded.", flush=True)
