"""Run and inventory all required live OCR tests; preserve logs and reject missing evidence."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


EXPECTED = {
    "RealKoreanModel_InitializesSession",
    "RealKoreanModel_WritesReceiptProbeReport",
    "RealKoreanModel_ReadsPrintedReceiptWords",
    "RealKoreanModel_LeavesAfterTheIdleSpellAndReadsAgain",
    "RealKoreanModel_ReadsACaptureWhoseFourthByteIsZero",
}
TEST_CLASS = "MyCapture.App.Tests.NeuralOcrEngineLiveTests"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
SOURCE_INPUTS = [
    "build/validate-ocr-live.py",
    "tests/MyCapture.App.Tests/NeuralOcrLiveFactAttribute.cs",
    "tests/MyCapture.App.Tests/NeuralOcrEngineLiveTests.cs",
    "tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj",
    "src/MyCapture.Ocr/OcrModelCatalog.cs",
]


def sha256(path):
    with path.open("rb") as stream:
        digest = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
        return digest.hexdigest()


def build_evidence(root):
    directory = root / "tests/MyCapture.App.Tests/bin/Release/net10.0-windows10.0.22000.0"
    names = ["MyCapture.App.Tests.dll", "MyCapture.dll", "MyCapture.Core.dll", "MyCapture.Platform.dll",
             "MyCapture.Ocr.dll", "MyCapture.App.Tests.deps.json", "MyCapture.App.Tests.runtimeconfig.json"]
    return [{"path": str(directory / name), "sha256": sha256(directory / name)} for name in names]


def inspect_results(path):
    tree = ET.parse(path)
    tests = {}
    for unit in tree.findall(".//t:UnitTest", NS):
        method = unit.find("t:TestMethod", NS)
        if method is not None:
            tests[unit.attrib["id"]] = (method.attrib["className"].split(",")[0], method.attrib["name"])
    observed = []
    errors = []
    for result in tree.findall(".//t:UnitTestResult", NS):
        cls, name = tests.get(result.attrib["testId"], (None, None))
        output = result.findtext("t:Output/t:StdOut", default="", namespaces=NS)
        row = {"class": cls, "name": name, "outcome": result.attrib["outcome"],
               "duration": result.attrib.get("duration"),
               "model_evidence": [line.strip() for line in output.splitlines() if "Verified OCR model:" in line]}
        observed.append(row)
        if cls != TEST_CLASS or name not in EXPECTED or row["outcome"] != "Passed":
            errors.append(f"Unexpected or unsuccessful test: {cls}.{name}: {row['outcome']}")
        catalog = re.findall(r"Required OCR models: catalog=([^,\r\n]+), files=([^\r\n]+)", output)
        verified = re.findall(r"Verified OCR model: catalog=([^,\r\n]+), file=([^,\r\n]+), bytes=([0-9]+), sha256=([A-Fa-f0-9]{64})", output)
        if len(catalog) != 1 or not catalog[0][1] or not set(catalog[0][1].split(";")).issubset(
                {name for version, name, size, digest in verified if version == catalog[0][0] and int(size) > 0}):
            errors.append(f"Missing required model hash evidence: {name}")
    if len(observed) != len(EXPECTED) or {r["name"] for r in observed} != EXPECTED:
        errors.append("Executed test inventory does not match the five required live OCR tests.")
    counters = tree.find("t:ResultSummary/t:Counters", NS)
    if counters is None or any(int(counters.attrib.get(key, -1)) != len(EXPECTED)
                               for key in ("total", "executed", "passed")):
        errors.append("TRX counters do not confirm five executed, passed tests.")
    summary = tree.find("t:ResultSummary", NS)
    if summary is None or summary.attrib.get("outcome") != "Completed":
        errors.append("Test run did not complete successfully.")
    return observed, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    dotnet, models, output = (p.resolve() for p in (args.dotnet, args.models, args.output))
    if not dotnet.is_file():
        parser.error("--dotnet must identify the installed dotnet executable")
    if output.exists():
        parser.error("--output must be a new directory; existing evidence is preserved")
    output.mkdir(parents=True)
    record = {"started_utc": datetime.now(timezone.utc).isoformat(), "cwd": str(root),
              "models": str(models), "status": "RUNNING", "expected_tests": sorted(EXPECTED)}
    receipt = output / "validation.json"
    receipt.write_text(json.dumps(record, indent=2), encoding="utf-8")
    errors = []
    try:
        record["build_files"] = build_evidence(root)
        record["source_head"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
        record["source_status"] = subprocess.check_output(["git", "status", "--porcelain"], cwd=root, text=True)
        record["source_diff_sha256"] = hashlib.sha256(subprocess.check_output(["git", "diff", "HEAD"], cwd=root)).hexdigest()
        record["validation_source_files"] = [{"path": path, "sha256": sha256(root / path)} for path in SOURCE_INPUTS]
    except (OSError, subprocess.CalledProcessError) as error:
        errors.append(f"Build/source evidence unavailable: {error}")
    # A missing folder fails before execution, never becomes an empty passing suite.
    if not models.is_dir():
        errors.append("Model directory does not exist; live OCR validation was not run.")
        record["exit_code"] = None
    elif not errors:
        env = os.environ.copy()
        env.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                   DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="1",
                   MYCAPTURE_OCR_LIVE_MODELS=str(models), MYCAPTURE_REQUIRE_OCR_LIVE="1")
        env["PATH"] = str(dotnet.parent) + os.pathsep + env.get("PATH", "")
        command = [str(dotnet), "test", "tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj",
                   "-c", "Release", "--no-build", "--filter", "FullyQualifiedName~" + TEST_CLASS,
                   "--logger", "trx;LogFileName=ocr-live.trx", "--results-directory", str(output)]
        record.update(command=command, dotnet_sha256=sha256(dotnet))
        started = time.monotonic()
        record["exit_code"] = None
        record["process_exit_confirmed"] = False
        try:
            with (output / "execution.log").open("xb") as log:
                process = subprocess.Popen(command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
                                           creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                record["pid"] = process.pid
                receipt.write_text(json.dumps(record, indent=2), encoding="utf-8")
                try:
                    record["exit_code"] = process.wait(timeout=600)
                except subprocess.TimeoutExpired:
                    errors.append("Live OCR test process exceeded 600 seconds.")
                    # The live Popen handle owns this PID; only its test process tree is stopped.
                    try:
                        if os.name == "nt":
                            stopped = subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                                                     stdout=log, stderr=subprocess.STDOUT, timeout=30)
                            record["timeout_stop_exit_code"] = stopped.returncode
                        else:
                            process.kill()
                    except (OSError, subprocess.SubprocessError) as error:
                        errors.append(f"Owned test process stop could not be confirmed: {error}")
                    try:
                        record["exit_code"] = process.wait(timeout=30)
                    except (OSError, subprocess.SubprocessError) as error:
                        errors.append(f"Owned test process exit remains unconfirmed: {error}")
                record["process_exit_confirmed"] = record["exit_code"] is not None
        except (OSError, subprocess.SubprocessError) as error:
            errors.append(f"Test process execution failed: {error}")
        record["elapsed_seconds"] = time.monotonic() - started
        if record["exit_code"] != 0:
            errors.append(f"dotnet test failed: exit {record['exit_code']}")
        try:
            record["tests"], inventory_errors = inspect_results(output / "ocr-live.trx")
            errors.extend(inventory_errors)
        except (OSError, ET.ParseError, KeyError, ValueError) as error:
            errors.append(f"Missing or invalid test evidence: {error}")
        try:
            if build_evidence(root) != record["build_files"]:
                errors.append("Test/product binaries changed during validation.")
            if [{"path": path, "sha256": sha256(root / path)} for path in SOURCE_INPUTS] != record["validation_source_files"]:
                errors.append("Validation source files changed during execution.")
        except OSError as error:
            errors.append(f"Cannot revalidate build evidence: {error}")
    record.update(status="FAIL" if errors else "PASS", errors=errors,
                  finished_utc=datetime.now(timezone.utc).isoformat())
    receipt.write_text(json.dumps(record, indent=2), encoding="utf-8")
    print(json.dumps(record, indent=2))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
