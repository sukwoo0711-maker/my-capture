import { readFileSync, readdirSync, existsSync, mkdirSync, copyFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const repositoryRoot = fileURLToPath(new URL('../../../', import.meta.url));
function confinedPath(root, name) {
  const path = resolve(root, name);
  const fromRoot = relative(root, path);
  if (fromRoot.startsWith('..') || isAbsolute(fromRoot))
    throw new Error('Asset path must stay within its root directory');
  return path;
}

// Preserve each raw run (including failures), collecting the latest successful receipt per clip.
const [destination, ...sourceArgs] = process.argv.slice(2);
assert(destination && sourceArgs.length, 'Usage: node AssembleReview.mjs <empty-output-folder> <run-folder> [newer-run-folder ...]');
const output = confinedPath(repositoryRoot, resolve(destination));
assert(!existsSync(output) || readdirSync(output).length === 0, 'Output folder must be empty');
const selected = new Map(), runs = [];
let baseline;
const hash = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();
for (const sourceArg of sourceArgs) {
  const source = confinedPath(repositoryRoot, resolve(sourceArg));
  const receipt = JSON.parse(readFileSync(confinedPath(source, 'receipt.json'), 'utf8'));
  baseline ??= receipt;
  for (const key of ['ProductDllSha256', 'Resolution', 'Theme', 'Accent', 'Fps', 'Profile', 'Quality'])
    assert.equal(receipt[key], baseline[key], `Incompatible ${key} in ${source}`);
  const seen = new Set();
  for (const clip of receipt.Receipts) {
    const stem = `${clip.Feature}.${clip.Language}`;
    assert(/^[a-z]+(?:-[a-z]+)*\.(ko|en)$/.test(stem));
    assert(!seen.has(stem), `Duplicate receipt ${stem}`); seen.add(stem);
    assert.equal(hash(readFileSync(confinedPath(source, `${stem}.mp4`))), clip.Sha256);
    assert.equal(hash(readFileSync(confinedPath(source, `${stem}.jpg`))), clip.PosterSha256);
    assert.deepEqual(JSON.parse(readFileSync(confinedPath(source, `${stem}.receipt.json`), 'utf8')), clip);
    selected.set(stem, { source, clip });
  }
  runs.push({ Folder: source, Clips: receipt.Clips, Failures: receipt.Failures, ElapsedSeconds: receipt.ElapsedSeconds });
}
assert(selected.size > 0, 'No successful clips to collect');
mkdirSync(output, { recursive: true });
const receipts = [];
for (const [stem, { source, clip }] of [...selected].sort(([a], [b]) => a.localeCompare(b))) {
  for (const extension of ['mp4', 'jpg', 'sheet.png', 'receipt.json'])
    copyFileSync(confinedPath(source, `${stem}.${extension}`), confinedPath(output, `${stem}.${extension}`));
  receipts.push(clip);
}
const summary = {
  ...baseline,
  Assembly: 'Latest successful per-clip receipts collected from the listed runs; original failures are retained in SourceRuns.',
  SourceRuns: runs,
  Clips: receipts.length,
  Bytes: receipts.reduce((sum, clip) => sum + clip.Bytes, 0),
  PosterBytes: receipts.reduce((sum, clip) => sum + clip.PosterBytes, 0),
  ElapsedSeconds: Math.round(runs.reduce((sum, run) => sum + run.ElapsedSeconds, 0) * 10) / 10,
  Failures: [],
  Receipts: receipts,
};
writeFileSync(confinedPath(output, 'receipt.json'), JSON.stringify(summary, null, 2) + '\n');
console.log(`COLLECTED=${receipts.length} OUT=${output}. Generate contact sheets with review, then run VerifyAssets.mjs.`);
