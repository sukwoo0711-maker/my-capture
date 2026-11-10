"""Create an opt-in, host-local runsettings file without changing the shell environment."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


def main() -> None:
    project = Path(__file__).resolve().parent
    repo = project.parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--hook", type=Path, default=project / "bin/Release/net10.0/MyCapture.TestHostDpiStartupHook.dll")
    parser.add_argument("--expected-monitor-dpi", type=int, default=120)
    args = parser.parse_args()
    hook = args.hook.resolve(strict=True)
    output = args.output.resolve()
    if not hook.is_file() or hook.name != "MyCapture.TestHostDpiStartupHook.dll":
        parser.error("--hook must identify the built startup hook DLL")
    if not output.is_relative_to(repo / "artifacts") or output.suffix != ".runsettings":
        parser.error("--output must be a new .runsettings file under this repository's artifacts directory")
    if not 96 <= args.expected_monitor_dpi <= 768:
        parser.error("--expected-monitor-dpi must be between 96 and 768")
    root = ET.Element("RunSettings")
    configuration = ET.SubElement(root, "RunConfiguration")
    environment = ET.SubElement(configuration, "EnvironmentVariables")
    for name, value in {
        "DOTNET_STARTUP_HOOKS": str(hook),
        "MYCAPTURE_TESTHOST_DPI_MODE": "PerMonitorV2",
        "MYCAPTURE_TESTHOST_DPI_EXPECTED_MONITOR": str(args.expected_monitor_dpi),
    }.items():
        ET.SubElement(environment, name).text = value
    ET.indent(root, space="  ")
    data = ET.tostring(root, encoding="utf-8", xml_declaration=True) + b"\n"
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(data)
    print(json.dumps({
        "runsettings": str(output), "sha256": hashlib.sha256(data).hexdigest(),
        "hook": str(hook), "hookSha256": hashlib.sha256(hook.read_bytes()).hexdigest(),
        "expectedMonitorDpi": args.expected_monitor_dpi,
    }, indent=2))


if __name__ == "__main__":
    main()
