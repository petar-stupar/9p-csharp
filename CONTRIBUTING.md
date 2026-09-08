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
- the README snippets compile and print what the README says they print.

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
