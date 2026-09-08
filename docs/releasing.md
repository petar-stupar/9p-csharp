# Releasing

How a version of `NineP.Protocol`, `NineP.Client` and `NineP.Server` reaches nuget.org. A release
starts from a **tag**, never from a push to `main`: a NuGet version is immutable, so publishing
per commit would need a version bump per commit or fail as a duplicate. Nightly or per-commit
packages are deliberately not published anywhere.

## The procedure

1. **Bump the version** in `Directory.Build.props` (`<Version>`), the one place it is stated; the
   packaging test and CI's scratch install read it from there. Semantic versioning, and a
   prerelease is a prerelease suffix: `0.2.0-rc.1`.
2. **Write the changelog.** Rename `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md) to
   `## [<version>] — <date>` and open a fresh `## [Unreleased]` above it. The section becomes the
   GitHub Release's notes verbatim, and the release refuses a version that has no section.
3. **Merge to `main`** through the ordinary pull request and its three-platform gate.
4. **Tag and push the tag** from the merge commit on `main`:

   ```text
   git switch main && git pull --ff-only
   git tag -a v0.1.0 -m "v0.1.0"
   git push origin v0.1.0
   ```

   The `release` workflow (`.github/workflows/release.yml`) runs on the tag. `workflow_dispatch`
   with the tag name is the manual fallback for a rerun.

## What the workflow checks before it publishes

- The tag is `v<Version>` for the version in `Directory.Build.props`.
- The tagged commit is on `main`.
- `CHANGELOG.md` has a non-empty `## [<version>]` section.
- None of the three ids already carries that version on nuget.org.
- The gate is green on that exact commit: locked restore, Release build at zero warnings, the
  whole test suite, `dotnet format`. The fuzz smoke and the scratch install stay in `ci`.

Only then does it pack, push the three `.nupkg` with their `.snupkg` symbol packages, and create
the GitHub Release with the packages attached. `--skip-duplicate` makes a rerun after a partial
push finish the packages that did not land rather than fail on the ones that did.

## Authentication

**Trusted Publishing** (preferred, no long-lived secret). On nuget.org, under your user name,
open *Trusted Publishing* and add a policy:

| Field | Value |
| --- | --- |
| Repository owner | `petar-stupar` |
| Repository | `9p-csharp` |
| Workflow file | `release.yml` (the file name only) |
| Environment | `release` |

Then add the repository secret `NUGET_USER` holding your nuget.org **profile name**, not your
e-mail. The workflow's `release` job requests an OIDC token, `NuGet/login` exchanges it for an
API key that lives one hour, and the push uses that. A policy for a repository nuget.org has not
seen yet is provisionally active for seven days; the first publish makes it permanent.

**API key** (fallback). If Trusted Publishing is not available on the account, create an API key
on nuget.org scoped to *push new packages and package versions* for the glob `NineP.*` only, store
it as the repository secret `NUGET_API_KEY`, and the workflow uses it instead. Rotate it; nuget.org
keys expire after a year at most.

The `release` GitHub environment is created on first use. Adding a required reviewer to it on
GitHub makes every publish wait for a click, which is a reasonable second guard for a one-person
project.

## Prefix reservation

The three ids are free on nuget.org (checked 2026-09-08). Reserving the `NineP.` prefix on
nuget.org (*Account → Manage Package ID Prefixes*) gives the packages the verified check mark and
stops anyone else publishing under it; it is a request from your account, reviewed by nuget.org,
and not something a workflow can do.

## Who can release

Only the owner: tags are refs, and the repository allows nobody else to push any. A fork cannot
trigger the workflow with this repository's secrets or Trusted Publishing policy either.

## If something goes wrong

- A failed check before the push changes nothing anywhere; fix, delete the tag locally and on
  GitHub, and tag again.
- A failure after some packages were pushed: rerun the workflow by `workflow_dispatch` with the
  same tag; `--skip-duplicate` skips what landed. A version that reached nuget.org cannot be
  replaced, only unlisted, so a broken release is followed by a fixed patch version.
