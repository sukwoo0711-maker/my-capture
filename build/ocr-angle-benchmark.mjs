// Offline angle-policy comparison. All models are local; run after a Release build.
// Every configuration runs serially in a fresh process. Evidence is retained, never cleaned.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const cwd = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
if (!process.env.MYCAPTURE_OCR_BENCH) throw new Error('Set MYCAPTURE_OCR_BENCH to a fresh evidence directory.');
const root = path.resolve(process.env.MYCAPTURE_OCR_BENCH);
if (fs.existsSync(root) && fs.readdirSync(root).length) throw new Error('Evidence directory must be empty.');
fs.mkdirSync(root, { recursive: true });
const write = (name, data) => fs.writeFileSync(path.join(root, name), JSON.stringify(data, null, 2));
const stamp = () => new Date().toISOString();
const phase = process.argv[2] ?? 'explore';
if (!['explore', 'holdouts', 'gate'].includes(phase)) throw new Error('Phase must be explore, holdouts or gate.');
const product = { boxThresh: 0.3, boxScoreThresh: 0.5, unClipRatio: 1.6,
  padding: 50, limitSideLen: 736, imgResize: 1024, textScore: 0.5 };
const geometries = [
  { ...product, detector: 'current', name: 'v5-product' },
  { ...product, detector: 'v6', name: 'v6-locked', boxThresh: 0.25, boxScoreThresh: 0.4, padding: 80, limitSideLen: 960 },
  { ...product, detector: 'v6', name: 'v6-product' },
];
const policies = [{ name: 'off', doAngle: false, clsThresh: 0.9 },
  { name: '97', doAngle: true, clsThresh: 0.97 }, { name: '99', doAngle: true, clsThresh: 0.99 }];
const hash = file => createHash('sha256').update(fs.readFileSync(path.join(cwd, file))).digest('hex');
const sources = fs.readdirSync(path.join(cwd, 'tests/MyCapture.App.Tests'))
  .filter(n => n.startsWith('OcrModelBenchmarkTests') && n.endsWith('.cs'))
  .map(n => `tests/MyCapture.App.Tests/${n}`);
sources.push('build/ocr-angle-benchmark.mjs', 'src/MyCapture.Ocr/NeuralOcrEngine.cs');
const binaries = ['MyCapture.App.Tests.dll', 'MyCapture.Ocr.dll', 'RapidOcrNet.dll']
  .map(n => `tests/MyCapture.App.Tests/bin/Release/net10.0-windows10.0.22000.0/${n}`);
write('manifest.json', { startedUtc: stamp(), cwd, phase, repetitions: 3,
  method: 'Serial fresh processes; cold excluded; same Accurate preparation and local Korean recognizer. Locked detector geometry from the prior request, no new geometry tuning.',
  sourceHashes: Object.fromEntries(sources.map(f => [f, hash(f)])),
  binaryHashes: Object.fromEntries(binaries.map(f => [f, hash(f)])) });
const results = [];
let next = 0;
const pct = n => n == null ? '—' : (100 * n).toFixed(3);
function saveTable() {
  const lines = ['| Detector/geometry | Angle | Corpus/split | Overall CER % | Korean UI % | Space-free % | Code % | Spreadsheet % | Rotated Korean % | Rotated English % | Median ms | Peak delta MiB | Peak MiB |',
    '|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|'];
  for (const r of results) {
    const p = r.pipeline, c = p.categories, a = r.configuration;
    lines.push(`| ${a.name} | ${a.doAngle ? a.clsThresh : 'off'} | ${r.corpus}/${r.split} | ${pct(p.overall.cer)} | ${pct(c['korean-ui']?.cer)} | ${pct(p.overall.spaceInsensitiveCer)} | ${pct(c.code?.cer)} | ${pct(c.spreadsheet?.cer)} | ${pct(c['rotated-korean']?.cer)} | ${pct(c['rotated-english']?.cer)} | ${p.overall.warmMedianMs.toFixed(2)} | ${(p.sampledPeakDeltaBytes / 1048576).toFixed(1)} | ${(p.sampledPeakWorkingSetBytes / 1048576).toFixed(1)} |`);
  }
  fs.writeFileSync(path.join(root, 'angle-table.md'), lines.join('\n') + '\n');
}
function run(geometry, policy, corpus, split) {
  const config = { ...geometry, ...policy, name: `${geometry.name}-${policy.name}` };
  const name = `${String(++next).padStart(3, '0')}-${config.name}-${corpus}-${split}`;
  const output = path.join(root, name);
  fs.mkdirSync(output);
  const temp = path.join(output, 'temp'); fs.mkdirSync(temp);
  const profile = path.join(output, 'profile'); fs.mkdirSync(profile);
  const args = ['test', 'tests/MyCapture.App.Tests/MyCapture.App.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore',
    '--filter', 'FullyQualifiedName=MyCapture.App.Tests.OcrModelBenchmarkTests.BenchmarkLocalOcrModels_WhenExplicitlyEnabled',
    '--logger', 'console;verbosity=minimal', '--results-directory', output];
  write('progress.json', { state: 'running', name, completed: results.length, startedUtc: stamp() });
  console.log(`${stamp()} START ${name}`);
  const startedUtc = stamp();
  const executed = spawnSync('dotnet', args, { cwd, encoding: 'utf8', windowsHide: true, maxBuffer: 16 * 1024 * 1024,
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', TEMP: temp, TMP: temp,
      MYCAPTURE_DATA_ROOT: profile, MYCAPTURE_OCR_BENCH: output, MYCAPTURE_OCR_BENCH_MODE: 'detector',
      MYCAPTURE_OCR_BENCH_SPLIT: split, MYCAPTURE_OCR_BENCH_CORPUS: corpus,
      MYCAPTURE_OCR_BENCH_OPTIONS: JSON.stringify(config), MYCAPTURE_OCR_BENCH_DIAGNOSTICS: phase === 'gate' ? '1' : '0' } });
  fs.writeFileSync(path.join(output, 'execution.log'), (executed.stdout ?? '') + (executed.stderr ?? ''));
  fs.writeFileSync(path.join(output, 'execution.json'), JSON.stringify({ cwd, command: ['dotnet', ...args],
    startedUtc, finishedUtc: stamp(), exitCode: executed.status, error: executed.error?.message }, null, 2));
  if (executed.status !== 0) throw new Error(`${name} failed (${executed.status}); see execution.log`);
  const result = JSON.parse(fs.readFileSync(path.join(output, 'detector-results.json'), 'utf8'));
  result.directory = output;
  results.push(result); write('all-results.json', results); saveTable();
  console.log(`${stamp()} DONE ${name} CER=${pct(result.pipeline.overall.cer)}% median=${result.pipeline.overall.warmMedianMs.toFixed(2)}ms`);
}
try {
  if (phase === 'explore') {
    for (const policy of policies) for (const geometry of geometries) {
      run(geometry, policy, 'original', 'tuning');
      run(geometry, policy, 'rotated', 'all');
    }
    for (const geometry of geometries) run(geometry, { name: '90', doAngle: true, clsThresh: 0.9 }, 'rotated', 'all');
  } else if (phase === 'holdouts') {
    for (const [corpus, split] of [['original', 'validation'], ['validation2', 'all']])
      for (const policy of policies) for (const geometry of geometries) run(geometry, policy, corpus, split);
  } else {
    const policy = policies.find(p => p.name === process.env.MYCAPTURE_OCR_ANGLE_POLICY);
    if (!policy) throw new Error('Set MYCAPTURE_OCR_ANGLE_POLICY to the policy locked before holdouts: off, 97 or 99.');
    for (const [corpus, split] of [['original', 'validation'], ['validation2', 'all']])
      for (const geometry of geometries.slice(0, 2)) run(geometry, policy, corpus, split);
  }
  write('progress.json', { state: 'completed', count: results.length, finishedUtc: stamp() });
} catch (error) {
  write('progress.json', { state: 'failed', count: results.length, error: String(error) });
  throw error;
}
