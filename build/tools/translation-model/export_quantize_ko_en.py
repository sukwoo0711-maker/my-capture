"""Reproduces the three int8 ONNX graphs of Helsinki-NLP/opus-mt-tc-big-ko-en (revision fa26583a...).

Environment used (CPU only):  Python 3.12, torch 2.7.1+cpu, transformers 4.52.4, optimum 1.27.0,
onnx 1.23.2, onnxruntime 1.30.0 (the version the app ships), sentencepiece 0.2.2, numpy 2.2.6.

    python -I -X utf8 export_quantize_ko_en.py --model <folder with the HF repo files> --work <empty folder>

Writes <work>/onnx_fp32/{encoder_model,decoder_model,decoder_with_past_model}.onnx (optimum export,
task text2text-generation-with-past, opset 14, fp32, no post-processing so the three graphs stay separate)
and <work>/onnx_int8/*_quantized.onnx (onnxruntime dynamic quantization, QInt8 weights, per tensor).
dedupe_opus_onnx_ko_en.py then expects those three files under <input>/onnx/ next to source.spm and vocab.json.
"""
import argparse
import subprocess
import sys
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", type=Path, required=True)
    ap.add_argument("--work", type=Path, required=True)
    a = ap.parse_args()
    fp32, int8 = a.work / "onnx_fp32", a.work / "onnx_int8"
    subprocess.run([sys.executable, "-I", "-X", "utf8", "-m", "optimum.commands.optimum_cli", "export", "onnx",
                    "--model", str(a.model), "--task", "text2text-generation-with-past", "--dtype", "fp32",
                    "--no-post-process", "--opset", "14", str(fp32)], check=True)
    from onnxruntime.quantization import QuantType, quantize_dynamic
    int8.mkdir(parents=True, exist_ok=True)
    for name in ("encoder_model", "decoder_model", "decoder_with_past_model"):
        quantize_dynamic(str(fp32 / f"{name}.onnx"), str(int8 / f"{name}_quantized.onnx"),
                         weight_type=QuantType.QInt8, per_channel=False)
        print("quantized", name)


if __name__ == "__main__":
    main()
