# Backports

The ledger of the backport policy in [loop.md](loop.md) §Backports (owner decision of 2026-09-08,
ARCHITECTURE.md Decision Log): an issue found in one port that is transferable to the ports
implemented before it becomes a numbered rule, and every earlier port gets a named test and a fix
before the loop advances. A row is closed when its **Pending** cell is empty.

| # | Rules | Origin | Found | Done | Pending |
| --- | --- | --- | --- | --- | --- |
| B-1 | Ref §8 rules 15–27 (dialect projection honesty) | 001 csharp, owner audit | 2026-09-08 | csharp — landed 2026-09-08 (improvement request IR-9, rule-index rows 78–119) | none: no earlier port exists |
| B-2 | Ref §8 rule 8 revised; rules 28–36 | 001 csharp, independent review of 2026-09-08 | 2026-09-08 | csharp — regression suites ClientLifetimeRegressionTests, ServerLifecycleRegressionTests, ServerSemanticsRegressionTests, FlushBackpressureRegressionTests and TransportHandshakeRegressionTests; rule-index rows 120–137 | none: no earlier port exists |
| B-3 | Ref §8 rules 37–39: a `.L` rename falls back to `Trename` when `Trenameat` is `EOPNOTSUPP` (diod), an error on `NOTAG` answering a `Tversion` is a version error (diod), and every 9P2000 ename is one Linux v9fs maps to the same errno (`fixtures/linux-9p-errors.json`) | 001 csharp, interop runs of 2026-09-10 against Linux v9fs, diod, p9ufs, plan9port | 2026-09-10 | csharp — landed 2026-09-10 (`ClientInteropRegressionTests`, `ErrorTableTests`; rule-index rows 139, 140, 155; `InteropTests` opt-in per peer); promoted to numbered rules the same day | none: no earlier port exists |
| B-4 | Ref §8 rule 19 revised and arch §12 shapes: the settable file flags reach the handler (`CreateRequest.file_flags`, `SetAttr.flags`) and are read back; `DMAUTH` / `DMMOUNT` refused; the client completes a half-stated `Twstat` mode word and refuses the flags in `.L` | 001 csharp, owner decision of 2026-09-10 against open(2) and stat(5) | 2026-09-10 | csharp — landed 2026-09-10 (`CreateTests`, `WstatTests`, `ClientProjectionTests`, `AttrProjectorTests`; rule-index rows 91, 92, 141–154) | none: no earlier port exists |
