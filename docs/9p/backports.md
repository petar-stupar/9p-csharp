# Backports

The ledger of the backport policy in [loop.md](loop.md) §Backports (owner decision of 2026-09-08,
ARCHITECTURE.md Decision Log): an issue found in one port that is transferable to the ports
implemented before it becomes a numbered rule, and every earlier port gets a named test and a fix
before the loop advances. A row is closed when its **Pending** cell is empty.

| # | Rules | Origin | Found | Done | Pending |
| --- | --- | --- | --- | --- | --- |
| B-1 | Ref §8 rules 15–27 (dialect projection honesty) | 001 csharp, owner audit | 2026-09-08 | csharp — landed 2026-09-08 (improvement request IR-9, rule-index rows 78–119) | none: no earlier port exists |
| B-2 | Ref §8 rule 8 revised; rules 28–36 | 001 csharp, independent review of 2026-09-08 | 2026-09-08 | csharp — regression suites ClientLifetimeRegressionTests, ServerLifecycleRegressionTests, ServerSemanticsRegressionTests, FlushBackpressureRegressionTests and TransportHandshakeRegressionTests; rule-index rows 120–137 | none: no earlier port exists |
