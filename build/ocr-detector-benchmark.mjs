// Offline, serial, fresh-process detector experiment. Build the solution before invoking.
// Evidence is retained exclusively below MYCAPTURE_OCR_BENCH; this script never cleans it up.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const cwd = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
if (!process.env.MYCAPTURE_OCR_BENCH) throw new Error('Set MYCAPTURE_OCR_BENCH to a fresh evidence directory.');
const root = path.resolve(process.env.MYCAPTURE_OCR_BENCH);
if (fs.existsSync(root) && fs.readdirSync(root).length) throw new Error('Evidence directory must be empty; existing evidence is retained.');
fs.mkdirSync(root, { recursive: true });
const write = (name, data) => fs.writeFileSync(path.join(root, name), JSON.stringify(data, null, 2));
const defaults = { detector: 'v6', boxThresh: 0.3, boxScoreThresh: 0.5, unClipRatio: 1.6,
  padding: 50, limitSideLen: 736, imgResize: 1024, textScore: 0.5 };
const results = [];
let next = 0;
const stamp = () => new Date().toISOString();
const manifest = {
  startedUtc: stamp(), cwd,
  method: 'One serial dotnet test process per configuration, 3 warm repetitions/image, cold excluded; timed product pipeline, diagnostics after measured memory.',
  pruning: '9 threshold pairs at default geometry; best two extend unclip/padding, then best two extend resolution. Absolute median <=75ms preferred for seeds. TextScore 0.4 only when baseline Korean diagnostics show score drops. Candidate locked before either validation corpus runs.',
  sourceHashes: Object.fromEntries(['tests/MyCapture.App.Tests/OcrModelBenchmarkTests.cs',
    'tests/MyCapture.App.Tests/OcrModelBenchmarkTests.Detector.cs',
    'tests/MyCapture.App.Tests/OcrModelBenchmarkTests.Diagnostics.cs',
    'tests/MyCapture.App.Tests/OcrModelBenchmarkTests.Validation.cs', 'build/ocr-detector-benchmark.mjs'].map(f =>
      [f, createHash('sha256').update(fs.readFileSync(path.join(cwd, f))).digest('hex')])),
  binaryHashes: Object.fromEntries(['MyCapture.App.Tests.dll', 'MyCapture.Ocr.dll', 'RapidOcrNet.dll'].map(name => {
    const file = path.join('tests', 'MyCapture.App.Tests', 'bin', 'Release', 'net10.0-windows10.0.22000.0', name);
    return [file, createHash('sha256').update(fs.readFileSync(path.join(cwd, file))).digest('hex')];
  })),
};
write('manifest.json', manifest);

function run(configuration, split = 'tuning', corpus = 'original', diagnostic = false) {
  const config = { ...defaults, ...configuration };
  const name = `${String(++next).padStart(3, '0')}-${config.name}-${corpus}-${split}`;
  const output = path.join(root, name);
  fs.mkdirSync(output);
  const args = ['test', 'tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj', '-c', 'Release',
    '--no-build', '--no-restore', '--filter', 'FullyQualifiedName~OcrModelBenchmarkTests.BenchmarkLocalOcrModels_WhenExplicitlyEnabled',
    '--logger', 'console;verbosity=minimal', '--results-directory', output];
  write('progress.json', { state: 'running', name, startedUtc: stamp() });
  console.log(`${stamp()} START ${name}`);
  const startedUtc = stamp();
  const executed = spawnSync('dotnet', args, { cwd, encoding: 'utf8', windowsHide: true,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1',
      MYCAPTURE_OCR_BENCH: output, MYCAPTURE_OCR_BENCH_MODE: 'detector',
      MYCAPTURE_OCR_BENCH_SPLIT: split, MYCAPTURE_OCR_BENCH_CORPUS: corpus,
      MYCAPTURE_OCR_BENCH_OPTIONS: JSON.stringify(config), MYCAPTURE_OCR_BENCH_DIAGNOSTICS: diagnostic ? '1' : '0' },
    maxBuffer: 16 * 1024 * 1024 });
  fs.writeFileSync(path.join(output, 'execution.log'), (executed.stdout ?? '') + (executed.stderr ?? ''));
  fs.writeFileSync(path.join(output, 'execution.json'), JSON.stringify({ cwd, command: ['dotnet', ...args],
    startedUtc, finishedUtc: stamp(), exitCode: executed.status, error: executed.error?.message }, null, 2));
  if (executed.status !== 0) throw new Error(`${name} failed (${executed.status}); see execution.log`);
  const result = JSON.parse(fs.readFileSync(path.join(output, 'detector-results.json'), 'utf8'));
  result.directory = output;
  results.push(result);
  write('all-results.json', results);
  const m = result.pipeline.Overall ?? result.pipeline.overall;
  console.log(`${stamp()} DONE ${name} CER=${(m.cer * 100).toFixed(2)}% Korean=${(result.pipeline.categories['korean-ui'].cer * 100).toFixed(2)}% median=${m.warmMedianMs.toFixed(1)}ms`);
  saveTable();
  return result;
}
const tuneResults = () => results.filter(r => r.split === 'tuning' && r.configuration.detector === 'v6');
function summary(r) {
  return { cer: r.pipeline.overall.cer, korean: r.pipeline.categories['korean-ui'].cer,
    space: r.pipeline.overall.spaceInsensitiveCer, koreanSpace: r.pipeline.categories['korean-ui'].spaceInsensitiveCer,
    median: r.pipeline.overall.warmMedianMs, peakDelta: r.pipeline.sampledPeakDeltaBytes,
    peak: r.pipeline.sampledPeakWorkingSetBytes,
    extra: r.falsePositiveControls.reduce((s, i) => s + i.extraLines, 0),
    distant: r.falsePositiveControls.reduce((s, i) => s + i.distantLines, 0) };
}
function gates(candidate, baseline) {
  const c = summary(candidate), b = summary(baseline);
  return { overall: c.cer <= b.cer, koreanUi: c.korean <= b.korean,
    latency: c.median <= b.median * 0.75, peakMemory: c.peakDelta <= b.peakDelta && c.peak <= b.peak,
    extraLines: c.extra <= b.extra, distantLines: c.distant <= b.distant };
}
function ranked(items, baseline) {
  const score = r => {
    const s = summary(r), g = gates(r, baseline);
    return [Object.values(g).every(Boolean) ? 0 : 1, s.median <= 75 ? 0 : 1,
      s.korean, s.cer, s.space, s.extra, s.distant, s.median];
  };
  return [...items].sort((a, b) => { const x = score(a), y = score(b);
    for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] - y[i];
    return a.configuration.name.localeCompare(b.configuration.name); });
}
function already(config) {
  return tuneResults().some(r => Object.keys(defaults).every(k => r.configuration[k] === config[k]));
}
function evaluate(config) { if (!already(config)) return run(config); }
function saveTable() {
  const lines = ['| Name | Corpus/split | Box threshold | Box score | Unclip | Pad | Limit/resize | Text score | CER % | Korean % | Space-free % | Korean space-free % | Median ms | Peak delta MiB | Peak MiB | Extra/distant controls |',
    '|---|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---|'];
  for (const r of results) {
    const c = r.configuration, s = summary(r), pct = n => (100 * n).toFixed(3), mib = n => (n / 1048576).toFixed(1);
    lines.push(`| ${c.name} | ${r.corpus}/${r.split} | ${c.boxThresh} | ${c.boxScoreThresh} | ${c.unClipRatio} | ${c.padding} | ${c.limitSideLen}/${c.imgResize} | ${c.textScore} | ${pct(s.cer)} | ${pct(s.korean)} | ${pct(s.space)} | ${pct(s.koreanSpace)} | ${s.median.toFixed(2)} | ${mib(s.peakDelta)} | ${mib(s.peak)} | ${s.extra}/${s.distant} |`);
  }
  fs.writeFileSync(path.join(root, 'tuning-table.md'), lines.join('\n') + '\n');
}

try {
  const baseline = run({ name: 'A-cur', detector: 'current' }, 'tuning', 'original', true);
  const initial = run({ name: 'A-v6' }, 'tuning', 'original', true);
  const diagnostic = JSON.parse(fs.readFileSync(path.join(initial.directory, 'diagnostics.json'), 'utf8'));
  const scoreDrops = diagnostic.images.filter(i => i.id.includes('korean-ui'))
    .flatMap(i => i.diagnostic.recognizedLinesBeforeTextScore).filter(l => l.score != null && l.score < 0.5);
  write('diagnosis-decision.json', { permitTextScore04: scoreDrops.length > 0, scoreDrops,
    reason: 'Exploratory lower-confidence branch after observed score drops; lowering the threshold can retain incorrect text, so accuracy and control gates still apply.' });
  for (const boxThresh of [0.2, 0.25, 0.3])
    for (const boxScoreThresh of [0.3, 0.4, 0.5])
      evaluate({ ...defaults, boxThresh, boxScoreThresh, name: `threshold-${boxThresh}-${boxScoreThresh}` });
  const geometrySeeds = ranked(tuneResults(), baseline).slice(0, 2);
  write('geometry-seeds.json', geometrySeeds.map(r => r.configuration));
  for (const [seed, r] of geometrySeeds.entries())
    for (const unClipRatio of [1.6, 1.8, 2.0])
      for (const padding of [50, 80])
        evaluate({ ...r.configuration, unClipRatio, padding, name: `geometry-${seed}-${unClipRatio}-${padding}` });
  const resolutionSeeds = ranked(tuneResults(), baseline).slice(0, 2);
  write('resolution-seeds.json', resolutionSeeds.map(r => r.configuration));
  for (const [seed, r] of resolutionSeeds.entries())
    for (const [limitSideLen, imgResize] of [[960, 1024], [736, 1280], [960, 1280]])
      evaluate({ ...r.configuration, limitSideLen, imgResize, name: `resolution-${seed}-${limitSideLen}-${imgResize}` });
  if (scoreDrops.length) for (const [seed, r] of ranked(tuneResults(), baseline).slice(0, 2).entries())
    evaluate({ ...r.configuration, textScore: 0.4, name: `recognition-${seed}-0.4` });
  const selected = ranked(tuneResults(), baseline)[0];
  const choice = { lockedUtc: stamp(), configuration: selected.configuration, tuningGates: gates(selected, baseline),
    tuningBaseline: summary(baseline), tuningCandidate: summary(selected), validationAccessed: false };
  write('selected-options.json', choice);
  const validations = [];
  for (const [corpus, split] of [['original', 'validation'], ['validation2', 'all']]) {
    const a = run({ name: 'A-cur', detector: 'current' }, split, corpus, true);
    const b = run({ ...selected.configuration, name: 'selected-v6' }, split, corpus, true);
    validations.push({ corpus, split, baseline: summary(a), candidate: summary(b), gates: gates(b, a) });
  }
  const quantitativeGatesPassed = Object.values(choice.tuningGates).every(Boolean)
    && validations.every(v => Object.values(v.gates).every(Boolean));
  const decision = { selected: choice, validations, quantitativeGatesPassed,
    adopt: false, falsePositiveReview: 'pending',
    adoptionStatus: quantitativeGatesPassed ? 'pending-control-review' : 'rejected-by-quantitative-gates',
    note: 'Numeric control counts are heuristic audit aids. Final adoption requires an independent per-image/category review of retained code/spreadsheet strings, including duplicate lines and omitted truth.', finishedUtc: stamp() };
  write('decision.json', decision);
  write('progress.json', { state: 'completed', evaluated: results.length, adopt: decision.adopt });
  console.log(JSON.stringify(decision, null, 2));
} catch (error) {
  write('progress.json', { state: 'failed', error: String(error), evaluated: results.length });
  throw error;
}
