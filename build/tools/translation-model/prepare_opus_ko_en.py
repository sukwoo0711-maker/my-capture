"""Prepare the unchanged, pinned OPUS-MT ko-en model for an isolated preview profile.

Python standard library only. No upload, deletion, model conversion, or profile discovery.
    python -I -X utf8 prepare_opus_ko_en.py --output <model-folder>
    python -I -X utf8 prepare_opus_ko_en.py --output <model-folder> --verify-only
Existing files are never overwritten. An incomplete/mismatched file stops preparation and
is preserved for investigation; this tool does not remove it or bypass integrity checks.
"""
import argparse
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

ONNX = "https://huggingface.co/Xenova/opus-mt-ko-en/resolve/d9fa1ac6008242100fecb4fff9b0a5917a1be81f/"
TOKENIZER = "https://huggingface.co/Helsinki-NLP/opus-mt-ko-en/resolve/e42d1f41b66194e6d10512f8a27bebc1f4f5097e/"
FILES = [
    ("opus-mt-ko-en-encoder.onnx", ONNX + "onnx/encoder_model_quantized.onnx", 52899741,
     "210E1D2CB5B457CDEEFFE288099CC9D1DF9F13C35DC3D39FDE4BA252AF877380"),
    ("opus-mt-ko-en-decoder.onnx", ONNX + "onnx/decoder_model_quantized.onnx", 59842101,
     "0C961EA33A62EA84E73C9EC94C0737E65D06B5F73A7D0398B018FBBB7262D929"),
    ("opus-mt-ko-en-decoder-with-past.onnx", ONNX + "onnx/decoder_with_past_model_quantized.onnx", 56570637,
     "196BD1705AE0EAC256F1A9AB8C82C243DECDAD124C5ACE67FE073D9E0FC6956E"),
    ("opus-mt-ko-en-source.spm", TOKENIZER + "source.spm", 841805,
     "C9496F7C2BE9AECB84C751AE9F35A875915DDE8E3892F652A5C76811AB2A0F49"),
    ("opus-mt-ko-en-vocab.json", TOKENIZER + "vocab.json", 1719866,
     "D42E3838E7ED0E43785FA013EF22AB4D382248CDF4E959C46A2B48DB5EB019E9"),
]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    target = args.output.resolve()
    if not args.verify_only:
        target.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, url, expected_bytes, expected_hash in FILES:
        path = target / name
        if not path.exists():
            if args.verify_only:
                raise FileNotFoundError(path)
            with urlopen(url, timeout=60) as response, path.open("xb") as output:
                received = 0
                while block := response.read(1024 * 1024):
                    received += len(block)
                    if received > expected_bytes:
                        raise ValueError(f"Response exceeds pinned size: {name}")
                    output.write(block)
        with path.open("rb") as model:
            actual_hash = hashlib.file_digest(model, "sha256").hexdigest().upper()
        actual_bytes = path.stat().st_size
        if actual_bytes != expected_bytes or actual_hash != expected_hash:
            raise ValueError(f"Pinned integrity mismatch; preserving {path}: {actual_bytes}, {actual_hash}")
        manifest.append(dict(name=name, url=url, bytes=actual_bytes, sha256=actual_hash))
        print(f"VERIFIED {name}: {actual_bytes} bytes {actual_hash}", flush=True)
    evidence = dict(model="Helsinki-NLP/opus-mt-ko-en", license="Apache-2.0",
                    totalBytes=sum(item["bytes"] for item in manifest), files=manifest)
    manifest_path = target / "verified-model-manifest.json"
    if not args.verify_only and not manifest_path.exists():
        with manifest_path.open("x", encoding="utf-8") as output:
            json.dump(evidence, output, indent=2)
            output.write("\n")
    print(f"PASS: {len(manifest)} files, {evidence['totalBytes']} bytes; verify_only={args.verify_only}")

if __name__ == "__main__":
    main()
