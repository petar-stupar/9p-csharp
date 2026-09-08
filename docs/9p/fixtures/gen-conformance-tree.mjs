#!/usr/bin/env node
/**
 * Materialises sample.json as a directory tree on disk using the jsonfs
 * mapping (ARCHITECTURE.md §7), for serving through an external 9P server
 * such as hugelgupf/p9's p9ufs. Usage: node gen-conformance-tree.mjs <outdir>
 */
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const outdir = process.argv[2];
if (!outdir) {
  console.error('usage: gen-conformance-tree.mjs <outdir>');
  process.exit(64);
}
const doc = JSON.parse(readFileSync(fileURLToPath(new URL('./sample.json', import.meta.url)), 'utf8'));
const isDir = (v) => v !== null && typeof v === 'object';
const text = (v) => (v === null ? '' : typeof v === 'string' ? v : JSON.stringify(v));
const entries = (v) => (Array.isArray(v) ? v.map((x, i) => [String(i), x]) : Object.entries(v));
let files = 0;
function walk(dir, value) {
  mkdirSync(dir, { recursive: true });
  for (const [name, v] of entries(value)) {
    const p = join(dir, name);
    if (isDir(v)) walk(p, v);
    else {
      writeFileSync(p, text(v));
      files += 1;
    }
  }
}
walk(outdir, doc);
console.log(`materialised ${files} files under ${outdir}`);
