# Shared test index

**Generated from [test-index.json](test-index.json) by `gen-test-index-md.mjs`. Edit the
JSON, not this file.**

Language-neutral test obligations shared by every 9P port. An id names a behaviour, not a test method: each port maps the id onto its own test in its docs/test-map.json. Seeded from the C# reference port (workspace ARCHITECTURE.md §12); thereafter authored by hand, and a test added in any port for a transferable behaviour is added here and owed by every port.

794 obligations across 43 areas: **753 required**,
**41 recommended**.

- **required** — Every port must have a test for this behaviour before it is done.
- **recommended** — Every port should; a port that skips one records the reason in its docs/test-map.json "skipped" map. Typically needs a facility not every ecosystem has: property testing, an external peer, an OIDC issuer, a scale budget.

| Area | Obligations | Required | Recommended |
| --- | ---: | ---: | ---: |
| `attr` | 68 | 68 | 0 |
| `auth` | 61 | 42 | 19 |
| `boundary` | 28 | 28 | 0 |
| `cli` | 34 | 34 | 0 |
| `client-api` | 24 | 24 | 0 |
| `clunk` | 7 | 7 | 0 |
| `codec` | 64 | 54 | 10 |
| `codec-robustness` | 36 | 36 | 0 |
| `constants` | 4 | 4 | 0 |
| `create` | 10 | 10 | 0 |
| `dialect` | 26 | 26 | 0 |
| `directory` | 16 | 16 | 0 |
| `dispatch` | 7 | 7 | 0 |
| `docs` | 2 | 0 | 2 |
| `error` | 27 | 27 | 0 |
| `examples` | 3 | 0 | 3 |
| `fid` | 30 | 29 | 1 |
| `flush` | 21 | 21 | 0 |
| `handler` | 3 | 3 | 0 |
| `interop` | 6 | 2 | 4 |
| `jsonfs` | 30 | 30 | 0 |
| `limits` | 22 | 22 | 0 |
| `lock` | 3 | 3 | 0 |
| `mode` | 10 | 10 | 0 |
| `names` | 3 | 3 | 0 |
| `observability` | 10 | 10 | 0 |
| `open` | 12 | 12 | 0 |
| `permission` | 10 | 10 | 0 |
| `projection` | 19 | 19 | 0 |
| `qid` | 3 | 3 | 0 |
| `remove` | 5 | 5 | 0 |
| `scale` | 1 | 0 | 1 |
| `semantics` | 9 | 9 | 0 |
| `server-lifecycle` | 27 | 27 | 0 |
| `session` | 4 | 4 | 0 |
| `statfs` | 3 | 3 | 0 |
| `tag` | 19 | 18 | 1 |
| `todofs` | 37 | 37 | 0 |
| `transport` | 51 | 51 | 0 |
| `transport-tls` | 13 | 13 | 0 |
| `transport-websocket` | 14 | 14 | 0 |
| `walk` | 8 | 8 | 0 |
| `write` | 4 | 4 | 0 |

## `attr`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-9p2000-symlink-qid-yields-a-symlink-kind` | protocol | required |  |  |
| `a-stated-owner-reaches-dot-l-unchanged` | protocol | required | `8.42` |  |
| `all-dont-touch-is-an-fsync` | server | required |  |  |
| `all-dont-touch-wstat-is-an-fsync-request` | protocol | required |  |  |
| `all-or-nothing` | server | required | `5.8` |  |
| `an-unstated-owner-is-not-projected-as-an-id-in-dot-l` | protocol | required | `8.42` |  |
| `an-unstated-owner-is-still-the-sentinel-in-dot-u` | protocol | required | `8.42` |  |
| `basic-and-all-are-the-unions-the-reference-says` | protocol | required |  |  |
| `c-time-is-always-the-server-clock` | protocol | required |  |  |
| `changing-a-file-flag-reaches-the-handler` | server | required | `8.19` |  |
| `complete-mode-fills-the-unstated-half-from-the-record` | protocol | required |  |  |
| `create-mode-masked-to07777` | protocol | required | `4.7` |  |
| `directory-length-is-zero` | protocol | required | `4.2` |  |
| `directory-size-is-refused-before-any-part-of-the-update` | server | required | `5.8 / §4.6` |  |
| `dmdir-cannot-change` | server | required |  |  |
| `dot-l-qid-type-from-posix-file-type` | protocol | required | `4.1` |  |
| `dot-u-device-extension-is-the-reference-text` | protocol | required |  |  |
| `dot-u-permission-bits-round-trip-through-wstat` | server | required | `8.19` |  |
| `dot-u-qid-bits-are-linux` | protocol | required |  |  |
| `dot-u-symlink-still-carries-its-target` | protocol | required |  |  |
| `field-echoed-back-unchanged-is-a-no-op` | server | required |  |  |
| `fields-the-handler-left-zero-are-not-marked-valid` | server | required | `8.22` |  |
| `fields-the-handler-supplied-are-marked-valid` | server | required |  |  |
| `flag-echoed-back-unchanged-is-a-no-op` | server | required | `8.19` |  |
| `flag-update-the-handler-dropped-is-refused` | server | required | `8.19` |  |
| `flags-only-update-is-not-an-fsync` | protocol | required |  |  |
| `getattr-reads-every-marked-field` | protocol | required |  |  |
| `getattr-reads-only-the-marked-fields` | protocol | required |  |  |
| `half-stated-mode-word-is-refused` | protocol | required | `8.19` |  |
| `is-160-bytes` | protocol | required |  |  |
| `legacy-directory-length-zero-reaches-the-handler` | server | required | `5.8` |  |
| `numeric-group-needs-dot-u` | protocol | required |  |  |
| `only-marked-fields-are-projected` | protocol | required |  |  |
| `only-the-owner-may-set-a-flag` | server | required | `5.8` |  |
| `orphan-time-modifiers-are-einval-without-mutation` | server | required | `4.6` |  |
| `owner-cannot-change` | server | required |  |  |
| `owner-may-change-mode-and-length` | server | required |  |  |
| `plain-9p2000-refuses-the-unix-permission-bits` | protocol | required | `8.15` |  |
| `plain-9p2000-wstat-has-only-the-rwx-bits` | protocol | required |  |  |
| `posix-mode-carries-the-file-type` | protocol | required |  |  |
| `qid-type-mirrors-mode-high-bits` | protocol | required | `4.1` |  |
| `rgetattr-is-160-bytes` | protocol | required | `4.6` |  |
| `rgetattr-qid-is-always-valid` | protocol | required |  |  |
| `rgetattr-valid-is-requested-and-supplied` | protocol | required |  |  |
| `server-owned-bit-cannot-be-set-by-a-wstat` | server | required | `8.19` |  |
| `server-owned-flag-is-refused-by-the-projector` | protocol | required |  |  |
| `set-attr-values-match-reference` | protocol | required |  |  |
| `setattr-has-no-name-or-group-name` | protocol | required | `8.15` |  |
| `setuid-chmod-is-refused-on-plain9-p2000` | server | required |  |  |
| `short-frame-is-rejected` | protocol | required |  |  |
| `stat-record-is-not-a-dot-l-shape` | protocol | required |  |  |
| `symlink-stat-fails-on9-p2000` | protocol | required |  |  |
| `time-without-set-uses-server-clock` | protocol | required | `4.6` |  |
| `to-wstat-sends-the-file-flags-in-the-mode-word` | protocol | required | `8.19` |  |
| `to-wstat-sends-the-unix-permission-bits-in-dot-u` | protocol | required |  |  |
| `unix-permission-bits-only-exist-in-dot-u` | protocol | required |  |  |
| `unmarked-mode-takes-the-kind-from-the-qid` | protocol | required |  |  |
| `unsettable-field-is-refused` | server | required |  |  |
| `valid-is-the-intersection-with-the-request-mask` | server | required |  |  |
| `valid-mask-is-the-reference-basic-set` | protocol | required |  |  |
| `valid-mask-of-zero-changes-nothing-and-does-not-fsync` | server | required | `4.6` |  |
| `values-match-reference` | protocol | required | `4.4`, `4.6` |  |
| `wstat-cannot-ask-for-the-server-clock` | protocol | required |  |  |
| `wstat-cannot-set-atime` | protocol | required |  |  |
| `wstat-carries-the-unix-permission-bits-in-dot-u` | protocol | required |  |  |
| `wstat-never-changes-the-owner` | protocol | required |  |  |
| `wstat-projects-only-the-settable-fields` | protocol | required |  |  |
| `wstat-projects-the-unix-group` | protocol | required |  |  |

## `auth`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `afid-exchange-times-out` | server | required |  |  |
| `afid-write-bounded-at64-kib` | server | required |  |  |
| `anonymous-identity-carries-no-groups` | protocol | required |  |  |
| `attach-claiming-another-user-is-refused` | client | recommended |  |  |
| `attach-uname-must-match-the-token` | server | recommended |  |  |
| `auth-not-required-refusal-shape` | server | required | `5.2` |  |
| `bearer-token-attaches-and-an-expired-one-does-not` | client | recommended |  |  |
| `bearer-token-exchange-produces-the-identity` | protocol | required |  |  |
| `bearer-token-provider-is-consulted-per-attach` | protocol | required |  |  |
| `callback-credential-runs-the-callers-exchange` | protocol | required |  |  |
| `comments-and-blank-lines-are-skipped` | protocol | required |  |  |
| `comparison-goes-through-constant-time` | protocol | required |  |  |
| `constant-time-agrees-with-equality` | protocol | required |  |  |
| `credential-is-judged-once` | protocol | required |  |  |
| `device-grant-attaches-and-lists-the-users-own-directory` | client | recommended |  |  |
| `different-n-uname-rejected` | server | required | `5.2` |  |
| `each-hash-carries-its-own-salt` | protocol | required |  |  |
| `es256-is-accepted` | server | recommended |  |  |
| `every-shipped-authenticator-requires-an-afid` | protocol | required |  |  |
| `grants-need-issuer-and-client-id` | client | recommended |  |  |
| `identity-comes-from-the-authenticator` | server | required | `5.2` |  |
| `identity-falls-back-to-sub` | server | recommended |  |  |
| `identity-without-a-uid-carries-nonuname` | protocol | required |  |  |
| `impossible-credentials-are-refused` | protocol | required |  |  |
| `iteration-count-is-at-least600000` | protocol | required |  |  |
| `keycloak-auth-rejects-expired` | server | recommended |  |  |
| `malformed-line-names-its-number` | protocol | required |  |  |
| `password-exchange-produces-the-identity` | protocol | required |  |  |
| `password-grant-warns-and-attaches` | client | recommended |  |  |
| `refusal-shape-dot-l` | server | required |  |  |
| `refusal-shape-dot-u` | server | required |  |  |
| `refusal-shape-legacy` | server | required |  |  |
| `rejects-alg-none` | server | recommended |  |  |
| `rejects-bad-signature` | server | recommended |  |  |
| `rejects-bad-tokens` | server | recommended | `Exit 5` |  |
| `rejects-not-yet-valid` | server | recommended |  |  |
| `rejects-unknown-kid` | server | recommended |  |  |
| `rejects-wrong-audience` | server | recommended |  |  |
| `rejects-wrong-issuer` | server | recommended |  |  |
| `rejects-wrong-key` | server | recommended |  |  |
| `session-identity-is-authenticator-output` | server | required |  |  |
| `stored-line-below-the-floor-is-refused` | protocol | required |  |  |
| `stored-line-verifies-its-own-password` | protocol | required |  |  |
| `tls-client-cert-accepts-an-unspecified-uname` | protocol | required |  |  |
| `tls-client-cert-exchange-produces-the-certificate-identity` | protocol | required |  |  |
| `tls-client-cert-honours-a-custom-mapping` | protocol | required |  |  |
| `tls-client-cert-refuses-a-mismatched-uname` | protocol | required |  |  |
| `tls-client-cert-without-a-certificate-refuses-tauth` | protocol | required |  |  |
| `token-exchange-produces-the-identity` | protocol | required |  |  |
| `token-lookup-resolves-the-per-user-secret` | protocol | required |  |  |
| `token-lookup-with-no-secret-refuses-tauth` | protocol | required |  |  |
| `token-of-the-wrong-length-is-rejected` | protocol | required |  |  |
| `token-split-across-writes-still-matches` | protocol | required |  |  |
| `unknown-kid-does-not-stampede-issuer` | server | recommended |  |  |
| `unknown-user-costs-the-same-derivation` | protocol | required |  |  |
| `unknown-user-is-refused` | protocol | required |  |  |
| `unverified-afid-rejected` | server | required |  |  |
| `valid-token-proves-the-identity` | server | recommended |  |  |
| `weaker-iteration-count-is-refused` | protocol | required |  |  |
| `wrong-password-is-refused` | protocol | required |  |  |
| `wrong-token-rejected` | protocol | required |  |  |

## `boundary`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `attach-fid-can-be-clunked-and-remove-frees-it-even-on-error` | server | required |  | `F5` |
| `chunk-boundaries-and-repeated-short-reads` | server | required |  | `F15` |
| `concurrent-creates-have-exactly-one-winner` | server | required |  | `F26` |
| `create-collisions-preserve-the-parent-fid` | server | required |  | `F6`, `F25` |
| `create-requires-an-unopened-directory-fid` | server | required | `5.5` |  |
| `empty-directories-and-zero-counts` | server | required |  | `F2`, `F3` |
| `empty-file-reports-size-zero-and-reads-nothing` | server | required | `4.2` |  |
| `enospc-releases-capacity-and-preserves-fid` | server | required |  | `F12b`, `F25` |
| `exact-utf-8-record-budgets-and-zero-count-resume` | server | required |  | `F21`, `F22` |
| `flush-naming-itself-is-answered` | server | required |  |  |
| `high-offsets-eof-and-empty-writes-use-bounded-buffers` | server | required |  | `F4`, `F18`, `F19` |
| `length-zero-and-extension-change-only-the-requested-bytes` | server | required | `5.8` |  |
| `long-walks-split-by-count-and-encoded-size-and-release-failures` | server | required |  | `F13`, `F14` |
| `maximum-utf-8-names-and-normalization` | server | required |  | `F7b`, `F7d` |
| `memory-handler-rejects-unrepresentable-updates-before-mutation` | server | required |  |  |
| `mid-file-overwrite-preserves-prefix-and-suffix` | server | required | `5.7` |  |
| `paged-long-names-rewind-and-independent-fids` | server | required |  | `F11b`, `F21`, `F22` |
| `read-all-caps-known-and-underreported-sizes` | server | required |  | `F10b`, `F16`, `F17` |
| `removing-an-open-fid-removes-the-file-and-frees-the-fid` | server | required | `5.9` |  |
| `request-mask-of-zero-marks-nothing-valid-but-the-qid` | server | required | `4.6` |  |
| `sixteen-walk-elements-succeed-and-nofid-is-refused` | server | required | `5.3–4` |  |
| `stale-handle-never-reads-or-writes-a-replacement` | server | required |  | `F9` |
| `truncate-needs-write-permission-even-for-a-read-open` | server | required | `5.5` |  |
| `truncating-open-empties-a-plain-file` | server | required | `5.5` |  |
| `unopened-and-wrong-mode-fids-are-refused-without-closing` | server | required |  | `F1` |
| `write-past-the-end-zero-fills-the-hole` | server | required | `5.7` |  |
| `write-to-an-opened-directory-is-refused` | server | required | `5.7` |  |
| `zero-byte-write-leaves-a-non-empty-file-untouched` | server | required | `5.7` |  |

## `cli`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-server-refusal-is-not-replaced-by-the-create-it-provokes` | client | required | `arch:12` |  |
| `all-dialects-all-transports` | server | required | `Exit 3` |  |
| `auth-optional-fallback-is-not-a-crash` | client | required |  |  |
| `auth-optional-fallback-says-it-attached-anonymously` | client | required | `arch:7` |  |
| `auth-optional-falls-back-to-nofid` | client | required |  |  |
| `cat-prints-raw-bytes` | client | required |  |  |
| `connection-refused-reports-one-line` | client | required |  |  |
| `dot-dot-at-root-is-root` | client | required |  |  |
| `errors-use-the-frozen-format` | client | required |  |  |
| `list-prints-sorted-entries-with-directory-suffix` | client | required |  |  |
| `listing-a-file-is-not-a-directory` | client | required |  |  |
| `long-listing-is-ordered-by-name-and-shows-sizes` | client | required |  |  |
| `long-listing-on-anything-but-ls-is-usage-error` | client | required | `arch:7` |  |
| `long-listing-on-ls-still-runs` | client | required |  |  |
| `oidc-flag-without-an-oidc-grant-is-usage-error` | client | required | `arch:7` |  |
| `read-only-server-refuses-write` | client | required |  |  |
| `refused-dialect-is-a-version-error` | client | required |  |  |
| `sample-output-matches-byte-for-byte` | server | required | `AC-a` |  |
| `server-error-is-two` | client | required |  |  |
| `server-listing-is-sorted-bytewise` | client | required |  |  |
| `silent-commands-print-nothing` | client | required |  |  |
| `sorts-bytewise-not-by-culture-or-utf16` | client | required |  |  |
| `stat-prints-the-frozen-fields` | client | required |  |  |
| `success-is-zero` | client | required |  |  |
| `tls-flag-with-a-plaintext-address-is-usage-error` | client | required | `arch:7` |  |
| `tls-flag-with-a-tls-address-is-accepted` | client | required |  |  |
| `tls-handshake-failure-is-one` | client | required |  |  |
| `tls-with-a-trusted-name-succeeds` | client | required |  |  |
| `token-authentication-round-trips` | client | required |  |  |
| `transport-failure-is-one` | client | required |  |  |
| `usage-error-is-three` | client | required |  |  |
| `version-prints-dialect-and-msize` | client | required |  |  |
| `write-prints-wrote-count` | client | required |  |  |
| `write-still-creates-a-file-that-is-genuinely-absent` | client | required | `arch:12` |  |

## `client-api`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `cancelled-or-failed-directory-page-stops-and-session-survives` | client | required |  | `F23` |
| `dot-dot-navigation-returns-to-the-root` | client | required |  |  |
| `early-exit-does-not-fetch-another-directory-page` | client | required |  | `F23` |
| `empty-file-apis-distinguish-write-all-from-truncating-open` | client | required | `Client API` |  |
| `empty-write-all-sends-no-twrite` | client | required | `Client API` |  |
| `failed-batch-drains-other-outstanding-replies` | client | required |  | `F27` |
| `file-growing-after-stat-is-still-capped` | client | required |  | `F17` |
| `invalid-and-configured-names-send-no-operation` | client | required |  | `E6`, `F7c` |
| `invalid-read-all-cap-fails-before-negotiation` | client | required |  | `F16` |
| `iounit-is-clamped-and-zero-uses-msize` | client | required |  | `F20` |
| `large-opaque-cookie-round-trips-and-repeated-cookie-fails` | client | required |  | `F24` |
| `large-transfers-are-chunked-at-the-iounit` | client | required |  |  |
| `linux-extras-are-eopnotsupp-elsewhere` | client | required | `8.15` |  |
| `mid-transfer-cancellation-flushes-pending-request` | client | required |  | `F27` |
| `mid-transfer-error-or-disconnect-never-reports-success` | client | required |  | `F27` |
| `missing-path-leaks-no-fid` | client | required |  |  |
| `missing-size-mask-still-enforces-actual-bytes` | client | required |  | `F17` |
| `out-of-order-short-reads-repair-the-gap` | client | required |  | `F15` |
| `page-ending-on-a-record-boundary-continues` | client | required | `4.2/§4.7` |  |
| `rename-across-directories-is-refused-outside-dot-l` | client | required | `8.15` |  |
| `rename-within-a-directory-still-works` | client | required |  |  |
| `same-scenario-in-every-dialect` | client | required |  |  |
| `typed-api-handles-astral-and-maximum-names` | client | required | `8.2` |  |
| `zero-progress-ordinary-write-is-eio` | client | required | `Client API` |  |

## `clunk`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `clunk-of-an-unknown-fid-is-ebadf` | server | required |  |  |
| `fid-gone-even-when-remove-fails` | server | required | `5.7` |  |
| `handlers-clunk-error-is-the-reply` | server | required | `8.26` |  |
| `handlers-clunk-error-survives-a-remove` | server | required |  |  |
| `orclose-failure-still-frees-the-fid` | server | required |  |  |
| `refused-clunk-during-a-session-reset-does-not-strand-the-rest` | server | required |  |  |
| `remove-takes-the-file-and-the-fid` | server | required |  |  |

## `codec`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-9p2000-only-message-is-illegal-in-dot-l` | protocol | required |  |  |
| `accepts-the-longest-possible-string` | protocol | required |  |  |
| `all-dont-touch-is-fsync-request` | protocol | required | `4.2` |  |
| `all-dont-touch-wstat-decodes-as-an-fsync-request` | protocol | required |  |  |
| `arbitrary-bytes-never-throw-anything-else` | protocol | recommended |  |  |
| `corrupted-golden-frames-never-throw-anything-else` | protocol | recommended |  |  |
| `decoding-as-the-wrong-record-is-a-type-failure` | protocol | required |  |  |
| `deterministic-mutation-loop-finds-nothing-untyped` | protocol | recommended |  |  |
| `dont-touch-is-all-ones-per-field-width` | protocol | required |  |  |
| `dot-and-dot-dot-follow-the-walk-rule` | protocol | required |  |  |
| `dot-l-only-message-is-illegal-in-base` | protocol | required |  |  |
| `empty-ename-is-eio` | protocol | required |  |  |
| `empty-name-is-only-legal-for-xattrwalk` | protocol | required |  |  |
| `encode-after-decode-is-byte-identical` | protocol | recommended |  |  |
| `encode-rejects-a-null-writer` | protocol | required |  |  |
| `encoded-messages-decode-back-to-themselves` | protocol | required |  |  |
| `encoded-size-counts-every-string` | protocol | required |  |  |
| `encoded-size-of-an-empty-record-agrees-with-stat-fix-len` | protocol | required |  |  |
| `every-dot-l-vector-decodes-to-its-fields` | protocol | required |  |  |
| `every-dot-l-vector-re-encodes-byte-identically` | protocol | required |  |  |
| `every-record-declares-the-type-its-name-says` | protocol | required |  |  |
| `every-record-exposes-its-tag` | protocol | required |  |  |
| `every-type-has-a-struct` | protocol | required |  |  |
| `every-vector-is-legal-in-its-own-dialect` | protocol | required |  |  |
| `every-vector-round-trips-byte-exactly` | protocol | required | `9`, `Exit 2` |  |
| `every9-p2000-vector-decodes-to-its-fields` | protocol | required |  |  |
| `every9-p2000-vector-re-encodes-byte-identically` | protocol | required |  |  |
| `fixture-covers-every-dot-l-only-type` | protocol | required |  |  |
| `fixture-covers-the-whole-of9-p2000` | protocol | required |  |  |
| `fixture-holds-eighty-eight-vectors` | protocol | required |  |  |
| `fuzz-corpus-is-seeded-from-the-vectors` | protocol | recommended |  |  |
| `get-name-is-the-protocols-spelling` | protocol | required |  |  |
| `inner-size-equals-outer-minus-two` | protocol | required | `4.2` |  |
| `lock-range-overflow-is-rejected` | protocol | required |  |  |
| `long-form-keeps-its-datasync` | protocol | required |  |  |
| `name-length-cap-is255-bytes` | protocol | required |  |  |
| `negative-errno-is-refused` | protocol | required |  |  |
| `predicted-size-is-the-written-size` | protocol | required |  |  |
| `primitives-round-trip` | protocol | required |  |  |
| `read-bytes-takes-a-view-of-the-frame` | protocol | required |  |  |
| `reads-primitives-little-endian` | protocol | required |  |  |
| `record-shorter-than-its-outer-count-is-rejected` | protocol | required |  |  |
| `record-too-long-for-its-own-length-field-is-refused` | protocol | required |  |  |
| `reply-of-refuses-non-requests` | protocol | required |  |  |
| `rerror-round-trips-in-both-dialects` | protocol | recommended |  |  |
| `rgetattr-is-160-bytes` | protocol | required | `4.6` |  |
| `rgetattr-round-trips-at-a-fixed-length` | protocol | recommended |  |  |
| `short-decodes-long-encodes` | protocol | required |  |  |
| `short-form-legal-only-for-tfsync` | protocol | required | `8.2` |  |
| `size-field-holds-the-written-length` | protocol | required |  |  |
| `size-is-back-patched-after-the-body` | protocol | required |  |  |
| `string-is-length-prefixed-utf-8-without-terminator` | protocol | required |  |  |
| `string-limit-counts-utf-8-bytes` | protocol | required |  |  |
| `terror-and-tlerror-are-not-wire-legal` | protocol | required |  |  |
| `textual-fields-are-never-null` | protocol | required |  |  |
| `tfsync-of-any-other-length-is-rejected` | protocol | required |  |  |
| `tversion-round-trips` | protocol | recommended |  |  |
| `tversion-with-an-empty-version-decodes` | protocol | required |  |  |
| `twalk-round-trips` | protocol | recommended |  |  |
| `twrite-payload-round-trips` | protocol | recommended |  |  |
| `unix-fields-cost-fourteen-bytes-plus-the-extension` | protocol | required |  |  |
| `vectors-cover-sixty-six-distinct-message-names` | protocol | required |  |  |
| `walk-beyond-max-welem-is-refused` | protocol | required |  |  |
| `walk-without-its-element-list-is-refused` | protocol | required |  |  |

## `codec-robustness`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bad-stat-inner-size-is-rejected` | client | required |  |  |
| `claimed-length-never-allocates` | protocol | required |  |  |
| `disposing-a-lease-advances-the-pipe` | protocol | required |  |  |
| `end-of-stream-is-clean-between-frames-and-a-violation-inside-one` | protocol | required |  |  |
| `errno-above-int32-max-is-rejected` | protocol | required |  |  |
| `errno-at-int32-max-is-decoded` | protocol | required |  |  |
| `every-mutation-yields-a-typed-error` | protocol | required | `8.2`, `AC-d` |  |
| `first-failure-is-sticky` | protocol | required |  |  |
| `frame-shorter-than-a-header-is-a-size-failure` | protocol | required |  |  |
| `maximum-payload-count-is-bounds-without-allocating-the-claim` | protocol | required |  |  |
| `more-qids-than-names-is-rejected` | client | required |  |  |
| `multi-segment-frame-is-copied-into-the-pool` | protocol | required |  |  |
| `negotiated-msize-becomes-the-bound` | protocol | required |  |  |
| `nul-in-a-dirent-name-is-rejected` | protocol | required |  |  |
| `only-nine-p-protocol-exception-escapes` | protocol | required |  |  |
| `outer-stat-count-must-match-the-record` | protocol | required |  |  |
| `over-count-replies-are-rejected` | client | required | `8.13` |  |
| `overflow-trailing-bytes-are-rejected` | protocol | required |  |  |
| `pre-negotiation-cap-is8192` | protocol | required | `8.1` |  |
| `read-offset-at-the-end-of-the-address-space-is-decoded` | protocol | required |  |  |
| `reassembles-split-frame` | protocol | required |  |  |
| `rejects-integer-past-the-end` | protocol | required |  |  |
| `rejects-invalid-utf-8` | protocol | required | `8.3` |  |
| `rejects-nul-in-string` | protocol | required | `8.3` |  |
| `rejects-size-below7` | protocol | required |  |  |
| `rejects-slash-in-name` | protocol | required | `8.3` |  |
| `rejects-string-that-runs-past-the-frame` | protocol | required |  |  |
| `size-lie-closes-connection` | protocol | required | `8.1` |  |
| `size-must-equal-the-frames-length` | protocol | required |  |  |
| `split-dirent-is-rejected` | client | required |  |  |
| `twrite-count-disagrees-with-size` | protocol | required | `8.4` |  |
| `walk-beyond-max-welem-is-rejected` | protocol | required |  |  |
| `wire-reader-rejection-trailing-bytes-are-rejected` | protocol | required |  |  |
| `write-count-must-agree-with-size` | protocol | required |  |  |
| `write-offset-at-the-edge-is-accepted` | protocol | required |  |  |
| `write-offset-overflow-rejected` | protocol | required | `8.5` |  |

## `constants`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `no-fid-and-non-uname-are-both-all-ones` | protocol | required |  |  |
| `numeric-constants-match-the-reference` | protocol | required |  |  |
| `service-allowance-is-not-the-header-size` | protocol | required |  |  |
| `version-strings-match-the-reference` | protocol | required |  |  |

## `create`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `create-carries-the-file-flags-to-the-handler` | server | required | `8.19` |  |
| `create-whose-flags-the-handler-dropped-is-removed-and-refused` | server | required | `8.19` |  |
| `create-with-a-server-owned-bit-is-refused` | server | required | `8.19` |  |
| `create-without-those-bits-still-works` | server | required |  |  |
| `created-exclusive-file-is-held-by-its-creator` | server | required | `5.5` |  |
| `device-create-parses-the-extension` | server | required | `8.24` |  |
| `device-create-with-a-bad-extension-is-einval` | server | required |  |  |
| `directory-create-refuses-truncate-and-remove-on-close` | server | required | `8.25` |  |
| `directory-create-without-those-flags-still-works` | server | required |  |  |
| `symlink-create-leaves-a-fid-that-is-not-open` | server | required |  |  |

## `dialect`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `base-frame-in-unix-session-is-rejected` | protocol | required |  |  |
| `dialects-do-not-share-their-own-messages` | protocol | required |  |  |
| `downgrade-above-the-floor-is-accepted` | client | required |  |  |
| `every-step-is-gated-on-the-configured-set` | protocol | required | `5.1` |  |
| `every-unix-vector-round-trips-byte-exactly` | protocol | required |  |  |
| `every-wire-legal-type-is-checked-in-every-dialect` | protocol | required |  |  |
| `legacy-client-asks-for-the-legacy-msize` | client | required |  |  |
| `msize-below-floor-is-unknown` | protocol | required | `5.1` |  |
| `msize-is-clamped-to-the-client-and-to-the-maximum` | protocol | required |  |  |
| `n-uname-is-on-the-wire-in-dot-l-too` | protocol | required |  |  |
| `negotiation-reports-what-was-agreed` | client | required |  |  |
| `oracle-matches-committed-file` | protocol | required | `5.1` |  |
| `pre-negotiation-error-is9-p2000-shaped` | server | required | `8.9` |  |
| `refused-dialect-is-retried-with-the-next-on-the-list` | client | required |  |  |
| `second-tversion-resets-session` | server | required | `8.9` |  |
| `undefined-numbers-are-legal-nowhere` | protocol | required |  |  |
| `unix-carries-the-same-types-as-base` | protocol | required |  |  |
| `unix-fields-decode-into-their-records` | protocol | required |  |  |
| `unix-frame-in-base-session-is-rejected` | protocol | required |  |  |
| `unknown-answer-throws` | client | required |  |  |
| `unknown-echoes-client-msize` | protocol | required | `5.1` |  |
| `unknown-version-after-a-good-one-returns-to-pre-negotiation` | server | required |  |  |
| `unknown-version-keeps-connection-for-tversion-only` | server | required |  |  |
| `version-downgrade-below-min-throws` | client | required |  |  |
| `version-strings-round-trip` | protocol | required |  |  |
| `version-suffix-is-stripped` | protocol | required | `5.1` |  |

## `directory`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bad-offset-rejected` | server | required |  |  |
| `budget-below-the-first-record-packs-nothing` | protocol | required |  |  |
| `budget-under-one-record-is-erange` | server | required |  |  |
| `count-clamped-not-rejected` | server | required |  |  |
| `dir-entry-codec-entries-are-never-split` | protocol | required | `4.3` |  |
| `directory-packer-entries-are-never-split` | server | required | `4.3` |  |
| `dirent-type-mirrors-file-kind` | protocol | required |  |  |
| `dot-and-dot-dot-are-readable` | protocol | required |  |  |
| `golden-entries-round-trip` | protocol | required |  |  |
| `no-dot-entries` | server | required |  |  |
| `no-dot-or-dot-dot-entries` | server | required | `4.3` |  |
| `readdir-cookie-resumes-after-entry` | server | required |  |  |
| `record-never-split` | server | required |  |  |
| `split-trailing-record-is-rejected` | protocol | required |  |  |
| `stat-records-carry-their-size-once` | server | required |  |  |
| `tread-on-dir-is-error-in-dot-l` | server | required | `5.9` |  |

## `dispatch`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `every-legal-t-message-is-routed` | server | required |  |  |
| `every-routed-type-is-wire-legal` | server | required |  |  |
| `legacy-messages-answer` | server | required |  |  |
| `linux-messages-answer` | server | required |  |  |
| `shared-messages-answer-in-every-dialect` | server | required |  |  |
| `xattr-messages-answer` | server | required |  |  |
| `xattrcreate-with-zero-size-removes-the-attribute` | server | required | `8.21` |  |

## `docs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `every-row-names-a-handler-or-the-core` | server | recommended |  |  |
| `handler-table-lists-every-t-message` | server | recommended |  |  |

## `error`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `all-carries-every-row` | protocol | required |  |  |
| `dot-l-always-uses-rlerror` | protocol | required | `5.9` |  |
| `ename-for-matches-the-table` | protocol | required |  |  |
| `ename-of-exactly-the-cap-survives` | protocol | required |  |  |
| `ename-truncated-at-rune-boundary` | protocol | required | `8.10` |  |
| `error-table-unknown-ename-is-eio` | protocol | required |  |  |
| `error-type-follows-the-dialect` | protocol | required |  |  |
| `every-linux-ename-maps-back-to-its-errno` | protocol | required |  |  |
| `every-linux-errno-is-a-constant-and-a-row` | protocol | required |  |  |
| `every-sent-ename-is-one-linux-maps-to-that-errno` | protocol | required | `8.39` |  |
| `every-table-row-round-trips-through-every-dialect` | protocol | required |  |  |
| `former-ename-is-still-understood-but-not-sent` | protocol | required |  |  |
| `from-errno-and-from-ename-agree` | protocol | required |  |  |
| `legacy-dialects-carry-the-ename` | protocol | required |  |  |
| `legacy-reply-errno-comes-from-the-table` | protocol | required |  |  |
| `linux-table-matches-the-fixture` | protocol | required |  |  |
| `negative-errno-is-projected-as-eio` | protocol | required |  |  |
| `nine-p-failures-keep-their-value` | protocol | required |  |  |
| `non-nine-p-failures-project-to-eio` | protocol | required |  |  |
| `permission-denied-maps-back-to-eacces` | protocol | required |  |  |
| `projected-ename-is-truncated` | protocol | required |  |  |
| `server-enames-map-back` | protocol | required |  |  |
| `short-enames-are-not-touched` | protocol | required |  |  |
| `unix-reply-errno-wins-over-the-table` | protocol | required |  |  |
| `unknown-ename-is-eio` | protocol | required |  |  |
| `unknown-errno-projects-to-io-error` | protocol | required |  |  |
| `written-ename-never-degrades-to-eio` | protocol | required | `8.23` |  |

## `examples`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bind-failure-is-reported-rather-than-spun-on` | server | recommended |  |  |
| `unused-transport-flags-are-warned-about` | server | recommended | `arch:7` |  |
| `write-back-implies-writable-and-says-so` | server | recommended | `arch:7` |  |

## `fid`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `append-transfer-retries-only-the-unacknowledged-suffix` | client | required | `8.30` |  |
| `append-zero-progress-fails-rather-than-looping` | client | required |  |  |
| `attach-runs-afid-exchange` | client | required |  |  |
| `attach-with-a-uname-publishes-the-root` | client | required |  |  |
| `attach-without-credential-uses-nofid` | client | required |  |  |
| `clear-releases-every-handler` | server | required |  |  |
| `client-fid-dispose-clunks` | client | required |  |  |
| `close-waits-for-in-flight-read-or-write` | client | required |  |  |
| `directory-fetch-drains-before-close-and-resumed-enumeration-cannot-use-recycled-fid` | client | required |  |  |
| `disposal-during-clone-or-walk-waits-without-nested-lease-failure` | client | required |  |  |
| `disposal-waits-for-the-entire-chunked-transfer` | client | required |  |  |
| `disposing-a-session-whose-server-is-gone-is-quiet` | client | required | `8.41` |  |
| `double-dispose-is-safe` | client | required |  |  |
| `duplicate-fid-rejected` | server | required |  |  |
| `earlier-xattr-write-error-survives-cleanup-error` | client | required |  |  |
| `early-short-reply-cannot-hide-a-later-overcount` | client | required |  |  |
| `fid-table-cap-overflow` | server | required | `8.7` |  |
| `generated-lifecycles-match-the-independent-model` | server | recommended |  |  |
| `invalid-window-fails-before-dial-or-negotiation` | client | required | `8.34` |  |
| `minimum-window-works-and-cancelled-transfers-fail-before-sending` | client | required |  |  |
| `nofid-cannot-be-bound` | server | required |  |  |
| `ordinary-writes-stay-pipelined-and-repair-short-write-gaps-with-reordered-replies` | client | required |  |  |
| `per-fid-operations-serialise` | server | required |  |  |
| `real-server-xattr-handler-commit-refusal-reaches-caller` | client | required | `8.29` |  |
| `refused-auth-is-not-downgraded-to-nofid` | client | required |  |  |
| `released-handles-cannot-use-recycled-fids` | client | required | `8.28` |  |
| `second-attach-does-not-displace-the-root` | client | required |  |  |
| `unknown-fid-rejected` | server | required |  |  |
| `xattr-commit-errors-are-propagated-and-sink-is-released` | client | required |  |  |
| `xattr-commit-must-be-acknowledged` | client | required |  |  |

## `flush`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `already-cancelled-token-sends-nothing` | client | required |  |  |
| `delivers-racey-reply` | client | required | `8.14` |  |
| `disposal-against-a-silent-server-is-bounded-whatever-the-fid-count` | client | required |  |  |
| `disposal-completes-against-a-silent-server` | client | required |  |  |
| `disposing-a-fid-against-a-silent-server-throws-nothing` | client | required |  |  |
| `failed-walk-reports-its-own-error-when-the-cleanup-clunk-times-out` | client | required |  |  |
| `failed-walks-cleanup-clunk-is-bounded` | client | required |  |  |
| `flush-behind-one-queued-ordinary-request-must-reach-its-reserved-slot` | server | required |  |  |
| `flush-of-unknown-tag-is-answered` | client | required |  |  |
| `flushed-request-answered-once` | server | required | `5.3` |  |
| `late-error-reply-to-a-tflush-reclaims-both-tags` | client | required |  |  |
| `late-rflush-does-not-terminate-the-session` | client | required |  |  |
| `multiple-flushes-answered-in-order` | server | required |  |  |
| `no-reply-after-rflush` | server | required |  |  |
| `racey-error-reply-is-delivered-too` | client | required |  |  |
| `request-timeout-is-flushed-too` | client | required |  |  |
| `rflush-always-sent` | server | required |  |  |
| `tag-reused-the-instant-its-rflush-arrives-is-served` | server | required |  |  |
| `tflush-answered-with-an-error-frees-both-tags` | client | required |  |  |
| `unanswered-rflush-times-out-rather-than-hanging` | client | required |  |  |
| `waits-for-rflush-before-tag-reuse` | client | required | `8.14` |  |

## `handler`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `missing-capability-is-eopnotsupp` | server | required |  |  |
| `readlink-on-a-plain-file-is-refused` | server | required |  |  |
| `throwing-handler-is-io-error-and-the-session-lives` | server | required |  |  |

## `interop`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `error-answering-the-version-request-is-a-version-error` | client | required | `8.38` |  |
| `linux-v9fs-against-our-jsonfs` | client | recommended |  |  |
| `our-client-against-diod` | client | recommended |  |  |
| `our-client-against-p9ufs` | client | recommended |  |  |
| `plan9port-client-against-our-jsonfs` | client | recommended |  |  |
| `rename-falls-back-to-trename-when-the-server-lacks-trenameat` | client | required | `8.37` |  |

## `jsonfs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `array-append-only-at-next-index` | server | required |  |  |
| `cap-rejects-create-write-and-rename-without-changing-open-objects` | server | required |  | `F12c` |
| `create-and-move-cannot-exceed-reloadable-depth` | server | required |  | `F29` |
| `create-asking-for-a-flag-is-refused` | server | required | `8.27` |  |
| `create-beyond-max-entries-is-enospc` | server | required | `8.40` |  |
| `directory-sync-fsyncs-a-real-directory-and-fails-loudly-on-a-missing-one` | server | required |  |  |
| `dot-and-dot-dot-keys-are-encoded` | server | required |  |  |
| `empty-key-is-percent` | server | required |  |  |
| `empty-writes-preserve-content-and-sparse-writes-survive-reload` | server | required |  |  |
| `encoded-keys-remain-distinct-after-write-back-and-reload` | server | required |  | `E17` |
| `exact-serialized-threshold-and-concurrent-growth` | server | required |  | `F12c` |
| `incomplete-utf-8-at-clunk-is-an-error-and-fid-is-released` | server | required |  | `F28` |
| `key-encoding-is-injective` | server | required |  |  |
| `move-into-own-subtree-is-refused` | server | required |  |  |
| `mutation-between-pages-does-not-skip-untouched-entries` | server | required |  | `F8` |
| `number-formatting-is-invariant` | server | required |  |  |
| `refuses-deep-document` | server | required |  |  |
| `refuses-oversize-document` | server | required |  |  |
| `scalar-type-demotion-is-documented` | server | required |  |  |
| `set-attr-applies-a-truncation-and-a-rename-together` | server | required | `8.27` |  |
| `set-attr-refuses-a-length-it-cannot-produce` | server | required |  |  |
| `set-attr-with-an-unsupported-field-changes-nothing` | server | required | `8.27` |  |
| `utf-8-characters-may-cross-write-boundaries-and-invalid-bytes-are-atomic` | server | required |  | `F28` |
| `whole-file-pipeline-splits-utf-8-across-wire-chunks` | server | required |  | `F28` |
| `write-back-failure-reports-error-and-leaves-a-complete-document` | server | required |  | `F30` |
| `write-back-indents-numbers` | server | required |  |  |
| `write-back-is-atomic` | server | required |  |  |
| `write-back-is-coalesced-and-flushed-on-shutdown` | server | required | `8.40` |  |
| `write-back-keeps-non-ascii-as-itself` | server | required |  |  |
| `write-beyond-the-scalar-bound-is-refused-before-it-allocates` | server | required |  |  |

## `limits`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `allocation-stays-within-msize` | server | required | `AC-b` |  |
| `auth-flood-stops-paying-the-derivation` | server | required | `8.40` |  |
| `budget-of-zero-throttles-nothing` | server | required | `8.40` |  |
| `defaults-are-the-architectures-numbers` | protocol | required |  |  |
| `defaults-validate` | protocol | required |  |  |
| `fid-flood-hits-cap-not-memory` | server | required |  |  |
| `flush-answered-while-window-full` | server | required | `8.8` |  |
| `half-header-times-out` | server | required |  |  |
| `listener-wide-bound-holds` | server | required | `8.8` |  |
| `one-address-cannot-hold-every-connection` | server | required | `8.40` |  |
| `one-connection-cannot-stall-another` | server | required |  |  |
| `oversize-read-count-is-clamped` | server | required |  |  |
| `oversize-read-count-on-a-file-larger-than-msize-is-clamped-to-msize` | server | required |  |  |
| `pre-negotiation-cap-is-not-the-configured-maximum` | protocol | required |  |  |
| `pre-version-oversize-frame-closes-the-connection` | server | required |  |  |
| `request-flood-is-metered-and-flush-still-answered` | server | required | `8.40` |  |
| `size-lie-closes-only-that-connection` | server | required |  |  |
| `success-clears-the-address-budget` | server | required | `8.40` |  |
| `tag-flood-backpressures` | server | required |  |  |
| `validate-rejects-inconsistent-limits` | protocol | required |  |  |
| `walk-of-seventeen-elements-is-refused` | server | required |  |  |
| `window-reused-the-instant-its-reply-arrives-is-never-refused` | server | required | `8.8` |  |

## `lock`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `constants-match-reference` | protocol | required | `4.8` |  |
| `synthetic-filesystem-magic-is-v9fs` | protocol | required |  |  |
| `zero-length-and-unlock-are-the-references-conventions` | protocol | required |  |  |

## `mode`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `access-modes-are-the-low-two-bits` | protocol | required |  |  |
| `append-is-a-flag-not-an-access-mode` | protocol | required | `4.5` |  |
| `exec-is-sent-as-read-only-in-dot-l` | protocol | required |  |  |
| `flags-9p2000-has-still-project` | protocol | required |  |  |
| `linux-only-flags-have-no9-p2000-spelling` | protocol | required |  |  |
| `open-flags-are-distinct-single-bits` | protocol | required |  |  |
| `qid-mirror-skips-only-the-mount-bit` | protocol | required |  |  |
| `remove-on-close-flag-has-no-linux-spelling` | protocol | required |  |  |
| `server-decoding-still-reads-access-mode-three` | protocol | required |  |  |
| `values-match-reference` | protocol | required | `4.4`, `4.6` |  |

## `names`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `configured-limit-covers-every-name-field-and-keeps-session-usable` | server | required |  | `F7c` |
| `malformed-names-are-answered-then-only-that-connection-closes` | server | required |  | `F7a` |
| `malformed-names-respect-each-fields-legal-exceptions` | server | required | `8.2–3` |  |

## `observability`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `backslashes-are-escaped` | protocol | required |  |  |
| `cap-falls-on-a-rune-boundary` | protocol | required |  |  |
| `message-the-dialect-does-not-carry-is-answered-and-the-session-stays-up` | server | required |  |  |
| `messages-are-counted-by-type` | server | required |  |  |
| `null-arguments-throw` | protocol | required |  |  |
| `read-at-the-end-of-the-address-space-is-an-empty-read` | server | required |  |  |
| `reply-too-long-to-encode-is-an-error-reply-rather-than-silence` | server | required |  |  |
| `request-log-sees-escaped-summaries` | server | required |  |  |
| `throwing-sink-does-not-break-the-session` | server | required |  |  |
| `untrusted-strings-escaped-and-capped` | protocol | required | `8.11` |  |

## `open`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bad-open-mode-rejected` | server | required | `4.5` |  |
| `directory-open-is-read-or-exec-only` | server | required |  |  |
| `dmexcl-second-open-fails` | server | required |  |  |
| `dot-l-flags-honoured` | server | required | `4.5` |  |
| `iounit-is-msize-minus-iohdrsz` | server | required | `5.5` |  |
| `nofollow-on-a-symlink-is-eloop` | server | required |  |  |
| `o-directory-on-a-plain-file-is-enotdir` | server | required | `8.23` |  |
| `open-of-a-fifo-is-enxio` | server | required | `8.23` |  |
| `open-of-a-symlink-is-eloop` | server | required | `8.23` |  |
| `open-of-an-open-fid-is-refused` | server | required |  |  |
| `orclose-removes-on-clunk` | server | required |  |  |
| `oread-plus-oappend-stays-read` | server | required |  |  |

## `permission`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-non-owner-with-write-permission-may-truncate-and-stamp-the-time` | server | required | `8.43` |  |
| `a-server-stamped-time-still-needs-the-write-bit` | server | required | `8.43` |  |
| `an-explicit-time-stays-the-owners-alone-however-open-the-mode-is` | server | required | `8.43` |  |
| `attach-identity-reaches-the-filesystem` | server | required |  |  |
| `core-denies-before-handler-is-called` | server | required |  |  |
| `create-needs-write-on-the-directory` | server | required |  |  |
| `only-the-owner-may-change-mode` | server | required |  |  |
| `remove-needs-write-in-the-parent` | server | required |  |  |
| `the-owner-may-still-set-an-explicit-time` | server | required | `8.43` |  |
| `walk-needs-search-permission` | server | required |  |  |

## `projection`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-9p2000-symlink-qid-is-a-symlink-in-a-listing-too` | client | required |  |  |
| `a-9p2000-symlink-qid-is-reported-as-a-symlink` | client | required | `8.17` |  |
| `answer-that-is-not-the-offer-is-a-version-error` | client | required | `8.18` |  |
| `atime-update-never-reaches-a-wstat-server` | client | required |  |  |
| `create-asking-for-a-server-owned-flag-is-refused` | client | required | `8.19` |  |
| `create-of-a-kind-with-no-extension-field-is-refused` | client | required | `8.15` |  |
| `exec-opens-read-only-on-a-dot-l-server` | client | required | `8.16` |  |
| `file-flags-never-reach-a-dot-l-create` | client | required | `8.15` |  |
| `file-flags-never-reach-a-dot-l-setattr` | client | required | `8.15` |  |
| `half-stated-mode-word-is-completed-from-the-record` | client | required | `8.19` |  |
| `linux-only-open-flags-never-reach-a-9p2000-server` | client | required | `8.15` |  |
| `numeric-group-is-refused-on-plain9-p2000` | client | required | `8.15` |  |
| `remove-on-close-flag-never-reaches-a-dot-l-create` | client | required |  |  |
| `remove-on-close-flag-never-reaches-a-dot-l-open` | client | required | `8.15` |  |
| `server-clock-update-is-not-sent-as-an-fsync` | client | required | `8.15` |  |
| `suffix-stripping-downgrade-is-the-only-one` | client | required | `8.18` |  |
| `textual-group-and-a-name-are-refused-on-dot-l` | client | required |  |  |
| `unmarked-getattr-field-keeps-its-default` | client | required | `8.17` |  |
| `unsolicited-rversion-terminates-the-session` | client | required | `8.18` |  |

## `qid`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `equality-is-by-all-three-fields` | protocol | required |  |  |
| `type-bits-follow-linux` | protocol | required | `4.1` |  |
| `wire-size-is-thirteen` | protocol | required |  |  |

## `remove`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `directory-with-removedir-is-removed` | server | required |  |  |
| `directory-without-removedir-is-eisdir` | server | required | `8.20` |  |
| `file-with-removedir-is-enotdir` | server | required |  |  |
| `file-without-flags-is-removed` | server | required |  |  |
| `unknown-flag-is-einval` | server | required | `8.20` |  |

## `scale`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bounded-ci-workloads` | server | recommended |  | `F10`, `F11`, `F12` |

## `semantics`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `clunk-does-not-dispose-an-in-flight-read` | server | required |  |  |
| `dmappend-ignores-the-write-offset` | server | required |  |  |
| `dmappend-ignores-truncation` | server | required |  |  |
| `dot-dot-requires-search-permission` | server | required |  |  |
| `non-directory-after-first-element-returns-partial-walk` | server | required |  |  |
| `plain-plan9-owner-can-use-other-read-permission` | server | required |  |  |
| `separate-dot-dot-walks-reach-the-real-root` | server | required |  |  |
| `version-reset-releases-exclusive-open` | server | required |  |  |
| `version-reset-removes-orclose-files` | server | required |  |  |

## `server-lifecycle`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `append-writes-across-connections-do-not-overwrite-each-other` | server | required | `8.30` |  |
| `cleanup-continues-when-reporting-a-handler-failure-also-throws` | server | required |  |  |
| `closing-a-file-waits-for-its-active-write` | server | required | `8.28` |  |
| `concurrent-replies-are-serialised` | server | required |  |  |
| `connections-are-counted` | server | required |  |  |
| `deep-cloned-walk-preserves-all-ancestors` | server | required | `8.31` |  |
| `disconnect-finalizes-open-state` | server | required |  |  |
| `disposal-completes-while-the-peer-has-stopped-reading` | server | required |  |  |
| `dot-dot-from-a-file-is-rejected` | server | required |  |  |
| `endpoints-are-published-once-bound` | server | required |  |  |
| `graceful-shutdown-drains` | server | required |  |  |
| `incomplete-xattr-reset-logs-failure-and-continues-cleanup` | server | required |  |  |
| `listener-retains-only-active-sessions` | server | required | `8.35` |  |
| `no-listen-address-is-refused` | server | required |  |  |
| `partial-permission-failure-does-not-change-fids` | server | required | `8.31` |  |
| `permission-classes-follow-the-dialect` | server | required | `8.32` |  |
| `queued-operation-rejects-retired-fid-after-number-reuse` | server | required |  |  |
| `rename-outside-restricted-attach-clamps-parent-walk-and-keeps-removal-location` | server | required | `8.31` |  |
| `renamed-ancestor-rebases-existing-aliases-across-connections` | server | required | `8.31` |  |
| `renamed-directory-ascends-through-its-new-parents` | server | required |  |  |
| `shutdown-defers-disposal-until-an-uncooperative-read-returns` | server | required |  |  |
| `shutdown-keeps-flush-gates-and-handlers-alive-until-inline-reader-unwinds` | server | required | `8.28` |  |
| `slow-lookup-does-not-block-other-clients-and-revalidates-after-rename` | server | required | `8.31` |  |
| `thrown-lookup-errors-preserve-walk-bindings` | server | required |  |  |
| `unbound-scheme-is-refused` | server | required |  |  |
| `version-reset-commits-a-complete-xattr-sink` | server | required |  |  |
| `xattr-reads-check-permissions-before-calling-the-handler` | server | required | `8.33` |  |

## `session`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `oversize-msize-answer-throws` | client | required |  |  |
| `reply-larger-than-msize-terminates-session` | client | required |  |  |
| `unexpected-reply-type-terminates-session` | client | required |  |  |
| `unknown-tag-terminates-session` | client | required | `8.12` |  |

## `statfs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `filesystem-answers-when-the-handler-does-not` | server | required |  |  |
| `statfs-is-linux-only` | server | required |  |  |
| `synthetic-servers-report-v9fs-magic` | server | required | `4.9` |  |

## `tag`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `clear-cancels-everything-in-flight` | server | required |  |  |
| `completion-is-exactly-once` | server | required |  |  |
| `duplicate-tag-rejected` | server | required | `8.6` |  |
| `error-reply-releases-its-tag-exactly-once` | client | required |  |  |
| `flush-that-suppresses-a-reply-frees-the-tag-at-once` | server | required |  |  |
| `flushing-an-unknown-tag-suppresses-nothing` | server | required |  |  |
| `generated-reply-and-flush-schedules-preserve-tag-ownership` | client | recommended |  |  |
| `illegal-message-for-the-dialect-is-refused-before-the-wire` | client | required |  |  |
| `late-release-does-not-evict-the-next-request-on-the-same-tag` | server | required |  |  |
| `memory-address-needs-its-transport` | client | required |  |  |
| `notag-is-ordinary-outside-version` | server | required | `8.6` |  |
| `release-frees-the-tag-for-reuse` | server | required |  |  |
| `requests-are-pipelined-and-replies-may-arrive-out-of-order` | client | required |  |  |
| `rerror-carries-both-halves-in-dot-u` | client | required |  |  |
| `rlerror-becomes-a-typed-exception` | client | required |  |  |
| `tag-reused-the-instant-its-reply-arrives-is-never-refused` | server | required |  |  |
| `tags-are-reused-after-their-reply` | client | required |  |  |
| `transport-overload-dials-and-negotiates` | client | required |  |  |
| `whole-tag-pool-is-rentable-after-an-error-reply` | client | required |  |  |

## `todofs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `add-and-remove-round-trip` | server | required |  |  |
| `cascade-delete-removes-lists-and-items` | server | required |  |  |
| `concurrent-writers-serialise` | server | required |  |  |
| `create-asking-for-a-flag-is-refused` | server | required | `8.27` |  |
| `ensure-user-is-idempotent` | server | required |  |  |
| `field-write-capped-at64-kib` | server | required |  |  |
| `fields-round-trip` | server | required |  |  |
| `list-and-item-quotas-are-enospc` | server | required | `8.40` |  |
| `listing-is-sorted-by-name` | server | required |  |  |
| `malformed-command-is-einval` | server | required |  |  |
| `mkdir-takes-the-next-number-only` | server | required |  |  |
| `new-list-and-item-start-empty` | server | required |  |  |
| `non-admin-gets-eacces` | server | required |  |  |
| `qid-path-carries-the-table-tag` | server | required |  |  |
| `queries-are-scoped-by-user` | server | required |  |  |
| `read-after-a-write-answers-the-stored-value` | server | required |  |  |
| `read-after-a-write-answers-the-user-list` | server | required | `arch:7` |  |
| `remove-takes-the-users-lists-and-items` | server | required |  |  |
| `rmdir-removes-items-and-empty-lists` | server | required |  |  |
| `schema-version-refuses-newer` | server | required |  |  |
| `set-attr-length-zero-empties-a-free-text-field` | server | required | `8.27` |  |
| `set-attr-length-zero-on-status-is-refused` | server | required | `8.27` |  |
| `set-attr-length-zero-on-the-control-file-is-refused` | server | required |  |  |
| `set-attr-on-a-directory-is-refused` | server | required |  |  |
| `set-attr-refuses-a-length-it-cannot-produce` | server | required |  |  |
| `set-attr-with-an-unsupported-field-changes-nothing` | server | required | `8.27` |  |
| `shipped-wiring-refuses-an-unauthenticated-attach` | server | required |  |  |
| `status-rejects-other-values` | server | required |  |  |
| `store-queries-are-scoped-even-when-the-row-id-is-known` | server | required |  |  |
| `trailing-newline-is-optional` | server | required |  |  |
| `truncating-open-clunked-without-a-write-empties-the-field` | server | required | `8.27` |  |
| `truncating-open-of-status-resets-it-to-open` | server | required |  |  |
| `truncating-open-of-the-control-file-keeps-the-users` | server | required | `8.27` |  |
| `two-concurrent-creates-at-the-quota-have-exactly-one-winner` | server | required | `8.40` |  |
| `two-opens-of-one-field-see-one-file` | server | required |  |  |
| `user-a-cannot-reach-user-b` | server | required | `AC-c` |  |
| `user-a-cannot-walk-read-stat-or-list-user-b` | server | required |  |  |

## `transport`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `accepted-connection-knows-the-peer` | protocol | required |  |  |
| `actual-tls-and-wss-handshakes-enforce-server-purpose` | protocol | required |  |  |
| `bound-address-carries-the-real-port` | protocol | required |  |  |
| `cancellation-ends-the-loop` | protocol | required |  |  |
| `cancelling-one-accept-preserves-handshake-and-disposal-drains-pending-and-queued-connections` | protocol | required |  |  |
| `canonical-form-round-trips` | protocol | required |  |  |
| `close-reason-reaches-the-peer` | protocol | required |  |  |
| `closing-is-an-end-of-stream-at-the-peer` | protocol | required |  |  |
| `concurrent-tcp-listener-and-accepted-connection-disposal-does-not-race-slot-release` | protocol | required |  |  |
| `connection-cap-refuses` | protocol | required |  |  |
| `custom-root-preserves-peer-role-on-ca-issued-leaf` | protocol | required | `8.36` |  |
| `delayed-reply-respects-the-flush-boundary` | client | required |  |  |
| `delayed-writer-never-exceeds-the-reply-channel-bound` | server | required |  |  |
| `dialling-an-unbound-name-fails` | protocol | required |  |  |
| `disposal-ends-the-loop` | protocol | required |  |  |
| `dispose-is-a-normal-close-and-is-idempotent` | protocol | required |  |  |
| `disposing-a-listener-unbinds-the-name` | protocol | required |  |  |
| `disposing-tcp-listener-wakes-accept-blocked-at-connection-cap` | protocol | required |  |  |
| `dropped-reply-times-out-flushes-and-leaves-the-session-usable` | client | required |  |  |
| `duplicate-after-retirement-fails-every-pending-caller` | client | required |  |  |
| `duplicate-request-is-rejected-while-the-original-remains-pending` | server | required |  |  |
| `expired-handshake-returns-capacity-for-waiting-healthy-peer` | protocol | required |  |  |
| `fatal-failure-ends-the-loop` | protocol | required |  |  |
| `first-close-reason-wins` | protocol | required |  |  |
| `host-named-host-is-still-a-host` | protocol | required |  |  |
| `illegal-addresses-are-refused` | protocol | required |  |  |
| `impossible-bounds-are-refused` | protocol | required |  |  |
| `intermediate-purpose-restriction-cannot-be-repaired-by-custom-trust` | protocol | required | `8.36` |  |
| `is-secure-follows-the-scheme` | protocol | required |  |  |
| `legal-addresses-parse` | protocol | required |  |  |
| `listen-accept-and-dial-meet` | protocol | required |  |  |
| `memory-transport-disposed-listener-accepts-null` | protocol | required |  |  |
| `memory-transport-round-trips-frames` | protocol | required |  |  |
| `mid-frame-close-fails-the-pending-calls` | client | required |  |  |
| `name-binds-once` | protocol | required |  |  |
| `no-delay-is-set` | protocol | required |  |  |
| `null-is-not-an-address` | protocol | required |  |  |
| `only-memory-addresses-are-accepted` | protocol | required |  |  |
| `only-tcp-addresses-are-accepted` | protocol | required |  |  |
| `parse-names-the-offending-text` | protocol | required |  |  |
| `partial-header-close-or-stall-affects-only-its-connection` | server | required |  |  |
| `pathless-web-socket-url-gains-the-root` | protocol | required |  |  |
| `port-zero-is-the-wildcard-listen-port` | protocol | required |  |  |
| `silent-peer-does-not-block-healthy-handshake-and-pending-plus-active-connections-stay-capped` | protocol | required | `8.35` |  |
| `single-byte-reads-reassemble-the-reply` | client | required |  |  |
| `tcp-transport-disposed-listener-accepts-null` | protocol | required |  |  |
| `tcp-transport-round-trips-frames` | protocol | required |  |  |
| `throttled-quarter-megabyte-transfer-completes-with-the-window` | client | required |  |  |
| `transient-failure-is-retried-and-the-listener-lives-on` | protocol | required |  |  |
| `transports-are-isolated-from-each-other` | protocol | required |  |  |
| `writing-after-close-throws` | protocol | required |  |  |

## `transport-tls`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `additional-peer-check-can-refuse-a-trusted-certificate` | protocol | required |  |  |
| `additional-peer-check-cannot-rescue-a-bad-certificate` | protocol | required |  |  |
| `clean-shutdown-sends-close-notify` | protocol | required |  |  |
| `hostname-mismatch-rejected-by-default` | protocol | required |  |  |
| `insecure-opt-out-does-not-reach-the-listener` | protocol | required |  |  |
| `insecure-opt-out-logs` | protocol | required |  |  |
| `listening-without-a-certificate-is-refused` | protocol | required |  |  |
| `mutual-tls-identity-exposed` | protocol | required |  |  |
| `negotiated-protocol-is-at-least-tls-12` | protocol | required |  |  |
| `only-tls-addresses-are-accepted` | protocol | required |  |  |
| `renegotiation-is-off-on-both-sides` | protocol | required |  |  |
| `tls-11-refused` | protocol | required |  |  |
| `untrusted-self-signed-rejected-by-default-and-accepted-by-the-trust-hook` | protocol | required |  |  |

## `transport-websocket`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `accept-token-follows-the-rfc` | protocol | required |  |  |
| `allowed-origin-is-accepted-and-exposed` | protocol | required |  |  |
| `client-web-socket-handshake-succeeds-over-test-certificate` | protocol | required |  |  |
| `close-reasons-map-to-status-codes` | protocol | required |  |  |
| `disallowed-origin-refused` | protocol | required |  |  |
| `empty-allow-list-logs-a-warning-at-listen-time` | protocol | required |  |  |
| `fragmented-oversize-closes1009` | protocol | required |  |  |
| `fragmented-under-cap-accepted` | protocol | required |  |  |
| `missing-origin-is-refused-when-an-allow-list-exists` | protocol | required |  |  |
| `only-a-sixteen-byte-key-is-legal` | protocol | required |  |  |
| `only-web-socket-addresses-are-accepted` | protocol | required |  |  |
| `plain-http-request-is-refused` | protocol | required |  |  |
| `plain-web-socket-round-trips-frames` | protocol | required |  |  |
| `text-frame-closes` | protocol | required |  |  |

## `walk`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bad-name-rejected` | server | required |  |  |
| `cannot-clone-open-fid` | server | required | `5.4` |  |
| `dot-dot-at-root-is-root` | server | required |  |  |
| `dot-dot-returns-to-the-parent` | server | required |  |  |
| `duplicate-newfid-rejected` | server | required |  |  |
| `first-element-failure-is-an-error` | server | required |  |  |
| `partial-walk-does-not-bind-newfid` | server | required |  |  |
| `partial-walk-returns-prefix` | server | required | `5.4` |  |

## `write`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `append-open-ignores-the-offset` | server | required |  |  |
| `maximal-legal-write-is-accepted` | server | required | `8.4` |  |
| `server-bound-shortens-write` | server | required | `5.6` |  |
| `write-needs-a-write-open` | server | required |  |  |
