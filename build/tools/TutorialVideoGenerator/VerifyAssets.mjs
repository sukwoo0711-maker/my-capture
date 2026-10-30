import { readFileSync, statSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';
import assert from 'node:assert/strict';

// Independent inspection of the encoded containers, installed copies and first-frame JPEGs.
// Does not invoke an encoder, alter assets or touch the user's profile.
const [folderArg, installedArg, expectedArg] = process.argv.slice(2);
assert(folderArg && installedArg && expectedArg,
  'Usage: node VerifyAssets.mjs <generation-folder> <installed-assets-folder|-> <expected-id,id,...>');
const checkInstalled = installedArg !== '-';
const expectedFeatures = expectedArg.split(',');
assert(expectedFeatures.every(id => /^[a-z]+(?:-[a-z]+)*$/.test(id)), 'Invalid expected feature ID');
assert.equal(new Set(expectedFeatures).size, expectedFeatures.length, 'Duplicate expected feature ID');
const folder = resolve(folderArg);
const summary = JSON.parse(readFileSync(join(folder, 'receipt.json'), 'utf8').replace(/^\uFEFF/, ''));
assert.deepEqual(summary.Failures, []);
assert.equal(summary.Resolution, '960x540');
assert.equal(summary.Fps, 25);
assert.equal(summary.Profile, 100);

function boxes(bytes, start = 0, end = bytes.length) {
  const result = [];
  for (let offset = start; offset < end;) {
    assert(offset + 8 <= end, 'Truncated MP4 box header');
    let size = bytes.readUInt32BE(offset), header = 8;
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    if (size === 1) { size = Number(bytes.readBigUInt64BE(offset + 8)); header = 16; }
    if (size === 0) size = end - offset;
    assert(size >= header && offset + size <= end, `Invalid ${type} box size`);
    result.push({ type, start: offset + header, end: offset + size });
    offset += size;
  }
  return result;
}
function child(bytes, box, type) {
  const matches = boxes(bytes, box.start, box.end).filter(item => item.type === type);
  assert.equal(matches.length, 1, `Expected one ${type}`);
  return matches[0];
}
function video(bytes) {
  const moov = child(bytes, { start: 0, end: bytes.length }, 'moov');
  const tracks = boxes(bytes, moov.start, moov.end).filter(box => box.type === 'trak');
  assert.equal(tracks.length, 1, 'Silent tutorial must contain exactly one video track');
  const mdia = child(bytes, tracks[0], 'mdia');
  const hdlr = child(bytes, mdia, 'hdlr');
  assert.equal(bytes.toString('ascii', hdlr.start + 8, hdlr.start + 12), 'vide');
  const mdhd = child(bytes, mdia, 'mdhd');
  const version = bytes[mdhd.start];
  assert(version === 0 || version === 1);
  const timescale = bytes.readUInt32BE(mdhd.start + (version === 1 ? 20 : 12));
  const stbl = child(bytes, child(bytes, mdia, 'minf'), 'stbl');
  const stsd = child(bytes, stbl, 'stsd');
  assert.equal(bytes.readUInt32BE(stsd.start + 4), 1);
  const entry = boxes(bytes, stsd.start + 8, stsd.end)[0];
  assert.equal(entry.type, 'avc1');
  const width = bytes.readUInt16BE(entry.start + 24), height = bytes.readUInt16BE(entry.start + 26);
  assert.equal(width, 960); assert.equal(height, 540);
  const avcc = boxes(bytes, entry.start + 78, entry.end).find(box => box.type === 'avcC');
  assert(avcc, 'Missing H.264 decoder configuration');
  assert.equal(bytes[avcc.start + 1], 100, 'Expected H.264 High profile');
  const stts = child(bytes, stbl, 'stts');
  const entries = bytes.readUInt32BE(stts.start + 4);
  let frames = 0, ticks = 0;
  for (let index = 0; index < entries; index++) {
    const at = stts.start + 8 + index * 8;
    const count = bytes.readUInt32BE(at), delta = bytes.readUInt32BE(at + 4);
    assert.equal(timescale / delta, 25, 'Every frame must be 40 ms');
    frames += count; ticks += count * delta;
  }
  return { codec: 'H.264 High', width, height, fps: 25, frames, seconds: ticks / timescale, audioTracks: 0 };
}
function jpeg(bytes) {
  assert.equal(bytes.readUInt16BE(0), 0xffd8);
  for (let offset = 2; offset + 4 < bytes.length;) {
    assert.equal(bytes[offset++], 0xff);
    while (bytes[offset] === 0xff) offset++;
    const marker = bytes[offset++];
    const size = bytes.readUInt16BE(offset);
    if ([0xc0, 0xc1, 0xc2].includes(marker)) {
      return { width: bytes.readUInt16BE(offset + 5), height: bytes.readUInt16BE(offset + 3) };
    }
    assert(marker !== 0xda && marker !== 0xd9, 'JPEG frame header missing');
    offset += size;
  }
  throw new Error('JPEG dimensions missing');
}
const hash = bytes => createHash('sha256').update(bytes).digest('hex').toUpperCase();
const rows = [];
for (const receipt of summary.Receipts) {
  const stem = `${receipt.Feature}.${receipt.Language}`;
  const mp4 = readFileSync(join(folder, `${stem}.mp4`));
  const jpg = readFileSync(join(folder, `${stem}.jpg`));
  const format = video(mp4);
  assert.equal(format.frames, receipt.Frames);
  assert.equal(format.seconds, receipt.Seconds);
  assert(format.seconds >= 7 && format.seconds <= 9, `${stem}: expected 7–9 seconds`);
  assert.deepEqual(jpeg(jpg), { width: 960, height: 540 });
  assert.equal(mp4.length, receipt.Bytes); assert.equal(jpg.length, receipt.PosterBytes);
  assert.equal(hash(mp4), receipt.Sha256); assert.equal(hash(jpg), receipt.PosterSha256);
  assert(statSync(join(folder, `${stem}.sheet.png`)).size > 0);
  assert.deepEqual(JSON.parse(readFileSync(join(folder, `${stem}.receipt.json`), 'utf8')), receipt);
  if (checkInstalled) {
    assert.equal(hash(readFileSync(join(installedArg, `${stem}.mp4`))), receipt.Sha256);
    assert.equal(hash(readFileSync(join(installedArg, `${stem}.jpg`))), receipt.PosterSha256);
  }
  rows.push({ id: stem, ...format, bytes: mp4.length, posterBytes: jpg.length, installed: checkInstalled });
}
assert.equal(rows.length, summary.Clips);
assert.equal(new Set(rows.map(row => row.id)).size, rows.length);
assert.deepEqual(rows.map(row => row.id).sort(), expectedFeatures.flatMap(id => [`${id}.ko`, `${id}.en`]).sort(),
  'Generated clip set must exactly match the explicitly requested features in both languages');
for (const language of ['ko', 'en']) {
  const png = readFileSync(join(folder, `contact-sheet.${language}.png`));
  assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  assert.equal(png.toString('ascii', 12, 16), 'IHDR');
  assert.equal(png.readUInt32BE(16), 1440, 'Contact sheet must have three 480-pixel frames per row');
  assert.equal(png.readUInt32BE(20), 298 * expectedFeatures.length, 'Contact sheet must have one row per expected feature');
}
for (const id of new Set(summary.Receipts.map(row => row.Feature))) {
  assert(rows.some(row => row.id === `${id}.ko`), `Missing Korean ${id}`);
  assert(rows.some(row => row.id === `${id}.en`), `Missing English ${id}`);
}
const result = { result: 'PASS', expectedFeatures, clips: rows.length, rows };
writeFileSync(join(folder, 'verification.json'), JSON.stringify(result, null, 2) + '\n');
console.log(`PASS: ${rows.length} clips; H.264 High, 960x540, 25 fps, 7–9 seconds, silent; JPEGs, receipts, hashes${checkInstalled ? ' and installed copies' : ''} verified.`);
