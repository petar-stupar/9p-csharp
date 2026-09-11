#!/usr/bin/env node
/**
 * Renders test-index.json as test-index.md. The JSON is the source of truth — every port's
 * harness reads it directly — and this is the copy a person reviews.
 *
 *   node docs/9p/fixtures/gen-test-index-md.mjs
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const index = JSON.parse(readFileSync(join(here, 'test-index.json'), 'utf8'));
const byArea = new Map();
for (const test of index.tests) {
  if (!byArea.has(test.area)) byArea.set(test.area, []);
  byArea.get(test.area).push(test);
}

const required = index.tests.filter((t) => t.tier === 'required').length;
const recommended = index.tests.length - required;
const out = [];
out.push('# Shared test index');
out.push('');
out.push('**Generated from [test-index.json](test-index.json) by `gen-test-index-md.mjs`. Edit the');
out.push('JSON, not this file.**');
out.push('');
out.push(index.description.replace(/\s+/g, ' '));
out.push('');
out.push(`${index.tests.length} obligations across ${byArea.size} areas: **${required} required**,`);
out.push(`**${recommended} recommended**.`);
out.push('');
for (const [tier, meaning] of Object.entries(index.tiers)) {
  out.push(`- **${tier}** — ${meaning.replace(/\s+/g, ' ')}`);
}
out.push('');
out.push('| Area | Obligations | Required | Recommended |');
out.push('| --- | ---: | ---: | ---: |');
for (const area of [...byArea.keys()].sort()) {
  const tests = byArea.get(area);
  const req = tests.filter((t) => t.tier === 'required').length;
  out.push(`| \`${area}\` | ${tests.length} | ${req} | ${tests.length - req} |`);
}
out.push('');
for (const area of [...byArea.keys()].sort()) {
  out.push(`## \`${area}\``);
  out.push('');
  out.push('| Id | Layer | Tier | Rules | Conformance |');
  out.push('| --- | --- | --- | --- | --- |');
  for (const test of byArea.get(area)) {
    const rules = (test.rules ?? []).map((r) => `\`${r}\``).join(', ') || '';
    const parts = (test.conformance ?? []).map((c) => `\`${c}\``).join(', ') || '';
    const short = test.id.slice(area.length + 1);
    out.push(`| \`${short}\` | ${test.layer} | ${test.tier} | ${rules} | ${parts} |`);
  }
  out.push('');
}
writeFileSync(join(here, 'test-index.md'), out.join('\n'));
console.log(`test-index.md: ${index.tests.length} obligations, ${byArea.size} areas`);
