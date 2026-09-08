#!/usr/bin/env node
/**
 * PreToolUse hook (matcher: Bash): repo-scoped rm / mv / chmod for Claude Code.
 *
 * Wire it from `.claude/settings.local.json` (see `settings.local.example.json`):
 *
 *   "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command",
 *     "command": "node \"$CLAUDE_PROJECT_DIR/.claude/hooks/repo-scoped-fs-guard.mjs\"" } ] } ] }
 *
 * and drop `Bash(rm:*)`, `Bash(mv:*)`, `Bash(chmod:*)` from `permissions.deny` — a deny
 * rule is evaluated before any hook and would make this one unreachable.
 *
 * Policy (owner's, 2026-09-04):
 *   - rm     is allowed when every path it names resolves INSIDE the repository
 *            working tree (and is not the tree itself or its .git directory).
 *   - mv     is allowed when every source AND the destination resolve inside it.
 *   - chmod  is allowed on in-repo paths unless the mode elevates privileges:
 *            setuid / setgid / sticky (symbolic `s` or `t`, or a 4-digit octal
 *            with a non-zero leading digit) are denied. `+x` is allowed.
 *
 * Decision shape. A PreToolUse "allow" covers the WHOLE tool call, so this
 * script only answers "allow" when it can vouch for every simple command in
 * the line: rm/mv/chmod (verified), plus a handful of inert helpers (cd into
 * the repo, echo, printf, true, ls, test). Any rm/mv/chmod it cannot verify —
 * a glob, a variable, a command substitution, `~user`, a relative path after a
 * cd it could not follow, a stdin-driven rm with no operands — is answered
 * "ask", which hands the call back to the normal permission flow. A rm/mv/chmod
 * that verifiably leaves the repo, or targets the repo root or .git, is a
 * "deny". A line with no rm/mv/chmod at all produces no output (no decision),
 * and so does a line that mixes verified rm/mv/chmod with other commands: the
 * guard never vouches for what it did not inspect.
 *
 * Repo root: $CLAUDE_PROJECT_DIR (set by Claude Code for hooks), else the
 * hook input's `cwd`. Workspace amendment (petar-stupar, 2026-09-05): the
 * project dir holds one git repository per 9P language under it, so every
 * `.git` segment anywhere in the tree is denied, and a directory that itself
 * contains `.git` (a nested repository root) is treated like the project root. Paths are resolved through realpath on their deepest
 * existing ancestor, so a symlink pointing out of the tree is treated as
 * outside — conservative on purpose (removing the link itself is also denied).
 *
 * Second amendment (2026-09-05, code review round 1 finding F-28): the
 * project's Claude Code **session scratchpad** counts as inside. The harness
 * hands agents that directory for temporary files and docs/9p/loop.md makes
 * cleanup of empirical probes mandatory, which was impossible while `rm` there
 * was denied as "outside the repository". The scratchpad root is
 * `/private/tmp/claude-<uid>/<encoded>/` (or `/tmp/claude-<uid>/<encoded>/`
 * where that is a distinct directory), `<encoded>` being the project directory
 * path with every `/` replaced by `-`; it is computed from the repo path at
 * run time, never hardcoded. The `.git`-segment and nested-repository-root
 * denials apply inside it exactly as they do inside the repository, and the
 * scratchpad root itself is not removable as a unit.
 *
 * Input: hook JSON on stdin. Output: hookSpecificOutput JSON on stdout.
 * Always exits 0; a decision is carried in the JSON, never in the exit code.
 * No dependencies; Node >= 20.
 */
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, resolve, sep } from 'node:path';

const HELPERS = new Set(['echo', 'printf', 'true', 'ls', 'test', '[', ':']);
const GUARDED = new Set(['rm', 'mv', 'chmod']);

function emit(decision, reason) {
  process.stdout.write(
    `${JSON.stringify({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: decision,
        permissionDecisionReason: `repo-scoped-fs-guard: ${reason}`,
      },
    })}\n`,
  );
}

/** Split a shell line into simple commands (arrays of words). Quoted text stays one word. Unsafe constructs are flagged. */
function parse(line) {
  const commands = [];
  let words = [];
  let word = '';
  let inWord = false;
  let unsafe = false;
  let i = 0;
  const push = () => {
    if (inWord) words.push(word);
    word = '';
    inWord = false;
  };
  const endCommand = () => {
    push();
    if (words.length > 0) commands.push(words);
    words = [];
  };
  while (i < line.length) {
    const c = line[i];
    if (c === "'") {
      const end = line.indexOf("'", i + 1);
      if (end === -1) return { commands: [], unsafe: true };
      word += line.slice(i + 1, end);
      inWord = true;
      i = end + 1;
      continue;
    }
    if (c === '"') {
      let j = i + 1;
      let s = '';
      while (j < line.length && line[j] !== '"') {
        if (line[j] === '\\' && j + 1 < line.length) {
          s += line[j + 1];
          j += 2;
          continue;
        }
        if (line[j] === '$' || line[j] === '`') unsafe = true;
        s += line[j];
        j += 1;
      }
      if (j >= line.length) return { commands: [], unsafe: true };
      word += s;
      inWord = true;
      i = j + 1;
      continue;
    }
    if (c === '\\' && i + 1 < line.length) {
      word += line[i + 1];
      inWord = true;
      i += 2;
      continue;
    }
    if (c === '$' || c === '`') unsafe = true;
    if (c === '(' || c === ')' || c === '{' || c === '}') {
      // Subshell / group braces act as separators; `$(` was already flagged.
      endCommand();
      i += 1;
      continue;
    }
    if (c === '&' || c === '|' || c === ';' || c === '\n') {
      endCommand();
      while (i < line.length && '&|;\n'.includes(line[i])) i += 1;
      continue;
    }
    if (c === '>' || c === '<') {
      // Redirections: skip the operator and its target word; not a path we police.
      push();
      i += 1;
      while (i < line.length && (line[i] === '>' || line[i] === '&' || line[i] === ' ')) i += 1;
      while (i < line.length && !' \t&|;\n'.includes(line[i])) i += 1;
      continue;
    }
    if (c === ' ' || c === '\t') {
      push();
      i += 1;
      continue;
    }
    word += c;
    inWord = true;
    i += 1;
  }
  endCommand();
  return { commands, unsafe };
}

function isGlob(arg) {
  return /[*?[\]]/.test(arg);
}

/** realpath of the deepest existing ancestor, joined with the rest. */
function canonical(path) {
  let existing = path;
  const missing = [];
  while (!existsSync(existing)) {
    const parent = dirname(existing);
    if (parent === existing) return null;
    missing.unshift(existing.slice(parent.length + 1));
    existing = parent;
  }
  let real;
  try {
    real = realpathSync.native(existing);
  } catch {
    return null;
  }
  return missing.length === 0 ? real : resolve(real, ...missing);
}

/**
 * Roots of this project's Claude Code session scratchpad, derived from the repo
 * path: /private/tmp/claude-<uid>/<encoded>/ and the /tmp form of the same,
 * canonicalised (on macOS /tmp is a symlink to /private/tmp, so the two collapse
 * to one entry). <encoded> is the project directory path with its separators
 * flattened to "-"; the harness also flattens dots ("github.com" becomes
 * "github-com"), so both spellings are accepted and only ones that resolve are
 * kept. Never hardcodes a project. Returns [] when the uid is unavailable.
 */
function scratchpadRoots(repo) {
  if (typeof process.getuid !== 'function') return [];
  const encodings = new Set([
    repo.split(sep).join('-'), // separators only
    repo.replace(/[^A-Za-z0-9]/g, '-'), // separators and every other non-alphanumeric (dots)
  ]);
  const roots = new Set();
  for (const tmp of ['/private/tmp', '/tmp']) {
    for (const encoded of encodings) {
      const real = canonical(resolve(tmp, `claude-${process.getuid()}`, encoded));
      if (real !== null) roots.add(real);
    }
  }
  return [...roots];
}

function classify(path, repo, scratch = []) {
  const real = canonical(path);
  if (real === null) return 'unknown';
  if (real === repo) return 'root';
  // The base this path belongs to: the repository, or one of the scratchpad roots.
  let base = null;
  if (real.startsWith(repo + sep)) base = repo;
  else {
    for (const root of scratch) {
      if (real === root) return 'root'; // the scratchpad root itself is not removable as a unit
      if (real.startsWith(root + sep)) {
        base = root;
        break;
      }
    }
  }
  if (base === null) return 'outside';
  const rel = real.slice(base.length + 1);
  // Any .git segment anywhere in the tree: the workspace holds one git repository
  // per language under the project dir, and each of those .git directories is as
  // off-limits as the project's own. The same holds for anything cloned into the
  // scratchpad.
  if (rel.split(sep).includes('.git')) return 'git';
  // A directory that directly contains a .git entry is a nested repository root
  // (9p-<lang>/, plumber/): treated like the project root, never removable as a unit.
  if (existsSync(resolve(real, '.git'))) return 'root';
  return 'inside';
}

/** Split argv into flags and operands, honouring `--`. */
function operands(args) {
  const out = [];
  let literal = false;
  for (const a of args) {
    if (!literal && a === '--') {
      literal = true;
      continue;
    }
    if (!literal && a.startsWith('-') && a.length > 1) continue;
    out.push(a);
  }
  return out;
}

function privilegedMode(mode) {
  if (/^[0-7]{4}$/.test(mode)) return mode[0] !== '0';
  if (/^[0-7]{1,3}$/.test(mode)) return false;
  // Symbolic: any `s` (setuid/setgid) or `t` (sticky) in a permission clause elevates.
  return /[st]/.test(mode.replace(/[ugoa]+(?=[+\-=])/g, ''));
}

function main() {
  let input;
  try {
    input = JSON.parse(readFileSync(0, 'utf8'));
  } catch {
    return;
  }
  if (input.tool_name !== 'Bash') return;
  const line = String(input.tool_input?.command ?? '');
  const repoRaw = process.env.CLAUDE_PROJECT_DIR || input.cwd;
  if (!repoRaw) return;
  let repo;
  try {
    repo = realpathSync.native(repoRaw);
  } catch {
    return;
  }
  const { commands, unsafe } = parse(line);
  const guarded = commands.filter((c) => GUARDED.has(c[0]));
  if (guarded.length === 0) return; // nothing to police; no decision
  const scratch = scratchpadRoots(repo);

  let cwd = input.cwd ? (canonical(input.cwd) ?? input.cwd) : repo;
  let cwdKnown = true;
  let allVouched = true;
  const asks = [];

  /** Absolute path for an operand, or a string explaining why it cannot be resolved. */
  const resolveArg = (arg) => {
    if (arg === '~' || arg.startsWith('~/')) {
      const home = process.env.HOME;
      if (!home) return { why: '$HOME is unset, so "~" cannot be resolved' };
      return { path: resolve(home, arg.slice(2)) };
    }
    if (arg.startsWith('~')) return { why: '"~user" expansion is not resolved here' };
    if (isAbsolute(arg)) return { path: arg };
    if (!cwdKnown) return { why: 'a relative path after a cd this guard could not follow' };
    return { path: resolve(cwd, arg) };
  };

  for (const cmd of commands) {
    const [name, ...args] = cmd;
    if (name === 'cd' || name === 'pushd') {
      const target = args.find((a) => !a.startsWith('-')) ?? '~';
      const r = resolveArg(target);
      const kind = r.path === undefined || isGlob(target) ? 'unknown' : classify(r.path, repo, scratch);
      if (kind === 'inside' || kind === 'root') {
        cwd = canonical(r.path) ?? r.path;
      } else {
        cwdKnown = false;
        allVouched = false;
      }
      continue;
    }
    if (HELPERS.has(name)) continue;
    if (!GUARDED.has(name)) {
      allVouched = false; // some other command: never "allow" the whole line from here
      continue;
    }
    if (unsafe) {
      asks.push(`${name}: the line contains $, backticks, or a substitution this guard cannot resolve`);
      continue;
    }
    let paths = operands(args);
    if (name === 'chmod') {
      const [mode, ...rest] = paths;
      if (mode === undefined || args.some((a) => a.startsWith('--reference'))) {
        asks.push('chmod: no literal mode');
        continue;
      }
      if (privilegedMode(mode)) {
        emit('deny', `chmod mode "${mode}" sets setuid/setgid/sticky — privilege elevation is never allowed`);
        return;
      }
      paths = rest;
    }
    if (paths.length === 0) {
      asks.push(`${name}: no path operands (stdin- or xargs-driven?)`);
      continue;
    }
    for (const p of paths) {
      if (isGlob(p)) {
        asks.push(`${name}: "${p}" is a glob; name the paths explicitly`);
        continue;
      }
      const r = resolveArg(p);
      if (r.path === undefined) {
        asks.push(`${name}: "${p}" — ${r.why}`);
        continue;
      }
      const kind = classify(r.path, repo, scratch);
      if (kind === 'inside') continue;
      if (kind === 'unknown') {
        asks.push(`${name}: "${p}" could not be resolved`);
        continue;
      }
      const where = scratch.length > 0 ? `${repo} or the session scratchpad (${scratch.join(', ')})` : repo;
      const why =
        kind === 'root'
          ? 'is a repository root or the scratchpad root itself'
          : kind === 'git'
            ? 'is inside a .git directory'
            : `resolves outside the repository and the session scratchpad (${r.path})`;
      emit('deny', `${name} "${p}" ${why}; only paths inside ${where} are allowed`);
      return;
    }
  }

  if (asks.length > 0) {
    emit('ask', asks.join('; '));
    return;
  }
  if (allVouched) {
    emit('allow', `every rm/mv/chmod operand resolves inside ${repo} or this project's session scratchpad, and the line holds nothing else`);
    return;
  }
  // Every guarded operand is in-repo, but the line also runs other commands the
  // guard cannot vouch for: leave the decision to the normal flow.
}

main();
