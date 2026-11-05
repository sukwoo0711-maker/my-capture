# Library performance probe

This bounded console probe measures existing `MyCapture.Core` APIs. It makes no
product changes and uses no private reflection or friend-assembly identity.
Run from the repository root, in Release, while other build/test workloads are idle:

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk\dotnet.exe'
& $dotnet build build/tools/LibraryPerformanceProbe/LibraryPerformanceProbe.csproj -c Release
& $dotnet run --project build/tools/LibraryPerformanceProbe/LibraryPerformanceProbe.csproj -c Release --no-build -- --output artifacts/library-performance/run-01
```

The output path must be new and below this repository's `artifacts` directory.
All synthetic fixtures, partial failure evidence, and `report.json` remain there.
The probe never cleans up files or opens an existing application data directory.
Use a new run name for another run. A failed operation or correctness assertion
returns exit code 1. No elapsed-time threshold decides pass/fail.

## Workloads and interpretation

- **Folder scopes:** 100, 500, and 1,000 folders arranged as independent chains
  of maximum depth 1 or 5. Alternate chains belong to different dates. Each case
  contains exactly 1,000 records; every tenth is unassigned, and the others use
  `folder[index % folderCount]`. A date scope and the second chain's root scope are
  measured independently. Counts and raw samples are in the report. `FoldersInScope`
  calls the private `FolderIdsInScope` traversal and then filters/materializes the
  returned folder list. Its timings therefore include that final pass; they are
  not isolated timings for the private helper. `RecordsInScope` is timed directly.
  Each case has 3 warmups excluded from statistics and 20 measured calls. Membership assertions run
  after the timed call; an independent construction-map oracle must contain both
  folders and records for every scope. A complete fixture fingerprint verifies
  read-only behavior.
- **Publication:** 300 and 1,000 records, with 0, 4 KiB, or 64 KiB of ASCII OCR text
  on every record. Input records share the same OCR string to bound fixture memory;
  JSON still serializes the text independently for every record. Public `Add`
  creates the in-memory fixture without repeated setup index writes. One target's
  title changes per publication. There is no folder state, queue backlog, OCR
  engine, media encoding, or image payload. The case has 1 warmup excluded from statistics and 5
  measured publications. Reservation occurs outside timing. Return latency ends
  as soon as `PublishRecordAsync` returns its `Task`; completion latency starts at
  the same timestamp and ends after that task successfully completes. The latter
  includes serializer, writer scheduling, and the existing flushed atomic writer.
  It is a completed API durability guarantee, not a power-loss experiment.
  Every result verifies every persisted index record, ordering, target metadata,
  and an unchanged source-state fingerprint.
- **Allocation/statistics:** scope allocation is current-calling-thread allocation.
  Publication records calling-thread allocation before return and process-wide
  managed allocation through completion; the latter includes worker allocations
  and runtime noise. Explicit full GC occurs between publication trials, outside
  timing. No allocation counter is peak live memory. JSON reports nearest-rank
  p50/p95/max and all raw samples. With only 5 publication samples, p95 equals max;
  these are diagnostic observations, not statistically established differences.
- **Write accounting/bounds:** after each successful publication, index and metadata
  file lengths are summed. The reported completed payload byte total includes
  warmup and repeated writes, even when an existing file is atomically replaced.
  These are actual completed JSON payload bytes, not device/NTFS physical I/O
  counters, write amplification, or retained disk usage. The current fixed matrix
  writes approximately 0.6 GB, capped at 768 MiB of completed publication payload.
  Final retained bytes are reported separately (including existing writer backups).
  Large cases run sequentially; there are no concurrent queued full-index writes.
  Expected live working data stays below roughly 1 GiB; cumulative managed
  allocation across all trials can exceed that amount. Fixture JSON checks are
  outside timing. No timing gates or performance improvement claims are made.

The runtime, OS, architecture, processor count, GC mode, and Core assembly identity
are recorded. Keep the source revision/build command with the report in the
development log. This probe does not measure WPF responsiveness, rendering,
capture hardware, GPU, OCR inference, or real-user library startup.

## CPU integration comparison mode

`--publication-cpu` reuses this public-API probe for the UTF-8 integration comparison.
The original mode and historical isolated experiment outputs remain available.
Freeze the baseline Core binary before changing source, then build this probe once:

```powershell
& (Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk\dotnet.exe') build build/tools/LibraryPerformanceProbe/LibraryPerformanceProbe.csproj -c Release --no-restore '-m:1' '-nr:false'
```

For this investigation the executed baseline Core is preserved at
`work/quality-implementation/storage-binaries/baseline-01/MyCapture.Core.dll`,
SHA-256 `528F2DB7A810257D63E3D557E796CB966DFF2667A18ACB2F1A1495E309F78662`.
Its manifest links the actual baseline TRX and source hashes. After a successful
candidate/probe build, create two **new** runtime directories under
`work/quality-implementation/storage-binaries`: `baseline-probe-01` and
`candidate-probe-01`. Copy the built probe output recursively to each, selecting
the preserved Core DLL for baseline and the built Core DLL for candidate before
copying that file. Keep the same probe DLL, dependencies, runtimeconfig and deps
files in both directories. Record both complete file manifests; refuse existing
destinations and preserve all outputs. This copies runtime binaries, not source
trees, and requires no friend access or production build flag.

Run from the repository root with the frozen Core hash appropriate to the runtime
directory, in the order A1 → B1 → B2 → A2 and with other builds/tests idle:

```powershell
& (Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk\dotnet.exe') work/quality-implementation/storage-binaries/baseline-probe-01/MyCapture.LibraryPerformanceProbe.dll --publication-cpu --output artifacts/library-cpu/integration-01/A1 --label A1 --core-sha256 528F2DB7A810257D63E3D557E796CB966DFF2667A18ACB2F1A1495E309F78662
```

The assembly file is `MyCapture.LibraryPerformanceProbe.dll` (the csproj's explicit
AssemblyName). Candidate runs use `candidate-probe-01`
and its recorded Core hash. Preserve an owned process receipt, stdout and stderr
for every block. The command checks the loaded Core file hash before allocating
fixtures and refuses an existing run directory.

The fixed initial matrix per process is:

| Records | ASCII OCR per record | Warmups | Measured |
| --- | --- | --- | --- |
| 2 | 0 | 20 | 2,000 |
| 2 | 4 KiB | 20 | 2,000 |
| 1,000 | 0 | 2 | 5 |
| 1,000 | 64 KiB | 2 | 5 |

The complete ABBA gives 4,000 small-input latency samples per arm/case and 10 per
arm/case for 1,000 records. Every repetition has the same deterministic payload.
One CPU interval surrounds the complete measured block, including reservation and
bounded sample bookkeeping. This avoids summing individually quantized CPU deltas.
Return/completion wall latency and caller allocation are recorded per operation.
Process allocation is measured over the block; natural GC is included. Forced GC
before the block and post-block GC are separate. Hashing and JSON reads occur
outside the CPU/latency block. Exact string-oracle current+backup bytes are checked
before and after each block; final records/order/metadata and input state are
validated. Earlier overwritten generations are represented by successful awaited
calls, not individual retained hash observations.

The campaign's exclusive append-only `cpu-budget.jsonl` reserves every warmup and
measured publication before it runs, with a 2 GiB logical JSON payload cap shared
across ABBA. Failed reservations remain charged. The initial matrix is expected
to consume about 2.0 GB; measured file lengths and fixed counts define the exact
total. Report/file metadata and filesystem write amplification are outside this
logical payload counter. The probe records `cpu-report.json`, `cpu-fixture.json`,
`cpu-case.json`, all samples, Core/probe hashes and runtime identity. Completion
means execution and correctness checks completed; the comparison decides budgets.

Keep the predeclared small-input CPU and return/completion p95 regression budgets
at +5%, and the 1,000-record completion p95 budget at +5%. Report AB and BA blocks
separately. Evaluate Windows process-time quantization conservatively from the
block CPU totals (the report records 15.625 ms); insufficient precision or order
effects require more controlled evidence. No budget is enlarged after observing
the result. This mode measures storage publication, not OCR inference or the
unreproduced user high-CPU episode.

## Accepted-index queue-load comparison

`--queue-load` measures the public `CaptureQueue.Load()` API with valid schema-2
indexes containing 0, 100, 1,000 and 5,000 deterministic image records. It creates
the inputs once, performs two warmups and then 20 measured calls on the same queue
per case. Fixed IDs, dates and synthetic metadata are identical between arms.
Each nonempty case has one valid 1×1 PNG shared by its records; this deliberately
measures parsing, sorting and warm cached existence checks without creating
thousands of media files. It does not represent cold disk or image decoding cost.
The empty case has no media file. All seven input files together are capped at
64 MiB before writing. Item and byte caps exceed the fixtures; retention is
disabled and any eviction fails the run. Inputs and partial outputs are retained.

Build the probe once, prepare new copied runtime directories with identical probe,
dependencies, deps and runtimeconfig files, and change only the pinned Core DLL.
Run the four blocks sequentially in A1 → B1 → B2 → A2 order while other measurement
work is idle. Use the recorded Core SHA-256 for each arm:

```powershell
& (Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk\dotnet.exe') <frozen-runtime>/MyCapture.LibraryPerformanceProbe.dll --queue-load --output artifacts/library-load/comparison-01/A1 --label A1 --core-sha256 <expected-core-sha256>
```

The command must run from the repository root, rejects existing output paths and
reparse-point ancestors, and verifies the loaded Core hash before creating inputs.
It records loaded Core/probe hashes and runtime identity in `queue-load-report.json`.
`queue-load-inputs.json` contains initial file fingerprints and expected metadata
hashes; `queue-load-r<count>.json` preserves every measured wall-time sample and
the final fingerprints. No input/output directory is deleted on completion/failure.

One process CPU interval covers the complete 20-call block, including bounded
timer/sample bookkeeping. Each wall sample covers only the public `Load()` call.
Process allocation and natural GC counts cover the block. Forced GC before timing
and the post-block GC CPU/wall tail are separate; post-block validation and file
hashing are outside both intervals. All IDs, order, serialized record metadata,
queue byte accounting and empty organization are checked after each warmup and
after the final measured call. Earlier measured generations are represented by
completed calls, not retained snapshots, to avoid changing their GC lifetimes.
The exact file set, lengths, SHA-256 and last-write timestamps must match before
and after each case and again after the full matrix. This detects changed retained
input bytes/paths/timestamps, rather than counting filesystem write system calls.

The predeclared small-input (0/100 records) regression budget is +5% for process CPU
and mean load wall time, used as a comparison diagnostic. Keep AB and BA results
separate and account conservatively for Windows process-time quantization (15.625 ms reported
per boundary). Twenty samples do not establish a p95 acceptance gate. Insufficient
CPU precision or inconsistent pair directions require more evidence; the probe
does not decide adoption or enlarge the budget. Exit 0 means all four cases of
20 calls and correctness checks in this process completed, not that the candidate
met the performance budget. Any setup/execution/invariant failure returns exit 1.

For the separate small-input precision follow-up, `--queue-load-small` keeps the
same fixture and correctness contracts but measures only 0 and 100 records. Each
case has 200 warmups; the measured batches are fixed at 20,000 loads for 0 records
and 2,000 for 100 records. Reports include the mode and actual per-case counts,
all individual wall samples, one CPU interval per batch and a separate GC tail.
The +5% CPU/mean-wall diagnostic budget and AB/BA checks remain unchanged.
The original `--queue-load` mode retains its 2-warmup/20-call four-case matrix.

The campaign-specific runner is
`work/quality-implementation/run-queue-load-small-abba.py`, with separate `prepare`
and `run` commands. It creates `baseline-load-small-24`/`candidate-load-small-24`
runtimes and `manifest-load-small-24.json` under `library-binaries`, and writes to
`artifacts/library-load/comparison-small-24`. The earlier campaign-22 runtimes,
manifest, runner and results are retained unchanged. Build and freeze the updated
probe before preparing these new runtimes.
