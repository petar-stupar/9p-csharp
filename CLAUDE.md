# CLAUDE.md — ninep for C# / .NET (`9p-csharp`)

@ARCHITECTURE.md

The file imported above is **binding**; it imports the workspace-wide
[docs/9p/ARCHITECTURE.md](docs/9p/ARCHITECTURE.md) and
[docs/9p/protocol-reference.md](docs/9p/protocol-reference.md), which win over the code and over
this file. Do not restate them here; read them, then follow them.

## Current phase

Ticket 001 — the initial implementation of the 9P2000 / 9P2000.u / 9P2000.L protocol, the
client and server packages, the jsonfs and todofs examples and the conformance cli — has landed
as `0.1.0`. When the workspace loop runs a further ticket here, the ticket is
`.qode/contexts/current/ticket.md` and the driver is the workspace's `docs/9p/loop.md`. Do not
start work that is not in a spec; outside the loop, follow [CONTRIBUTING.md](CONTRIBUTING.md).

## Commands

<!-- Filled in by the scaffold commit and kept current: -->
- `dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes` — build + tests + lint at zero warnings. **Work is not done until this is green.**
- `dotnet test` · `dotnet format --verify-no-changes` · `dotnet build` · `dotnet pack -c Release`
- `dotnet run --project tests/NineP.Conformance -- self` — the conformance scenario against this repo's own jsonfs.

## Day-to-day conventions

- **Dependency direction:** `examples → client|server → protocol`; `protocol` imports nothing
  from the others; `client` and `server` never import each other. A test pins this.
- **Handlers never see dialect types.** Only `protocol` knows `Tstat` from `Tgetattr`.
- **Every rule in `docs/9p/protocol-reference.md` §8 is a named test**, and the security-relevant
  ones are mutation-tested (delete the check, watch the named test fail).
- **Every test is classified against the shared test index** (workspace ARCHITECTURE.md §13). A new
  test in the three layer suites fails `TestIndexTests` until it is either mapped to an obligation
  in `docs/9p/fixtures/test-index.json` or listed as local in `docs/test-map.json`. See
  [CONTRIBUTING.md](CONTRIBUTING.md) §Adding a test. This port seeds the index for all fourteen
  ports, so prefer `required` and reserve `local` for genuine C#/.NET plumbing.
- **Golden vectors** in `docs/9p/fixtures/wire-vectors.json` are decoded, re-encoded and compared
  byte-for-byte; the mutation matrix of §9 yields typed errors.
- **Honest failure semantics.** Nothing silent maps to success; unrecoverable framing errors
  close the connection with a logged reason.
- **One type per file** in `src/` and `examples/`, the file named after the type. Nested types are
  unaffected; `tests/` is exempt on purpose. `RepoHygieneTests.EverySourceFileHoldsOneTypeNamedAfterIt`
  enforces it.
- **A directory holds surface or implementation, never both.** Every `internal` type in `src/` sits
  under an `Internal/` directory, with the namespace its directory implies
  (`NineP.Protocol.Codec.Internal`, …); `RepoHygieneTests.ImplementationTypesLiveUnderAnInternalDirectory`
  enforces it. The examples declare **no** public type — they are programs, and the test projects
  that drive them are named in each example's `InternalsVisibleTo`; `CA1515` enforces that.
- **Zero-warning policy.** A warning is a failure; suppress only at the source with a reason.
- **Control bytes:** before committing, scan touched files with `tr -d '\0' < f | cmp -s - f`
  and `LC_ALL=C grep -n '[[:cntrl:]]' f | grep -v $'\t'`; the repo's no-control-bytes test walks
  every tracked file except `*.png`, the package icon's format, which `.gitattributes` marks binary.
- **Vendored docs are copies.** Never edit `docs/9p/*` here; changes go to the workspace and are
  re-vendored (`node scripts/9p-loop/setup-repo.mjs vendor csharp` from the workspace).
- **Measured claims carry their measurement** (`docs/benchmarks.md`, `docs/interop.md`).

## Dependency policy

Every runtime dependency requires a Decision Log row in `ARCHITECTURE.md` (this repo's), with the
version pinned exactly. Prefer the standard library. No dependency that runs install scripts.

## qode workflow

This repo is driven by the workspace loop (`/9p-loop` in the workspace). Task state lives in
`.qode/contexts/current/` (gitignored). Gates: refine 25/25 (minimum two iterations), code review
≥ 10/12, security review ≥ 8/10; never bypass with `--force`. `gh` needs
`env -u GITHUB_TOKEN gh …` in this environment.
