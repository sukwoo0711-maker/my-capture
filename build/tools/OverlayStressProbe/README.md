# Capture overlay overload probe

This standalone Windows/WPF probe loads a published MyCapture DLL without starting
the product App, reading a user profile, acquiring the screen, moving the cursor,
using the clipboard or committing a capture. It closes only its own overlay HWND.
All desktop pixels and window candidates are synthetic.

```powershell
dotnet build build/tools/OverlayStressProbe/OverlayStress.csproj -c Release -v q -nologo
powershell.exe -NoProfile -NonInteractive -File build/tools/OverlayStressProbe/Run.ps1 -Case native-async-burst -TargetDll C:/absolute/publish/MyCapture.dll -Label baseline
```

The runner creates unique JSON, process receipt and runtimeconfig files under
`artifacts/overlay-stress` (or `-OutputDirectory`). It preserves all output and pins
both Core and Desktop runtimes to `-RuntimeVersion` (default `10.0.11`) with roll
forward disabled. Install matching runtimes separately if required. It does not
change execution policy or install software. Runtime pinning is deliberately not
a global MSBuild RuntimeFrameworkVersion property, which also affects Windows SDK
reference packages.

Use the same probe, runtime, display environment and workload for both targets.
Run one performance process at a time, apart from source inspection. Every child
has a default 60-second limit (maximum configurable 120 seconds) and a 2-GiB private
memory guard. A guard stops only the exact process the runner started and exits
124; retain its receipt as an incomplete workload, rather than a timing result.
Runner exceptions propagate a nonzero PowerShell exit separately; the finalizer
still stops only its owned child and records whatever exit evidence is available.

Cases:

- `native-async-normal`: 240 independent input batches per hover/drag phase, with
  8-ms ThreadPool delay and dispatcher backpressure. Records pointer-handler cost,
  dispatcher queue delay, unique WPF Rendering times, allocation and process CPU.
- `native-async-burst`: the same producer, with 32 pointer changes per batch
  (7,680 per phase). Requires the final input position to have been drawn.
- `native-normal`, `native-burst`, `native-many-windows`: diagnostic callback-driven
  producers, the last with 10,000 candidate windows. These inject input inside the
  Rendering observer and therefore couple the input producer to the scheduler's
  subscription order. Do not interpret their feedback/callback ratio as physical
  input loss or frame rate; prefer the independent producer above.
- `raster-4k`, `raster-8k`: full-size offscreen software RenderTargetBitmap draws,
  magnifier on/off and hover/drag, with 200 input changes and 10 raster samples per
  condition. Static layer counts expose accidental full background rebuilding.

Rendering callbacks are not DWM presentation. Dispatcher delivery is not OS
mouse-to-photon latency. Offscreen software raster cost is not the native GPU path.
The reflection calls, progress collection, synthetic pixels and producer overhead
are part of the documented workload. The runner records product/probe/runtime
hashes, actual command/cwd/PID/exit code, elapsed time, memory and raw output.
