#!/usr/bin/env node
/**
 * The error strings the Linux kernel's 9P client accepts over plain 9P2000.
 *
 * Over 9P2000 an Rerror carries only the ename; v9fs turns it into an errno with the exact-match
 * table in net/9p/error.c (Plan 9 wordings and glibc strerror texts), and a string not in that
 * table becomes ESERVERFAULT (526). Emits docs/9p/fixtures/linux-9p-errors.json: for every errno
 * the kernel table names, its number (asm-generic) and every string the kernel maps to it. Each
 * implementation's error table is checked against this file: the ename it sends for an errno must
 * be one Linux maps to that errno, and every string here must map back to its errno.
 *
 * Pinned to one kernel commit so that the fixture is reproducible; bump the commit deliberately.
 * Regenerate with `node docs/9p/fixtures/gen-linux-9p-errors.mjs` (needs network).
 */
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const COMMIT = 'ad2e2a77dcc7912c737a07fe061d93c414c6745e';
const RAW = `https://raw.githubusercontent.com/torvalds/linux/${COMMIT}/`;
const OUT = fileURLToPath(new URL('./linux-9p-errors.json', import.meta.url));

const fetchText = async (path) => {
  const response = await fetch(RAW + path);
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status}`);
  return response.text();
};

const numbers = {};
for (const header of ['include/uapi/asm-generic/errno-base.h', 'include/uapi/asm-generic/errno.h']) {
  const text = await fetchText(header);
  for (const [, name, value] of text.matchAll(/#define\s+(E[A-Z0-9]+)\s+(\d+)/g)) numbers[name] = Number(value);
  for (const [, name, alias] of text.matchAll(/#define\s+(E[A-Z0-9]+)\s+(E[A-Z0-9]+)\b/g)) numbers[name] = numbers[alias];
}

const table = new Map();
for (const [, text, name] of (await fetchText('net/9p/error.c')).matchAll(/\{"([^"]+)",\s*(E[A-Z0-9]+)\}/g)) {
  if (!(name in numbers)) throw new Error(`no number for ${name}`);
  if (!table.has(name)) table.set(name, []);
  table.get(name).push(text);
}

const errnos = [...table].map(([name, strings]) => ({ name, errno: numbers[name], strings })).sort((a, b) => a.errno - b.errno);
writeFileSync(OUT, JSON.stringify({ source: `linux/net/9p/error.c and include/uapi/asm-generic/errno*.h at ${COMMIT}`, errnos }, null, 1) + '\n');
console.log(`${errnos.length} errnos, ${errnos.reduce((n, e) => n + e.strings.length, 0)} strings -> ${OUT}`);
