# Rule index

The live map from every normative rule, acceptance criterion and exit criterion to the **named
test** that pins it and the **task** that delivers it. It is a table so that a test can read it:
`RuleIndexTests.EveryRuleHasATest` resolves every `Test` cell by reflection over the built test
assemblies, and `RuleIndexTests.EveryAcHasATask` asserts that no acceptance-criterion or
exit-criterion row has an empty `Task` cell.

Sources: `Ref §n` is [docs/9p/protocol-reference.md](9p/protocol-reference.md); `Arch §n` is
[docs/9p/ARCHITECTURE.md](9p/ARCHITECTURE.md); `AC-…` are the ticket's acceptance criteria;
`Exit n` are the nine exit criteria of Arch §10. A `review` task marks a rule that came out of the
independent review of 2026-09-08 rather than a ticket task.

Reading the table:

- `Test` is an executable `TypeName.MethodName` from the current build/configuration. Missing
  types, methods, assemblies and skipped/non-test methods fail the gate.
- Only AC-f/Exit 7 may name `workflow:review`, and AC-csharp-2/Exit 9 may name `workflow:ci`.
  Those explicitly identified review/CI gates still carry a task. An em dash is never a valid test.
- `Task` names the implementation task or subsequent review that delivered the rule. Every
  numbered rule in reference §8 must appear in this table.

| # | Source | Rule | Test | Task |
| --- | --- | --- | --- | --- |
| 1 | Ref §4.1 | Qid is `type[1] version[4] path[8]`; the low two type bits follow Linux (`QTSYMLINK 0x02`, `QTLINK 0x01`) | `QidTests.TypeBitsFollowLinux` | 5 |
| 2 | Ref §4.1 | The qid type byte equals the high 8 bits of the file's mode word | `AttrProjectorTests.QidTypeMirrorsModeHighBits` | 17 |
| 3 | Ref §4.1 | `.L` qid type is `S_IFDIR → QTDIR`, `S_IFLNK → QTSYMLINK`, everything else `QTFILE` | `AttrProjectorTests.DotLQidTypeFromPosixFileType` | 17 |
| 4 | Ref §4.2 | A stat record carries its size twice; the inner `size[2]` is `n − 2` | `StatCodecTests.InnerSizeEqualsOuterMinusTwo` | 7 |
| 5 | Ref §4.2 | A `Twstat` whose every field is "don't touch" is an fsync request | `StatRecordTests.AllDontTouchIsFsyncRequest` | 6 |
| 6 | Ref §4.2 | `length` is 0 for directories | `AttrProjectorTests.DirectoryLengthIsZero` | 17 |
| 7 | Ref §4.3 | An `Rreaddir` entry is `qid[13] offset[8] type[1] name[s]` and is never split | `DirectoryPackerTests.EntriesAreNeverSplit` | 30 |
| 8 | Ref §4.3 | Listings never contain `.` or `..` in either record format | `DirectoryPackerTests.NoDotOrDotDotEntries` | 30 |
| 9 | Ref §4.4 | The mode bits of a 9P2000 / `.u` stat record have the reference's values | `ModeBitsTests.ValuesMatchReference` | 17 |
| 10 | Ref §4.5 | The access mode is the low two bits; `OAPPEND (0x80)` is a flag, not a mode | `OpenModeTests.AppendIsAFlagNotAnAccessMode` | 5 |
| 11 | Ref §4.5 | Any other set bit in `Topen.mode` draws `Rerror "bad open mode"` | `OpenStateTests.BadOpenModeRejected` | 29 |
| 12 | Ref §4.5 | `.L` open flags: access mode, `O_TRUNC`, `O_APPEND`, `O_EXCL`, `O_DIRECTORY`, `O_NOFOLLOW` honoured, the rest ignored | `OpenStateTests.DotLFlagsHonoured` | 29 |
| 13 | Ref §4.6 | `Tgetattr.request_mask` / `Rgetattr.valid` bit values, `BASIC 0x7FF`, `ALL 0x3FFF` | `GetAttrMaskTests.ValuesMatchReference` | 6 |
| 14 | Ref §4.6 | `Rgetattr` is always 160 bytes; fields not marked valid carry 0 | `GoldenVectorTests.RgetattrIs160Bytes` | 12 |
| 15 | Ref §4.6 | A `Tsetattr` time bit without its `_SET` twin means "use the server's clock" | `SetAttrTests.TimeWithoutSetUsesServerClock` | 17 |
| 16 | Ref §4.7 | `.L` create and mkdir modes are masked with `07777` | `AttrProjectorTests.CreateModeMaskedTo07777` | 32 |
| 17 | Ref §4.8 | Lock type, flag and status constants have the reference's values; `length 0` is "to end of file" | `LockTests.ConstantsMatchReference` | 6 |
| 18 | Ref §4.9 | Synthetic servers report `V9FS_MAGIC (0x01021997)` and `namelen 255` | `StatFsTests.SyntheticServersReportV9fsMagic` | 32 |
| 19 | Ref §5.1 | Every negotiation step is gated on the configured dialect set | `NegotiationTests.EveryStepIsGatedOnTheConfiguredSet` | 16 |
| 20 | Ref §5.1 | An `"unknown"` reply echoes the client's msize, never the server's | `NegotiationTests.UnknownEchoesClientMsize` | 16 |
| 21 | Ref §5.1 | `msize < 4096` is refused with `"unknown"`, not with `Rerror` and not by answering the floor | `NegotiationTests.MsizeBelowFloorIsUnknown` | 16 |
| 22 | Ref §5.1 | A version string with a suffix strips to `9P2000` | `NegotiationTests.VersionSuffixIsStripped` | 16 |
| 23 | Ref §5.1 | The 56-row oracle in `docs/negotiation.json` is generated from the shipped negotiator | `NegotiationTests.OracleMatchesCommittedFile` | 16 |
| 24 | Ref §5.2 | An afid carries the triple `(uname, n_uname, aname)`; a mismatched `Tattach` is refused | `AfidTests.DifferentNUnameRejected` | 33 |
| 25 | Ref §5.2 | The session identity is the authenticator's, never the claimed `uname` | `AfidTests.IdentityComesFromTheAuthenticator` | 33 |
| 26 | Ref §5.2 | `Tauth` refusal is `Rerror "authentication not required"` / `Rlerror ECONNREFUSED` | `AfidTests.AuthNotRequiredRefusalShape` | 33 |
| 27 | Ref §5.3 | A flushed request is answered exactly once and `Rflush` follows it | `ServerFlushTests.FlushedRequestAnsweredOnce` | 31 |
| 28 | Ref §5.4 | A partial walk returns the qid prefix and does not bind `newfid` | `WalkTests.PartialWalkReturnsPrefix` | 29 |
| 29 | Ref §5.4 | An open fid cannot be cloned | `WalkTests.CannotCloneOpenFid` | 29 |
| 30 | Ref §5.5 | `iounit` is `msize − IOHDRSZ` on every open and create reply | `OpenStateTests.IounitIsMsizeMinusIohdrsz` | 29 |
| 31 | Ref §5.6 | `msize − IOHDRSZ` clamps `Tread` / `Treaddir` and shortens `Twrite`; it is never a validity bound | `TwriteTests.ServerBoundShortensWrite` | 32 |
| 32 | Ref §5.7 | A clunked fid is gone even when the remove fails | `ClunkRemoveTests.FidGoneEvenWhenRemoveFails` | 29 |
| 33 | Ref §5.8 | A `wstat` is atomic: all changes or none | `WstatTests.AllOrNothing` | 32 |
| 34 | Ref §5.9 | A `.L` session never sees an `Rerror`; every error is an `Rlerror` errno | `ErrorProjectionTests.DotLAlwaysUsesRlerror` | 17 |
| 35 | Ref §5.9 | `Tread` on a directory is an error in `.L` | `ServerDispatchTests.DotLReadOnDirectoryIsAnError` | 32 |
| 36 | Ref §8.1 | `size < 7` or `size >` the active bound closes the connection | `FrameReaderTests.SizeLieClosesConnection` | 14 |
| 37 | Ref §8.1 | The pre-negotiation frame cap is the constant 8192, never the configured maximum | `FrameReaderTests.PreNegotiationCapIs8192` | 14 |
| 38 | Ref §8.2 | Counted fields, `nwname`, trailing bytes and the stat inner size are checked before any allocation | `MutationMatrixTests.EveryMutationYieldsATypedError` | 13 |
| 39 | Ref §8.2 | A `Tfsync` of exactly 11 bytes is the sole legal short frame | `TfsyncTests.ShortFormLegalOnlyForTfsync` | 11 |
| 40 | Ref §8.3 | Strings must be valid UTF-8 | `WireReaderTests.RejectsInvalidUtf8` | 5 |
| 41 | Ref §8.3 | Strings must contain no NUL | `WireReaderTests.RejectsNulInString` | 5 |
| 42 | Ref §8.3 | Names must not contain `/`, must not be `.`, and are at most 255 bytes; `..` is legal only in `Twalk` | `WireReaderTests.RejectsSlashInName` | 5 |
| 43 | Ref §8.4 | `Twrite.count` must equal `size − 23` | `MutationMatrixTests.TwriteCountDisagreesWithSize` | 13 |
| 44 | Ref §8.4 | A maximal legal `Twrite` (`count == msize − 23`) is accepted | `TwriteTests.MaximalLegalWriteIsAccepted` | 32 |
| 45 | Ref §8.5 | `Twrite.offset + count`, `Tsetattr.size` and `Tlock.start + length` are guarded against `u64` overflow | `OverflowTests.WriteOffsetOverflowRejected` | 7 |
| 46 | Ref §8.6 | A duplicate tag draws `Rerror "duplicate tag"` and the new request is dropped | `TagTableTests.DuplicateTagRejected` | 28 |
| 47 | Ref §8.6 | `NOTAG` outside `Tversion` is an ordinary tag | `TagTableTests.NotagIsOrdinaryOutsideVersion` | 28 |
| 48 | Ref §8.7 | The per-connection fid table is bounded; overflow is `"too many fids"` / `ENFILE` | `FidTableTests.CapOverflow` | 28 |
| 49 | Ref §8.8 | Two in-flight bounds, per connection and per listener, with a flush reserve | `BackpressureTests.FlushAnsweredWhileWindowFull` | 31 |
| 50 | Ref §8.8 | The listener-wide bound stops the offending connection only | `BackpressureTests.ListenerWideBoundHolds` | 31 |
| 51 | Ref §8.9 | Before a dialect is agreed the only accepted message is `Tversion`; anything else draws a 9P2000-shaped `Rerror` and a close | `ServerVersionTests.PreNegotiationErrorIs9P2000Shaped` | 27 |
| 52 | Ref §8.9 | A `Tversion` mid-session resets the whole session | `ServerVersionTests.SecondTversionResetsSession` | 27 |
| 53 | Ref §8.10 | Errors carry no internals; `ename` is truncated to `ERRMAX − 1` bytes on a rune boundary | `ErrorTests.EnameTruncatedAtRuneBoundary` | 17 |
| 54 | Ref §8.11 | Untrusted strings are escaped and capped at 256 bytes before they are logged | `UntrustedTextTests.UntrustedStringsEscapedAndCapped` | 17 |
| 55 | Ref §8.12 | A reply with an unknown tag, an unexpected type, or a size over msize terminates the client session | `TagMultiplexerTests.UnknownTagTerminatesSession` | 23 |
| 56 | Ref §8.13 | An over-count `Rread` / `Rreaddir` / `Rwrite`, `nwqid > nwname`, a bad stat size and a split dirent are client protocol errors | `ClientProtocolErrorTests.OverCountRepliesAreRejected` | 26 |
| 57 | Ref §8.14 | The client waits for `Rflush` before reusing `oldtag` | `ClientFlushTests.WaitsForRflushBeforeTagReuse` | 24 |
| 58 | Ref §8.14 | A reply that arrives before the `Rflush` is delivered normally | `ClientFlushTests.DeliversRaceyReply` | 24 |
| 59 | Ref §9 | The 77 golden vectors decode and re-encode byte-exactly | `GoldenVectorTests.EveryVectorRoundTripsByteExactly` | 12 |
| 60 | AC-a | `jsonfs` plus the repo's `cli` reproduce `sample.expected.txt` byte-for-byte over tcp, tls and ws in all three dialects | `ConformanceTests.SampleOutputMatchesByteForByte` | 36 |
| 61 | AC-b | A hostile client cannot exceed the tested per-connection allocation bound proportional to msize or stall another connection | `HostileClientTests.AllocationStaysWithinMsize` | 40 |
| 62 | AC-c | `todofs`: user A's attach cannot reach anything under `/users/B`, and `users/ctl` needs the admin role | `TodoFsIsolationTests.UserACannotReachUserB` | 38 |
| 63 | AC-d | The mutation matrix yields typed errors with nothing escaping the codec, and both `Tfsync` forms decode | `MutationMatrixTests.EveryMutationYieldsATypedError` | 13 |
| 64 | AC-e | `docs/benchmarks.md` carries measured numbers for the four benchmarks of Arch §9 | `BenchmarkDocTests.MeasuredNumbersArePresent` | 41 |
| 65 | AC-f | Code review ≥ 10/12 and security review ≥ 8/10 | workflow:review | 44 |
| 66 | AC-csharp-1 | The packed artefacts install into a scratch project that runs the README's examples | `PackagingTests.ScratchInstallRunsReadmeExamples` | 44 |
| 67 | AC-csharp-2 | Build, test and format are green at zero warnings on macOS and Linux CI | workflow:ci | 3 |
| 68 | AC-csharp-3 | `docs/interop.md` lists every peer as pass / fail / not run with a reason | `InteropDocTests.EveryPeerHasAVerdict` | 43 |
| 69 | Exit 1 | Three installable packages build from a clean checkout | `PackagingTests.ThreePackagesPack` | 44 |
| 70 | Exit 2 | Every message type encodes and decodes; the vectors round-trip; the mutation matrix is typed | `GoldenVectorTests.EveryVectorRoundTripsByteExactly` | 12 |
| 71 | Exit 3 | Client and server pass the conformance scenario in all three dialects over tcp, tls, ws and memory | `ConformanceTests.AllDialectsAllTransports` | 36 |
| 72 | Exit 4 | Interop against every merged language and the external peers, or "not run: reason" | `InteropDocTests.EveryPeerHasAVerdict` | 43 |
| 73 | Exit 5 | Default auth and a custom authenticator are exercised end to end; bad tokens are rejected | `KeycloakAuthenticatorTests.RejectsBadTokens` | 39 |
| 74 | Exit 6 | `jsonfs` and `todofs` run with README walk-throughs a reader can paste | `ReadmeSnippetTests.SnippetsRunAndMatch` | 42 |
| 75 | Exit 7 | Arch §8 security items 1–7 are green and both reviews pass | workflow:review | 42 |
| 76 | Exit 8 | Every document of Arch §10.8 exists | `DocsTests.EveryRequiredDocExists` | 42 |
| 77 | Exit 9 | CI on Linux and macOS: build, tests, lint at zero warnings, fuzz smoke, package step, audit | workflow:ci | 3 |
| 78 | Ref §8.15 | The client refuses remove-on-close on `.L` before anything is sent | `ClientProjectionTests.TheRemoveOnCloseFlagNeverReachesADotLOpen` | IR-9 |
| 79 | Ref §8.15 | `O_EXCL`, `O_DIRECTORY` and `O_NOFOLLOW` are refused on 9P2000 and `.u`, nothing reaches the wire | `ClientProjectionTests.LinuxOnlyOpenFlagsNeverReachA9P2000Server` | IR-9 |
| 80 | Ref §8.15 | `Tsetattr` has no name and no textual group: both are `EINVAL` on `.L` | `AttrProjectorTests.SetattrHasNoNameOrGroupName` | IR-9 |
| 81 | Ref §8.15 | A `Twstat` cannot set `atime` (`EPERM`) or ask for the server's clock (`EINVAL`); the latter is never sent as an fsync | `ClientProjectionTests.AServerClockUpdateIsNotSentAsAnFsync` | IR-9 |
| 82 | Ref §8.15 | A numeric gid needs `.u`; plain 9P2000 refuses it with `EINVAL` | `ClientProjectionTests.ANumericGroupIsRefusedOnPlain9P2000` | IR-9 |
| 83 | Ref §8.15 | A rename across directories is refused on 9P2000 and `.u`, and nothing moves | `ClientFileApiTests.ARenameAcrossDirectoriesIsRefusedOutsideDotL` | IR-9 |
| 84 | Ref §8.15 | Symlink, statfs, lock and xattr calls are `EOPNOTSUPP` outside `.L` | `ClientFileApiTests.LinuxExtrasAreEopnotsuppElsewhere` | IR-9 |
| 85 | Ref §8.16 | `OEXEC` on `.L` is sent as `O_RDONLY`; access mode 3 never leaves the client | `ClientProjectionTests.ExecOpensReadOnlyOnADotLServer` | IR-9 |
| 86 | Ref §8.17 | The client reads only the fields an `Rgetattr` marks valid; an unmarked one keeps the `Attr` default | `ClientProjectionTests.AnUnmarkedGetattrFieldKeepsItsDefault` | IR-9 |
| 87 | Ref §8.17 | `Attr.Kind` and the qid type byte agree: a `QTSYMLINK` qid on plain 9P2000 is a symlink | `ClientProjectionTests.A9P2000SymlinkQidIsReportedAsASymlink` | IR-9 |
| 88 | Ref §8.18 | The `Rversion` must answer the offer that drew it; a higher or sideways dialect is a version error | `ClientProjectionTests.AnAnswerThatIsNotTheOfferIsAVersionError` | IR-9 |
| 89 | Ref §8.18 | The suffix-stripping downgrade to `9P2000` is the only one, and `MinDialect` gates it | `ClientProjectionTests.TheSuffixStrippingDowngradeIsTheOnlyOne` | IR-9 |
| 90 | Ref §8.18 | An `Rversion` with no `Tversion` outstanding terminates the session | `ClientProjectionTests.AnUnsolicitedRversionTerminatesTheSession` | IR-9 |
| 91 | Ref §8.19 | `Tcreate.perm` carrying `DMAPPEND`, `DMEXCL` or `DMTMP` is refused with `EPERM`, not masked | `CreateTests.CreateWithAnUnsupportedModeBitIsRefused` | IR-9 |
| 92 | Ref §8.19 | A `Twstat` that changes `DMAPPEND`, `DMEXCL` or `DMTMP` is refused; a bit echoed back unchanged is a no-op | `WstatTests.ChangingAnUnsupportedModeBitIsRefused` | IR-9 |
| 93 | Ref §8.20 | `Tunlinkat` needs `AT_REMOVEDIR` for a directory (`EISDIR`) and refuses it on anything else (`ENOTDIR`) | `UnlinkatTests.DirectoryWithoutRemovedirIsEisdir` | IR-9 |
| 94 | Ref §8.20 | Any other `Tunlinkat` flag bit is `EINVAL` | `UnlinkatTests.AnUnknownFlagIsEinval` | IR-9 |
| 95 | Ref §8.21 | `Txattrcreate` with `attr_size` 0 removes the attribute | `DispatcherTests.XattrcreateWithZeroSizeRemovesTheAttribute` | IR-9 |
| 96 | Ref §8.22 | `Rgetattr.valid` marks `btime`, `gen` and `data_version` only when the handler supplied a non-zero value | `GetattrTests.FieldsTheHandlerLeftZeroAreNotMarkedValid` | IR-9 |
| 97 | Ref §8.23 | Opening a symlink is `ELOOP`, with or without `O_NOFOLLOW` | `OpenStateTests.OpenOfASymlinkIsEloop` | IR-9 |
| 98 | Ref §8.23 | Opening a fifo, socket or device the server cannot open is `ENXIO`; the fid is never marked open | `OpenStateTests.OpenOfAFifoIsEnxio` | IR-9 |
| 99 | Ref §8.23 | `O_DIRECTORY` on anything but a directory is `ENOTDIR` | `OpenStateTests.ODirectoryOnAPlainFileIsEnotdir` | IR-9 |
| 100 | Ref §8.24 | A `.u` `DMDEVICE` create parses `"b maj min"` / `"c maj min"` into `rdev`; a malformed extension is `EINVAL` | `CreateTests.DeviceCreateParsesTheExtension` | IR-9 |
| 101 | Ref §8.25 | A directory create with `OTRUNC` or `ORCLOSE` is refused exactly as the open would be | `CreateTests.DirectoryCreateRefusesTruncateAndRemoveOnClose` | IR-9 |
| 102 | Ref §8.26 | A handler's clunk-time error is the reply, and the fid is freed regardless | `ClunkRemoveTests.AHandlersClunkErrorIsTheReply` | IR-9 |
| 103 | Ref §8.27 | jsonfs applies a `Twstat` whole: a length of zero and a name in one record perform both | `JsonFsTests.SetAttrAppliesATruncationAndARenameTogether` | IR-9 |
| 104 | Ref §8.27 | jsonfs refuses an update whole when it names a field it cannot honour, and nothing changes | `JsonFsTests.SetAttrWithAnUnsupportedFieldChangesNothing` | IR-9 |
| 105 | Ref §8.27 | todofs truly empties a free-text field on a length of zero | `TodoFsSetAttrTests.SetAttrLengthZeroEmptiesAFreeTextField` | IR-9 |
| 106 | Ref §8.27 | todofs refuses a length of zero where the file has no zero-length value (`status`, `/users/ctl`) | `TodoFsSetAttrTests.SetAttrLengthZeroOnStatusIsRefused` | IR-9 |
| 107 | Ref §8.27 | todofs refuses an update whole when it names a field it cannot honour | `TodoFsSetAttrTests.SetAttrWithAnUnsupportedFieldChangesNothing` | IR-9 |
| 108 | Ref §8.27 | todofs performs an `OTRUNC` truncation at the open, not at a write that may never arrive | `TodoFsSetAttrTests.ATruncatingOpenClunkedWithoutAWriteEmptiesTheField` | IR-9 |
| 109 | Ref §8.27 | The documented exception: a truncating open of `/users/ctl` keeps the users | `TodoFsSetAttrTests.ATruncatingOpenOfTheControlFileKeepsTheUsers` | IR-9 |
| 110 | Arch §7 | todofs reads answer from the database, on the fid that just wrote too | `TodoFsCtlTests.AReadAfterAWriteAnswersTheUserList` | IR-9 |
| 111 | Arch §7 | cli: a TLS flag with a plaintext address is a usage error, not a certificate loaded and dropped | `CliExitCodeTests.TlsFlagWithAPlaintextAddressIsUsageError` | IR-9 |
| 112 | Arch §7 | cli: `-l` belongs to `ls` alone | `CliExitCodeTests.LongListingOnAnythingButLsIsUsageError` | IR-9 |
| 113 | Arch §7 | cli: `--oidc-*` need an oidc grant | `CliExitCodeTests.OidcFlagWithoutAnOidcGrantIsUsageError` | IR-9 |
| 114 | Arch §7 | cli: the `--auth-optional` fallback says so on standard error, and standard output is unchanged | `CliExitCodeTests.AuthOptionalFallbackSaysItAttachedAnonymously` | IR-9 |
| 115 | Arch §7 | jsonfs and todofs warn about a `--tls-*` or `--ws-origin` flag with no matching listener | `ExampleHostTests.UnusedTransportFlagsAreWarnedAbout` | IR-9 |
| 116 | Arch §7 | jsonfs `--write-back` implies `--writable`, and the usage text says so | `ExampleHostTests.WriteBackImpliesWritableAndSaysSo` | IR-9 |
| 117 | Ref §8.19 | `.u` `DMSETUID` / `DMSETGID` / `DMSETVTX` in a `Twstat` reach the handler as the `07777` bits and round-trip | `WstatTests.TheDotUPermissionBitsRoundTripThroughWstat` | IR-9 |
| 118 | Ref §8.15 | Plain 9P2000 refuses a setuid, setgid or sticky chmod before the wire | `AttrProjectorTests.PlainNineP2000RefusesTheUnixPermissionBits` | IR-9 |
| 119 | Ref §8.23 | `ENXIO` has an `ErrorTable` row, so a 9P2000 peer recovers it rather than `EIO` | `ErrorTableTests.AWrittenEnameNeverDegradesToEio` | IR-9 |
| 120 | Ref §8.28 | Closing a server fid drains its active operations | `ServerLifecycleRegressionTests.ClosingAFileWaitsForItsActiveWrite` | review |
| 121 | Ref §8.28 | A disposed client handle cannot target a recycled fid | `ClientLifetimeRegressionTests.ReleasedHandlesCannotUseRecycledFids` | review |
| 122 | Ref §8.29 | An xattr commit refusal reaches the caller | `ClientLifetimeRegressionTests.RealServerXattrHandlerCommitRefusalReachesCaller` | review |
| 123 | Ref §8.30 | Concurrent append writes across connections preserve both payloads | `ServerLifecycleRegressionTests.AppendWritesAcrossConnectionsDoNotOverwriteEachOther` | review |
| 124 | Ref §8.30 | Append short writes retry only unacknowledged bytes | `ClientLifetimeRegressionTests.AppendTransferRetriesOnlyTheUnacknowledgedSuffix` | review |
| 125 | Ref §8.31 | Split and cloned walks preserve every ancestor | `ServerLifecycleRegressionTests.DeepClonedWalkPreservesAllAncestors` | review |
| 126 | Ref §8.31 | A later permission failure returns the walked prefix without changing fids | `ServerLifecycleRegressionTests.PartialPermissionFailureDoesNotChangeFids` | review |
| 127 | Ref §8.32 | Permission class selection follows the negotiated dialect | `ServerLifecycleRegressionTests.PermissionClassesFollowTheDialect` | review |
| 128 | Ref §8.33 | Xattr names and values require read access before handler invocation | `ServerLifecycleRegressionTests.XattrReadsCheckPermissionsBeforeCallingTheHandler` | review |
| 129 | Ref §8.34 | A nonpositive window fails before dialing or negotiation | `ClientLifetimeRegressionTests.InvalidWindowFailsBeforeDialOrNegotiation` | review |
| 130 | Ref §8.35 | A silent peer cannot block a healthy handshake and connections remain capped | `TransportHandshakeRegressionTests.SilentPeerDoesNotBlockHealthyHandshakeAndPendingPlusActiveConnectionsStayCapped` | review |
| 131 | Ref §8.35 | Completed sessions do not accumulate in the listener | `ServerLifecycleRegressionTests.ListenerRetainsOnlyActiveSessions` | review |
| 132 | Ref §8.36 | Custom roots retain peer-role EKU restrictions | `TransportHandshakeRegressionTests.CustomRootPreservesPeerRoleOnCaIssuedLeaf` | review |
| 133 | Ref §8.36 | Custom roots retain intermediate EKU restrictions | `TransportHandshakeRegressionTests.IntermediatePurposeRestrictionCannotBeRepairedByCustomTrust` | review |
| 134 | Ref §8.31 | Cross-connection Trename and Trenameat update existing aliases and descendants | `ServerLifecycleRegressionTests.RenamedAncestorRebasesExistingAliasesAcrossConnections` | review |
| 135 | Ref §8.31 | Moving outside a subtree attach cannot make dot-dot escape its root | `ServerLifecycleRegressionTests.RenameOutsideRestrictedAttachClampsParentWalkAndKeepsRemovalLocation` | review |
| 136 | Ref §8.31 | A slow lookup does not block unrelated clients and revalidates when rename races it | `ServerLifecycleRegressionTests.SlowLookupDoesNotBlockOtherClientsAndRevalidatesAfterRename` | review |
| 137 | Ref §8.28 | Shutdown retains gates and handlers until inline flush and the reader unwind | `SessionShutdownRegressionTests.ShutdownKeepsFlushGatesAndHandlersAliveUntilInlineReaderUnwinds` | review |
| 138 | Ref §8.8 | A budget is returned when the reply is queued, so a window reused the instant its reply arrives is never refused | `BackpressureTests.AWindowReusedTheInstantItsReplyArrivesIsNeverRefused` | 31 |
