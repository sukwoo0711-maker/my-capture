# Export + import + tags + search integration merge — development LOG

Date: 2026-10-14
Working directory for every command: `C:\GitRepositories\my-capture-bench\integration`
HEAD: `09c7aec` (integration including import, tags and search).
MERGE_HEAD: `ed2681106e18b3e0b435ba7b3c013b57fa8a1f74` (export).
Git common directory: `C:/GitRepositories/my-capture/.git`.

## Resolution

- `Strings.resx` and `Strings.en.resx`: retain HEAD's search/tag/import blocks and all 27 incoming `ExportOptions.*` entries. Remove only the three conflict delimiters in each file. Both catalogs contain 1,996 unique data names and parse as XML.
- Three-way comparison against the merge base verifies the complete union of entries, including values and attributes. Retain the search lane's `LibraryUx_SearchPlaceholder` update because incoming still has the base value. Preserve incoming's cleanly merged `ExportReduction_Title`, `ExportReduction_Description`, and `Library.Archive.Hint` updates.
- Reviewed export settings propagation, gallery selection/folder export handlers, image conversion/URL policy, and import publication. The incoming handlers retain the existing search/tag/import wiring.

## Additional integration fix

The new PNG/JPEG integration regression initially failed in all four cases: newly imported images had `original.png` and a thumbnail, but gallery export plans require `rendered.png`. `GalleryImportService.PrepareAsync` now copies the normalized original PNG to the initial rendered PNG before publication. The imported source bytes stay intact, JPEG retains its separate original, and the import service's existing total-byte calculation includes the new rendered copy.

`GalleryImportTests.TaggedSearchResultsExportImportedImagesWithoutChangingOriginals` covers PNG and JPEG, each with source-URL inclusion enabled and disabled. It imports actual images, applies a tag, filters with positive and negative field conditions, builds an export plan from the visible result, exports at an 800-pixel long edge, verifies dimensions and URL policy, verifies external/original/rendered bytes remain unchanged by export, and confirms tags and duplicate detection still work.

## Verification commands

Executed directly in the working directory above; results were collected from completed tool calls. No application launch or deployment occurred.

Build command:

```text
dotnet build MyCapture.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```

Focused test command:

```text
dotnet test MyCapture.slnx -c Release --no-build -m:1 -nr:false --filter "FullyQualifiedName~ImageExportPolicyTests|FullyQualifiedName~ImageExportEncoderTests|FullyQualifiedName~ImageReductionDialogTests|FullyQualifiedName~ImageExportOptionsTests|FullyQualifiedName~GalleryImportTests|FullyQualifiedName~CaptureImportTests|FullyQualifiedName~GallerySearchRefinementTests|FullyQualifiedName~CaptureSearchQueryTests|FullyQualifiedName~GalleryTagTests|FullyQualifiedName~CaptureTagTests"
```

Localization command:

```text
dotnet test MyCapture.slnx -c Release --no-build -m:1 -nr:false --filter "FullyQualifiedName~BothCatalogsContainEveryKeyAndMatchingValidCompositeFormats"
```

| Command / run | Exit code | Result and evidence |
| --- | --- | --- |
| Build before import fix | 0 | PASS; 0 warnings, 0 errors; session 19865 completed in 28.33 seconds. |
| Focused tests before import fix | 1 | FAIL; App 59 passed / 4 failed, Core 97 passed. All failures were the new regression: missing rendered.png. |
| Build after import fix | 0 | PASS; 0 warnings, 0 errors; session 66234 completed in 17.20 seconds. |
| Focused tests after import fix | 0 | PASS; App 63/63, Core 97/97; zero failed or skipped. |
| Localization command | 0 | PASS; Core 1/1; App contains no matching test. |
| Three-way catalog validation below | 0 | PASS; both XML catalogs have 1,996 unique entries and preserve the full three-way union. |
| Conflict marker scan below | 1 | PASS; git grep exit 1 means no matching conflict markers. |
| Whitespace validation below | 0 | PASS; no whitespace errors. |

## Catalog and final-state validation

Git reads after initial discovery use `GIT_CONFIG_GLOBAL=NUL`, `GIT_CONFIG_NOSYSTEM=1`, and an empty `core.excludesFile` to avoid profile configuration/ignore lookups. `core.autocrlf=true` is supplied per command for Windows working-tree comparisons; no Git configuration is written.

```powershell
$ErrorActionPreference='Stop'
$env:GIT_CONFIG_GLOBAL='NUL'
$env:GIT_CONFIG_NOSYSTEM='1'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$base=git -c core.excludesFile= merge-base HEAD MERGE_HEAD
if ($LASTEXITCODE -ne 0) { throw 'Merge base lookup failed' }
foreach ($file in @('src/MyCapture.Core/Localization/Strings.resx','src/MyCapture.Core/Localization/Strings.en.resx')) {
  $entries=@{}
  $xml=[xml][IO.File]::ReadAllText((Join-Path (Get-Location) $file))
  foreach ($entry in $xml.root.data) {
    if ($entries.ContainsKey($entry.name)) { throw "Duplicate: $($entry.name)" }
    $entries[$entry.name]=$entry.OuterXml
  }
  $catalogs=@{}
  foreach ($revision in @($base,'HEAD','MERGE_HEAD')) {
    $source=git -c core.excludesFile= show "${revision}:$file"
    if ($LASTEXITCODE -ne 0) { throw 'Catalog read failed' }
    $original=[xml]($source -join "`n")
    $catalog=@{}
    foreach ($entry in $original.root.data) { $catalog[$entry.name]=$entry.OuterXml }
    $catalogs[$revision]=$catalog
  }
  $expected=@{}
  foreach ($revision in @('HEAD','MERGE_HEAD')) {
    foreach ($name in $catalogs[$revision].Keys) {
      $value=$catalogs[$revision][$name]
      if ($expected.ContainsKey($name) -and $expected[$name] -cne $value) {
        if ($value -ceq $catalogs[$base][$name]) { Write-Output "Preserved HEAD update: $name"; continue }
        if ($expected[$name] -cne $catalogs[$base][$name]) { throw "Both changed: $name" }
      }
      $expected[$name]=$value
    }
  }
  foreach ($name in $expected.Keys) {
    if ($expected[$name] -cne $entries[$name]) { throw "Missing or changed: $file $name" }
  }
  if ($expected.Count -ne $entries.Count) { throw 'Unexpected entries' }
  Write-Output "PASS: $file : valid XML, $($entries.Count) unique entries, full three-way union preserved"
}
```

Final read-only checks:

```text
git -c core.excludesFile= grep -n -I -E '^(<<<<<<< |=======[[:space:]]*$|>>>>>>> )' -- .
git -c core.excludesFile= -c core.autocrlf=true diff --check
git -c core.excludesFile= -c core.autocrlf=true status --short
```

## Inspection notes and handoff

- The lane prompt and linked global instruction files reside under the user profile. They were not opened because this task prohibits user-profile access. Export intent was established from its commit, code and tests. The commit's referenced benchmark document is absent from this worktree.
- `rg` is unavailable; inspection used read-only Git and PowerShell. Initial Git reads emitted denied optional global-ignore lookups, then the per-process configuration above prevented further lookups. One inspection command had a PowerShell parse error from brace-style paths; the corrected path list ran successfully. A guessed test-helper path was absent; the discovered `tests/Shared/TestRecycleBin.cs` was read instead.
- The first combined validation command passed catalog and marker checks but exited 1 at `git diff --check`: disabling profile config also disabled CRLF normalization, so existing CRLF lines were reported as trailing whitespace. The corrected per-command `core.autocrlf=true` whitespace check passed without content changes.
- Focused tests cover all four lanes. Test classes with the legacy permanent-deletion fixture helper were not added to the focused filter. New integration fixtures and build outputs are retained; no cleanup was requested or performed.
- Only files within this worktree were edited. No staging, commits, merge continuation, checkout, reset or stash commands were issued. The two catalog paths remain `UU` until the lead stages their resolved contents. The import fix, regression test, and this LOG also need staging. No clipboard/registry operation or MyCapture.exe launch was performed.
