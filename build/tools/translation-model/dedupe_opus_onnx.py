"""Moves the large weights of the OPUS-MT en-ko ONNX graphs into shared external-data files.

The encoder, decoder and decoder-with-past graphs each embed their own copy of most weights.
Every top-level initializer with at least 4096 bytes of raw_data is stored once (de-duplicated by
data type, dims and bytes) in opus-mt-en-ko-weights-<n>.bin and the graph points at it through
ONNX external data. Tensor values, nodes and metadata are not changed. The output is deterministic.

Python 3.12, standard library only:

    python -I -X utf8 dedupe_opus_onnx.py --source <dir> --output <dir>

--source holds onnx/{encoder,decoder,decoder_with_past}_model_quantized.onnx, source.spm and
vocab.json of the pinned revisions below (checked by sha256). --output must be empty or absent.
"""

import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

PREFIX = "opus-mt-en-ko"
MIN_BYTES = 4096  # raw_data below this stays inline
LARGE_BYTES = 1 << 20  # from here a tensor starts on a 4096-byte boundary, else on a 64-byte one
MAX_FILE = 64 << 20  # size limit of one weights file

ONNX_BASE = (
    "https://huggingface.co/noticemkjung/opus-mt-tc-big-en-ko-ONNX/resolve/"
    "aea67600956847d47609f96b364f33a734007d50/"
)
MODEL_BASE = (
    "https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko/resolve/"
    "ae8606b7b29a495f31ce679cee2007f536a3a5ce/"
)
# (path below --source, output name, URL prefix, sha256), in processing order.
GRAPHS = [
    ("onnx/encoder_model_quantized.onnx", f"{PREFIX}-encoder.onnx", ONNX_BASE,
     "2056A550237F923C9AACA2FBBCF8C623D69E0EDD3E06780B86E09757ABB00F43"),
    ("onnx/decoder_model_quantized.onnx", f"{PREFIX}-decoder.onnx", ONNX_BASE,
     "292E6A8CDF39A17EA509F5B92D45F77284366C57EE111F0E716504AF63DCA4C1"),
    ("onnx/decoder_with_past_model_quantized.onnx", f"{PREFIX}-decoder-with-past.onnx", ONNX_BASE,
     "6B5321765CB284CAF39CEEE468F623AE2F52250A6C00DB40D178417B57C3E784"),
]
COPIES = [  # copied unchanged
    ("source.spm", f"{PREFIX}-source.spm", MODEL_BASE,
     "3D0591E65C49541D82F48DF33D7B322C3D4EE7AA0EE8747F9A7F9355DBF22C95"),
    ("vocab.json", f"{PREFIX}-vocab.json", MODEL_BASE,
     "9E03014979D240F6A6D1C5DC53CD14F4841351326D345D437D48D115EFF67B5F"),
]


# --- protobuf wire format: just enough to find and rewrite initializers -------------------------

def read_varint(buf, pos):
    result = shift = 0
    while True:
        byte = buf[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if byte < 0x80:
            return result, pos
        shift += 7


def put_varint(value):
    out = bytearray()
    while value > 0x7F:
        out.append(value & 0x7F | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def field(number, payload):
    """Encodes a length-delimited field."""
    return put_varint(number << 3 | 2) + put_varint(len(payload)) + bytes(payload)


def fields(buf):
    """Yields (number, wire type, value, start, end) per field; value is an int or a memoryview."""
    pos, end = 0, len(buf)
    while pos < end:
        start = pos
        tag, pos = read_varint(buf, pos)
        wire = tag & 7
        if wire == 0:
            value, pos = read_varint(buf, pos)
        elif wire == 2:
            size, pos = read_varint(buf, pos)
            value, pos = buf[pos:pos + size], pos + size
        elif wire in (1, 5):  # fixed64, fixed32
            size = 8 if wire == 1 else 4
            value, pos = buf[pos:pos + size], pos + size
        else:
            raise ValueError(f"unsupported wire type {wire}")
        if pos > end:
            raise ValueError("truncated protobuf message")
        yield tag >> 3, wire, value, start, pos


def to_external(tensor, place):
    """TensorProto bytes with raw_data swapped for an external-data reference; None keeps it."""
    dims, dtype, raw, external, kept = [], 0, None, False, []
    for number, wire, value, start, end in fields(tensor):
        if number == 9:  # raw_data
            raw = value
        elif number in (13, 14):  # external_data, data_location: rebuilt below
            external = external or number == 13 or value == 1
        else:
            kept.append(tensor[start:end])
            if number == 2:
                dtype = value
            elif number == 1 and wire == 0:
                dims.append(value)
            elif number == 1:  # packed dims
                pos = 0
                while pos < len(value):
                    dim, pos = read_varint(value, pos)
                    dims.append(dim)
    if raw is None or len(raw) < MIN_BYTES or external:
        return None
    name, offset = place(dtype, dims, raw)
    entries = (("location", name), ("offset", str(offset)), ("length", str(len(raw))))
    refs = [field(13, field(1, k.encode()) + field(2, v.encode())) for k, v in entries]
    return b"".join(kept + refs) + b"\x70\x01"  # data_location = EXTERNAL


def rewrite_model(model, place):
    """Returns (model bytes, tensors moved); only the graph's top-level initializers change."""
    for number, _, graph, start, end in fields(model):
        if number == 7:  # ModelProto.graph
            break
    else:
        raise ValueError("model has no graph")
    new_graph, moved = bytearray(), 0
    for number, _, item, s, e in fields(graph):
        new = to_external(item, place) if number == 5 else None  # GraphProto.initializer
        if new is None:
            new_graph += graph[s:e]
        else:
            new_graph += field(5, new)
            moved += 1
    return b"".join((model[:start], field(7, new_graph), model[end:])), moved


# --- weights files ------------------------------------------------------------------------------

class Weights:
    """Appends distinct blobs to weights files in first-seen order and remembers where each went."""

    def __init__(self, directory):
        self.directory, self.where, self.names = directory, {}, []
        self.handle, self.size = None, 0

    def place(self, dtype, dims, raw):
        digest = hashlib.sha256(struct.pack(f"<ii{len(dims)}q", dtype, len(dims), *dims))
        digest.update(raw)
        key = digest.digest()
        if key not in self.where:
            self.where[key] = self.append(raw)
        return self.where[key]

    def append(self, raw):
        if len(raw) > MAX_FILE:
            raise ValueError(f"tensor of {len(raw)} bytes exceeds the weights file limit")
        align = 4096 if len(raw) >= LARGE_BYTES else 64
        offset = -(-self.size // align) * align
        if self.handle is None or offset + len(raw) > MAX_FILE:  # next-fit: open the next file
            self.close()
            self.names.append(f"{PREFIX}-weights-{len(self.names) + 1}.bin")
            self.handle, self.size, offset = (self.directory / self.names[-1]).open("wb"), 0, 0
        self.handle.write(bytes(offset - self.size))
        self.handle.write(raw)
        self.size = offset + len(raw)
        return self.names[-1], offset

    def close(self):
        if self.handle is not None:
            self.handle.close()
            self.handle = None


def describe(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1 << 20):
            digest.update(chunk)
    return {"name": path.name, "bytes": path.stat().st_size, "sha256": digest.hexdigest().upper()}


def run(source, out):
    if out.exists() and any(out.iterdir()):
        sys.exit(f"{out} is not empty")
    sources = []  # verify every input before writing anything
    for path, _, base, sha256 in GRAPHS + COPIES:
        entry = describe(source / path)
        if entry["sha256"] != sha256:
            sys.exit(f"{source / path}: sha256 {entry['sha256']} is not the pinned {sha256}")
        sources.append({**entry, "name": path, "url": base + path})
    out.mkdir(parents=True, exist_ok=True)
    weights, moved_total = Weights(out), 0
    for path, name, _, _ in GRAPHS:
        model, moved = rewrite_model(memoryview((source / path).read_bytes()), weights.place)
        (out / name).write_bytes(model)
        moved_total += moved
        print(f"{name}: {moved} tensors moved, graph {len(model):,} bytes")
    weights.close()
    for path, name, _, _ in COPIES:
        (out / name).write_bytes((source / path).read_bytes())
    names = [g[1] for g in GRAPHS] + weights.names + [c[1] for c in COPIES]
    files = [describe(out / name) for name in names]
    manifest = {
        "model": "Helsinki-NLP/opus-mt-tc-big-en-ko, int8 ONNX, weights de-duplicated",
        "license": "CC-BY-4.0",
        "files": files,
        "totals": {
            "files": len(files),
            "bytes": sum(f["bytes"] for f in files),
            "sourceBytes": sum(s["bytes"] for s in sources),
            "weightsFiles": len(weights.names),
            "externalTensors": moved_total,
            "uniqueBlobs": len(weights.where),
        },
        "sources": sources,
    }
    (out / "manifest.json").write_bytes(json.dumps(manifest, indent=2).encode() + b"\n")
    totals = manifest["totals"]
    print(f"{len(weights.names)} weights files: {moved_total} tensors, {len(weights.where)} blobs")
    print(f"total {totals['bytes']:,} bytes (sources {totals['sourceBytes']:,})")


def main():
    parser = argparse.ArgumentParser(description="De-duplicate the OPUS-MT en-ko ONNX weights.")
    parser.add_argument("--source", type=Path, required=True, help="input folder")
    parser.add_argument("--output", type=Path, required=True, help="empty or new output folder")
    args = parser.parse_args()
    try:
        run(args.source, args.output)
    except (OSError, ValueError, IndexError, struct.error) as error:
        sys.exit(f"error: {error}")


if __name__ == "__main__":
    main()
