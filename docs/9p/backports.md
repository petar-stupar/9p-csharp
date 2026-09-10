# Backports

The ledger of the backport policy in [loop.md](loop.md) §Backports (owner decision of 2026-09-08,
ARCHITECTURE.md Decision Log): an issue found in one port that is transferable to the ports
implemented before it becomes a numbered rule, and every earlier port gets a named test and a fix
before the loop advances. A row is closed when its **Pending** cell is empty.

| # | Rules | Origin | Found | Done | Pending |
| --- | --- | --- | --- | --- | --- |
| B-1 | Ref §8 rules 15–27 (dialect projection honesty) | 001 csharp, owner audit | 2026-09-08 | csharp — landed 2026-09-08 (improvement request IR-9, rule-index rows 78–119) | none: no earlier port exists |
| B-2 | Ref §8 rule 8 revised; rules 28–36 | 001 csharp, independent review of 2026-09-08 | 2026-09-08 | csharp — regression suites ClientLifetimeRegressionTests, ServerLifecycleRegressionTests, ServerSemanticsRegressionTests, FlushBackpressureRegressionTests and TransportHandshakeRegressionTests; rule-index rows 120–137 | none: no earlier port exists |
| B-3 | Client behaviour against real peers: a `.L` rename falls back to `Trename` when `Trenameat` is `EOPNOTSUPP` (diod), and an error on `NOTAG` answering a `Tversion` is a version error (diod); candidate rules for ref §5.1 and arch §6, plus the 9P2000 ename wording Linux v9fs accepts (`9p-csharp/tests/interop/README.md`) | 001 csharp, interop runs of 2026-09-10 against Linux v9fs, diod, p9ufs, plan9port | 2026-09-10 | csharp — landed 2026-09-10 (`ClientInteropRegressionTests`, rule-index rows 139–140; `InteropTests` opt-in per peer) | none: no earlier port exists; the ename wording is an open owner decision |
