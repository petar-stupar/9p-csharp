#!/usr/bin/env node
/**
 * Derives the expected conformance outputs from sample.json using the jsonfs
 * mapping in docs/9p/ARCHITECTURE.md §7 (object → dir, array → dir with
 * index names, string → exact UTF-8 bytes, number/bool → JSON text, null →
 * empty file). Writes sample.expected.txt, the file every language's cli
 * output is diffed against. Regenerate after changing sample.json.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const doc = JSON.parse(readFileSync(fileURLToPath(new URL('./sample.json', import.meta.url)), 'utf8'));
const out = fileURLToPath(new URL('./sample.expected.txt', import.meta.url));

const isDir = (v) => v !== null && typeof v === 'object';
const text = (v) => (v === null ? '' : typeof v === 'string' ? v : JSON.stringify(v));
const entries = (v) => (Array.isArray(v) ? v.map((x, i) => [String(i), x]) : Object.entries(v));

const lines = [];
function walk(path, value) {
  if (isDir(value)) {
    // `ls` output: one entry per line, directories suffixed with "/", sorted bytewise (C locale).
    // Bytewise (C locale) — Buffer.compare, not string `<`, which orders UTF-16 code units and
    // puts U+FF21 after U+1F680 where the bytes put it before.
    const names = entries(value).map(([n, v]) => (isDir(v) ? `${n}/` : n)).sort((a, b) => Buffer.compare(Buffer.from(a, 'utf8'), Buffer.from(b, 'utf8')));
    lines.push(`ls ${path}`);
    for (const n of names) lines.push(n);
    lines.push('');
    for (const [n, v] of entries(value)) walk(path === '/' ? `/${n}` : `${path}/${n}`, v);
  } else {
    const bytes = Buffer.from(text(value), 'utf8');
    lines.push(`cat ${path}`);
    lines.push(`size ${bytes.length}`);
    lines.push(`sha256 ${require('node:crypto').createHash('sha256').update(bytes).digest('hex')}`);
    lines.push('');
  }
}
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
walk('/', doc);
writeFileSync(out, `${lines.join('\n')}\n`);
console.log(`wrote ${out} (${lines.length} lines)`);
