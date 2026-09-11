# Contributing

Thank you for looking at this. The repository is small enough that one person reviews everything,
so the workflow is deliberately simple.

## How changes land

`main` is protected. Nobody pushes to it directly, not even the owner in the normal course of
things, and only the owner merges. Everyone else:

1. Fork the repository on GitHub and clone your fork.
2. Branch from `main` (`git switch -c fix/what-it-fixes`).
3. Make the change, with a test that fails without it.
4. Run the gate until it is green (see below).
5. Push the branch to your fork and open a pull request against `petar-stupar/9p-csharp:main`.
   CI runs the same gate on Linux, macOS and Windows; a red run is not reviewed.

Small, single-purpose pull requests are merged fastest. If a change is large or touches the public
API, open an issue first so the shape can be agreed before the work is done.

## The gate

```text
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
```

Warnings are failures. A suppression carries its reason at the point of suppression. `dotnet test`
includes the repository hygiene suites, which check among other things that:

- every `src/` and `examples/` file holds one type named after it, and `internal` types sit under
  an `Internal/` directory;
- every public member appears in [docs/api.md](docs/api.md) and the generated `docs/api/`
  (regenerate with `dotnet tool restore && dotnet docfx metadata && dotnet docfx build`);
- every rule in [docs/rule-index.md](docs/rule-index.md) resolves to a test that exists;
- every test is classified against the shared test index (see below);
- the README snippets compile and print what the README says they print.

## Adding a test

This port is the reference implementation, so its suite is the workspace's shared test suite
(workspace [ARCHITECTURE.md](docs/9p/ARCHITECTURE.md) §13). Every test in `NineP.Protocol.Tests`,
`NineP.Client.Tests` and `NineP.Server.Tests` is therefore either an obligation every port owes or
this port's own, and **a new test fails the gate until you say which**:

```
these tests are neither mapped to a shared obligation nor declared local in docs/test-map.json
```

- **It proves protocol behaviour** — anything another language would have to get right too. Add an
  entry to [docs/9p/fixtures/test-index.json](docs/9p/fixtures/test-index.json) with an id naming
  the *behaviour* (`create/server-owned-flag-refused`, never the C# method name), tier `required`,
  and the `rules` it proves if any; then map the id to your method in
  [docs/test-map.json](docs/test-map.json). The vendored copy is regenerated at the workspace
  (`node scripts/9p-loop/setup-repo.mjs vendor csharp`) — edit the workspace copy, not this one —
  and `node docs/9p/fixtures/gen-test-index-md.mjs` refreshes the readable table.
- **It needs a facility not every ecosystem has** — property testing, an external peer, an OIDC
  issuer, a scale budget — tier it `recommended` instead.
- **It is about C# or .NET itself** — a buffer pool, a target framework, a GC measurement — add the
  method to the `local` list in `docs/test-map.json`. Being local is a decision someone makes, not
  a default.

A regression test for a bug found here is almost always `required`: that is the whole point, and a
fix in one port is meant to put a test in the other thirteen.

## Dependencies

Every runtime dependency has a row in the Decision Log of [ARCHITECTURE.md](ARCHITECTURE.md) with
its version pinned exactly, and a test compares the pin against `Directory.Packages.props`. Prefer
the standard library. Dependabot opens pull requests for package and action updates; a bump of a
runtime dependency needs its Decision Log row updated in the same pull request or the gate fails.
Restore runs in locked mode, so a bump must carry every `packages.lock.json` it changes: run
`dotnet restore --force-evaluate` and commit the result. On Dependabot branches the
`dependabot-locks` workflow does that and reruns CI on the regenerated commit.

## Documentation

`docs/9p/` is vendored from the owner's workspace and is not edited here. Everything else under
`docs/`, the README, the CHANGELOG and ARCHITECTURE.md are maintained in this repository; a change
in behavior updates them in the same pull request, and measured claims carry their measurement.

## Releasing

A version reaches nuget.org from a `v*` tag on `main`, pushed by the owner; the `release`
workflow checks the tag against `Directory.Build.props` and `CHANGELOG.md`, reruns the gate, and
publishes through nuget.org Trusted Publishing. [docs/releasing.md](docs/releasing.md) has the
procedure. Nothing is published per commit.

## The qode workflow

The owner drives larger tickets through qode with the slash commands
under `.claude/commands/` and the prompts under `.qode/`. They are committed so that anyone using
Claude Code can reuse them; they are not required for an ordinary pull request.

## Test suites

The protocol, client and server test projects share seven suite folders and namespaces. Each test
class has exactly one `Category` trait matching its folder. Helpers and collection definitions
are exempt. Additional qualifiers use `Kind` (for example `Interop`, `Scale`, or `Stress`).

| Category | Put tests here when they primarily check |
| --- | --- |
| Conformance | Required wire, API and filesystem behavior, including legal boundaries |
| Robustness | Malformed input, limits and recoverable or terminal error handling |
| Regression | A previously fixed bug with a named regression |
| Chaos | Controlled transport faults, scale and performance measurements |
| StateMachine | Generated operation sequences, state tables and codec properties |
| Security | Authentication, authorization and hostile-peer defenses |
| Compat | Dialect negotiation, external peers, frameworks and interoperability |

Run a category in an applicable layer, for example:

```sh
dotnet test --project tests/NineP.Server.Tests -- --filter-trait "Category=Security"
python scripts/test-suites.py
```

The discovery report counts cases by framework and suite; it includes opt-in cases, while the
execution report distinguishes skips. Protocol has no Chaos integration suite. Client and server
host the transport fault and lifecycle models. Example tests run only on net10.0; the client
Security suite (CLI OIDC) therefore exists only there. Other behavior suites cover both frameworks.
Repo/Docs tests check repository hygiene and are outside the behavior taxonomy. NineP.Conformance,
NineP.Fuzz and NineP.Benchmarks keep their executable project shapes. Full-scale workloads remain
local opt-ins, and allocation measurements keep their isolated xUnit collection.

The generated state models (`StateMachine`) run fixed seeds in CI. `NINEP_MODEL_SEED=<uint>`
replaces the seed, `NINEP_MODEL_STEPS=<count>` (at most 4096) the sequence length, and
`NINEP_MODEL_COMMANDS=<comma-separated uints>` replays a minimized command list from a failure
message exactly.
