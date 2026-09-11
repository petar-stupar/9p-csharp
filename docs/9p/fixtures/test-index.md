# Shared test index

**Generated from [test-index.json](test-index.json) by `gen-test-index-md.mjs`. Edit the
JSON, not this file.**

Language-neutral test obligations shared by every 9P port. An id names a behaviour, not a test method: each port maps the id onto its own test in its docs/test-map.json. Seeded from the C# reference port (workspace ARCHITECTURE.md §12); thereafter authored by hand, and a test added in any port for a transferable behaviour is added here and owed by every port.

785 obligations across 43 areas: **744 required**,
**41 recommended**.

- **required** — Every port must have a test for this behaviour before it is done.
- **recommended** — Every port should; a port that skips one records the reason in its docs/test-map.json "skipped" map. Typically needs a facility not every ecosystem has: property testing, an external peer, an OIDC issuer, a scale budget.

| Area | Obligations | Required | Recommended |
| --- | ---: | ---: | ---: |
| `attr` | 65 | 65 | 0 |
| `auth` | 61 | 42 | 19 |
| `boundary` | 28 | 28 | 0 |
| `cli` | 32 | 32 | 0 |
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
| `permission` | 6 | 6 | 0 |
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
| `dot-u-symlink-still-carries-its-target` | protocol | required |  |  |
| `half-stated-mode-word-is-refused` | protocol | required | `8.19` |  |
| `numeric-group-needs-dot-u` | protocol | required |  |  |
| `server-owned-flag-is-refused-by-the-projector` | protocol | required |  |  |
| `unmarked-mode-takes-the-kind-from-the-qid` | protocol | required |  |  |
| `complete-mode-fills-the-unstated-half-from-the-record` | protocol | required |  |  |
| `create-mode-masked-to07777` | protocol | required | `4.7` |  |
| `directory-length-is-zero` | protocol | required | `4.2` |  |
| `dot-l-qid-type-from-posix-file-type` | protocol | required | `4.1` |  |
| `dot-u-device-extension-is-the-reference-text` | protocol | required |  |  |
| `dot-u-qid-bits-are-linux` | protocol | required |  |  |
| `getattr-reads-every-marked-field` | protocol | required |  |  |
| `getattr-reads-only-the-marked-fields` | protocol | required |  |  |
| `plain-9p2000-refuses-the-unix-permission-bits` | protocol | required | `8.15` |  |
| `plain-9p2000-wstat-has-only-the-rwx-bits` | protocol | required |  |  |
| `posix-mode-carries-the-file-type` | protocol | required |  |  |
| `qid-type-mirrors-mode-high-bits` | protocol | required | `4.1` |  |
| `rgetattr-is-160-bytes` | protocol | required | `4.6` |  |
| `rgetattr-qid-is-always-valid` | protocol | required |  |  |
| `rgetattr-valid-is-requested-and-supplied` | protocol | required |  |  |
| `setattr-has-no-name-or-group-name` | protocol | required | `8.15` |  |
| `stat-record-is-not-a-dot-l-shape` | protocol | required |  |  |
| `symlink-stat-fails-on9-p2000` | protocol | required |  |  |
| `to-wstat-sends-the-file-flags-in-the-mode-word` | protocol | required | `8.19` |  |
| `to-wstat-sends-the-unix-permission-bits-in-dot-u` | protocol | required |  |  |
| `unix-permission-bits-only-exist-in-dot-u` | protocol | required |  |  |
| `wstat-cannot-ask-for-the-server-clock` | protocol | required |  |  |
| `wstat-cannot-set-atime` | protocol | required |  |  |
| `wstat-carries-the-unix-permission-bits-in-dot-u` | protocol | required |  |  |
| `basic-and-all-are-the-unions-the-reference-says` | protocol | required |  |  |
| `set-attr-values-match-reference` | protocol | required |  |  |
| `values-match-reference` | protocol | required | `4.4`, `4.6` |  |
| `is-160-bytes` | protocol | required |  |  |
| `short-frame-is-rejected` | protocol | required |  |  |
| `valid-mask-is-the-reference-basic-set` | protocol | required |  |  |
| `flags-only-update-is-not-an-fsync` | protocol | required |  |  |
| `all-dont-touch-wstat-is-an-fsync-request` | protocol | required |  |  |
| `c-time-is-always-the-server-clock` | protocol | required |  |  |
| `only-marked-fields-are-projected` | protocol | required |  |  |
| `time-without-set-uses-server-clock` | protocol | required | `4.6` |  |
| `wstat-never-changes-the-owner` | protocol | required |  |  |
| `wstat-projects-only-the-settable-fields` | protocol | required |  |  |
| `wstat-projects-the-unix-group` | protocol | required |  |  |
| `fields-the-handler-left-zero-are-not-marked-valid` | server | required | `8.22` |  |
| `fields-the-handler-supplied-are-marked-valid` | server | required |  |  |
| `valid-is-the-intersection-with-the-request-mask` | server | required |  |  |
| `valid-mask-of-zero-changes-nothing-and-does-not-fsync` | server | required | `4.6` |  |
| `directory-size-is-refused-before-any-part-of-the-update` | server | required | `5.8 / §4.6` |  |
| `legacy-directory-length-zero-reaches-the-handler` | server | required | `5.8` |  |
| `orphan-time-modifiers-are-einval-without-mutation` | server | required | `4.6` |  |
| `field-echoed-back-unchanged-is-a-no-op` | server | required |  |  |
| `flag-echoed-back-unchanged-is-a-no-op` | server | required | `8.19` |  |
| `flag-update-the-handler-dropped-is-refused` | server | required | `8.19` |  |
| `server-owned-bit-cannot-be-set-by-a-wstat` | server | required | `8.19` |  |
| `setuid-chmod-is-refused-on-plain9-p2000` | server | required |  |  |
| `all-dont-touch-is-an-fsync` | server | required |  |  |
| `all-or-nothing` | server | required | `5.8` |  |
| `unsettable-field-is-refused` | server | required |  |  |
| `changing-a-file-flag-reaches-the-handler` | server | required | `8.19` |  |
| `dmdir-cannot-change` | server | required |  |  |
| `only-the-owner-may-set-a-flag` | server | required | `5.8` |  |
| `owner-cannot-change` | server | required |  |  |
| `dot-u-permission-bits-round-trip-through-wstat` | server | required | `8.19` |  |
| `owner-may-change-mode-and-length` | server | required |  |  |

## `auth`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `attach-claiming-another-user-is-refused` | client | recommended |  |  |
| `bearer-token-attaches-and-an-expired-one-does-not` | client | recommended |  |  |
| `device-grant-attaches-and-lists-the-users-own-directory` | client | recommended |  |  |
| `grants-need-issuer-and-client-id` | client | recommended |  |  |
| `password-grant-warns-and-attaches` | client | recommended |  |  |
| `identity-without-a-uid-carries-nonuname` | protocol | required |  |  |
| `anonymous-identity-carries-no-groups` | protocol | required |  |  |
| `bearer-token-exchange-produces-the-identity` | protocol | required |  |  |
| `bearer-token-provider-is-consulted-per-attach` | protocol | required |  |  |
| `callback-credential-runs-the-callers-exchange` | protocol | required |  |  |
| `every-shipped-authenticator-requires-an-afid` | protocol | required |  |  |
| `password-exchange-produces-the-identity` | protocol | required |  |  |
| `tls-client-cert-accepts-an-unspecified-uname` | protocol | required |  |  |
| `tls-client-cert-exchange-produces-the-certificate-identity` | protocol | required |  |  |
| `tls-client-cert-honours-a-custom-mapping` | protocol | required |  |  |
| `token-exchange-produces-the-identity` | protocol | required |  |  |
| `token-lookup-resolves-the-per-user-secret` | protocol | required |  |  |
| `wrong-password-is-refused` | protocol | required |  |  |
| `unknown-user-costs-the-same-derivation` | protocol | required |  |  |
| `unknown-user-is-refused` | protocol | required |  |  |
| `constant-time-agrees-with-equality` | protocol | required |  |  |
| `impossible-credentials-are-refused` | protocol | required |  |  |
| `credential-is-judged-once` | protocol | required |  |  |
| `tls-client-cert-refuses-a-mismatched-uname` | protocol | required |  |  |
| `tls-client-cert-without-a-certificate-refuses-tauth` | protocol | required |  |  |
| `token-lookup-with-no-secret-refuses-tauth` | protocol | required |  |  |
| `malformed-line-names-its-number` | protocol | required |  |  |
| `stored-line-below-the-floor-is-refused` | protocol | required |  |  |
| `stored-line-verifies-its-own-password` | protocol | required |  |  |
| `weaker-iteration-count-is-refused` | protocol | required |  |  |
| `comments-and-blank-lines-are-skipped` | protocol | required |  |  |
| `each-hash-carries-its-own-salt` | protocol | required |  |  |
| `iteration-count-is-at-least600000` | protocol | required |  |  |
| `token-of-the-wrong-length-is-rejected` | protocol | required |  |  |
| `token-split-across-writes-still-matches` | protocol | required |  |  |
| `comparison-goes-through-constant-time` | protocol | required |  |  |
| `wrong-token-rejected` | protocol | required |  |  |
| `auth-not-required-refusal-shape` | server | required | `5.2` |  |
| `refusal-shape-dot-l` | server | required |  |  |
| `refusal-shape-dot-u` | server | required |  |  |
| `refusal-shape-legacy` | server | required |  |  |
| `afid-exchange-times-out` | server | required |  |  |
| `afid-write-bounded-at64-kib` | server | required |  |  |
| `different-n-uname-rejected` | server | required | `5.2` |  |
| `identity-comes-from-the-authenticator` | server | required | `5.2` |  |
| `session-identity-is-authenticator-output` | server | required |  |  |
| `unverified-afid-rejected` | server | required |  |  |
| `unknown-kid-does-not-stampede-issuer` | server | recommended |  |  |
| `attach-uname-must-match-the-token` | server | recommended |  |  |
| `es256-is-accepted` | server | recommended |  |  |
| `identity-falls-back-to-sub` | server | recommended |  |  |
| `rejects-alg-none` | server | recommended |  |  |
| `rejects-bad-signature` | server | recommended |  |  |
| `keycloak-auth-rejects-expired` | server | recommended |  |  |
| `rejects-not-yet-valid` | server | recommended |  |  |
| `rejects-unknown-kid` | server | recommended |  |  |
| `rejects-wrong-audience` | server | recommended |  |  |
| `rejects-wrong-issuer` | server | recommended |  |  |
| `rejects-wrong-key` | server | recommended |  |  |
| `valid-token-proves-the-identity` | server | recommended |  |  |
| `rejects-bad-tokens` | server | recommended | `Exit 5` |  |

## `boundary`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `read-all-caps-known-and-underreported-sizes` | server | required |  | `F10b`, `F16`, `F17` |
| `paged-long-names-rewind-and-independent-fids` | server | required |  | `F11b`, `F21`, `F22` |
| `enospc-releases-capacity-and-preserves-fid` | server | required |  | `F12b`, `F25` |
| `long-walks-split-by-count-and-encoded-size-and-release-failures` | server | required |  | `F13`, `F14` |
| `chunk-boundaries-and-repeated-short-reads` | server | required |  | `F15` |
| `unopened-and-wrong-mode-fids-are-refused-without-closing` | server | required |  | `F1` |
| `exact-utf-8-record-budgets-and-zero-count-resume` | server | required |  | `F21`, `F22` |
| `concurrent-creates-have-exactly-one-winner` | server | required |  | `F26` |
| `empty-directories-and-zero-counts` | server | required |  | `F2`, `F3` |
| `high-offsets-eof-and-empty-writes-use-bounded-buffers` | server | required |  | `F4`, `F18`, `F19` |
| `attach-fid-can-be-clunked-and-remove-frees-it-even-on-error` | server | required |  | `F5` |
| `create-collisions-preserve-the-parent-fid` | server | required |  | `F6`, `F25` |
| `maximum-utf-8-names-and-normalization` | server | required |  | `F7b`, `F7d` |
| `stale-handle-never-reads-or-writes-a-replacement` | server | required |  | `F9` |
| `flush-naming-itself-is-answered` | server | required |  |  |
| `mid-file-overwrite-preserves-prefix-and-suffix` | server | required | `5.7` |  |
| `request-mask-of-zero-marks-nothing-valid-but-the-qid` | server | required | `4.6` |  |
| `truncating-open-empties-a-plain-file` | server | required | `5.5` |  |
| `write-past-the-end-zero-fills-the-hole` | server | required | `5.7` |  |
| `write-to-an-opened-directory-is-refused` | server | required | `5.7` |  |
| `zero-byte-write-leaves-a-non-empty-file-untouched` | server | required | `5.7` |  |
| `empty-file-reports-size-zero-and-reads-nothing` | server | required | `4.2` |  |
| `create-requires-an-unopened-directory-fid` | server | required | `5.5` |  |
| `length-zero-and-extension-change-only-the-requested-bytes` | server | required | `5.8` |  |
| `removing-an-open-fid-removes-the-file-and-frees-the-fid` | server | required | `5.9` |  |
| `sixteen-walk-elements-succeed-and-nofid-is-refused` | server | required | `5.3–4` |  |
| `memory-handler-rejects-unrepresentable-updates-before-mutation` | server | required |  |  |
| `truncate-needs-write-permission-even-for-a-read-open` | server | required | `5.5` |  |

## `cli`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `auth-optional-fallback-is-not-a-crash` | client | required |  |  |
| `auth-optional-fallback-says-it-attached-anonymously` | client | required | `arch:7` |  |
| `connection-refused-reports-one-line` | client | required |  |  |
| `long-listing-on-anything-but-ls-is-usage-error` | client | required | `arch:7` |  |
| `long-listing-on-ls-still-runs` | client | required |  |  |
| `oidc-flag-without-an-oidc-grant-is-usage-error` | client | required | `arch:7` |  |
| `server-error-is-two` | client | required |  |  |
| `success-is-zero` | client | required |  |  |
| `tls-flag-with-a-plaintext-address-is-usage-error` | client | required | `arch:7` |  |
| `tls-flag-with-a-tls-address-is-accepted` | client | required |  |  |
| `tls-handshake-failure-is-one` | client | required |  |  |
| `tls-with-a-trusted-name-succeeds` | client | required |  |  |
| `transport-failure-is-one` | client | required |  |  |
| `usage-error-is-three` | client | required |  |  |
| `long-listing-is-ordered-by-name-and-shows-sizes` | client | required |  |  |
| `server-listing-is-sorted-bytewise` | client | required |  |  |
| `sorts-bytewise-not-by-culture-or-utf16` | client | required |  |  |
| `auth-optional-falls-back-to-nofid` | client | required |  |  |
| `cat-prints-raw-bytes` | client | required |  |  |
| `dot-dot-at-root-is-root` | client | required |  |  |
| `errors-use-the-frozen-format` | client | required |  |  |
| `list-prints-sorted-entries-with-directory-suffix` | client | required |  |  |
| `listing-a-file-is-not-a-directory` | client | required |  |  |
| `read-only-server-refuses-write` | client | required |  |  |
| `refused-dialect-is-a-version-error` | client | required |  |  |
| `silent-commands-print-nothing` | client | required |  |  |
| `stat-prints-the-frozen-fields` | client | required |  |  |
| `token-authentication-round-trips` | client | required |  |  |
| `version-prints-dialect-and-msize` | client | required |  |  |
| `write-prints-wrote-count` | client | required |  |  |
| `all-dialects-all-transports` | server | required | `Exit 3` |  |
| `sample-output-matches-byte-for-byte` | server | required | `AC-a` |  |

## `client-api`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `page-ending-on-a-record-boundary-continues` | client | required | `4.2/§4.7` |  |
| `zero-progress-ordinary-write-is-eio` | client | required | `Client API` |  |
| `empty-write-all-sends-no-twrite` | client | required | `Client API` |  |
| `invalid-and-configured-names-send-no-operation` | client | required |  | `E6`, `F7c` |
| `out-of-order-short-reads-repair-the-gap` | client | required |  | `F15` |
| `invalid-read-all-cap-fails-before-negotiation` | client | required |  | `F16` |
| `file-growing-after-stat-is-still-capped` | client | required |  | `F17` |
| `missing-size-mask-still-enforces-actual-bytes` | client | required |  | `F17` |
| `iounit-is-clamped-and-zero-uses-msize` | client | required |  | `F20` |
| `cancelled-or-failed-directory-page-stops-and-session-survives` | client | required |  | `F23` |
| `early-exit-does-not-fetch-another-directory-page` | client | required |  | `F23` |
| `large-opaque-cookie-round-trips-and-repeated-cookie-fails` | client | required |  | `F24` |
| `failed-batch-drains-other-outstanding-replies` | client | required |  | `F27` |
| `mid-transfer-cancellation-flushes-pending-request` | client | required |  | `F27` |
| `mid-transfer-error-or-disconnect-never-reports-success` | client | required |  | `F27` |
| `missing-path-leaks-no-fid` | client | required |  |  |
| `rename-across-directories-is-refused-outside-dot-l` | client | required | `8.15` |  |
| `rename-within-a-directory-still-works` | client | required |  |  |
| `dot-dot-navigation-returns-to-the-root` | client | required |  |  |
| `empty-file-apis-distinguish-write-all-from-truncating-open` | client | required | `Client API` |  |
| `large-transfers-are-chunked-at-the-iounit` | client | required |  |  |
| `linux-extras-are-eopnotsupp-elsewhere` | client | required | `8.15` |  |
| `same-scenario-in-every-dialect` | client | required |  |  |
| `typed-api-handles-astral-and-maximum-names` | client | required | `8.2` |  |

## `clunk`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `handlers-clunk-error-is-the-reply` | server | required | `8.26` |  |
| `handlers-clunk-error-survives-a-remove` | server | required |  |  |
| `refused-clunk-during-a-session-reset-does-not-strand-the-rest` | server | required |  |  |
| `clunk-of-an-unknown-fid-is-ebadf` | server | required |  |  |
| `fid-gone-even-when-remove-fails` | server | required | `5.7` |  |
| `orclose-failure-still-frees-the-fid` | server | required |  |  |
| `remove-takes-the-file-and-the-fid` | server | required |  |  |

## `codec`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `tfsync-of-any-other-length-is-rejected` | protocol | required |  |  |
| `long-form-keeps-its-datasync` | protocol | required |  |  |
| `short-decodes-long-encodes` | protocol | required |  |  |
| `short-form-legal-only-for-tfsync` | protocol | required | `8.2` |  |
| `dot-l-only-message-is-illegal-in-base` | protocol | required |  |  |
| `every-dot-l-vector-decodes-to-its-fields` | protocol | required |  |  |
| `every-dot-l-vector-re-encodes-byte-identically` | protocol | required |  |  |
| `fixture-covers-every-dot-l-only-type` | protocol | required |  |  |
| `lock-range-overflow-is-rejected` | protocol | required |  |  |
| `empty-ename-is-eio` | protocol | required |  |  |
| `string-limit-counts-utf-8-bytes` | protocol | required |  |  |
| `tversion-with-an-empty-version-decodes` | protocol | required |  |  |
| `a-9p2000-only-message-is-illegal-in-dot-l` | protocol | required |  |  |
| `decoding-as-the-wrong-record-is-a-type-failure` | protocol | required |  |  |
| `every9-p2000-vector-decodes-to-its-fields` | protocol | required |  |  |
| `every9-p2000-vector-re-encodes-byte-identically` | protocol | required |  |  |
| `fixture-covers-the-whole-of9-p2000` | protocol | required |  |  |
| `every-vector-is-legal-in-its-own-dialect` | protocol | required |  |  |
| `every-vector-round-trips-byte-exactly` | protocol | required | `9`, `Exit 2` |  |
| `fixture-holds-eighty-eight-vectors` | protocol | required |  |  |
| `rgetattr-is-160-bytes` | protocol | required | `4.6` |  |
| `vectors-cover-sixty-six-distinct-message-names` | protocol | required |  |  |
| `every-record-declares-the-type-its-name-says` | protocol | required |  |  |
| `every-record-exposes-its-tag` | protocol | required |  |  |
| `every-type-has-a-struct` | protocol | required |  |  |
| `get-name-is-the-protocols-spelling` | protocol | required |  |  |
| `reply-of-refuses-non-requests` | protocol | required |  |  |
| `terror-and-tlerror-are-not-wire-legal` | protocol | required |  |  |
| `encode-rejects-a-null-writer` | protocol | required |  |  |
| `encoded-messages-decode-back-to-themselves` | protocol | required |  |  |
| `negative-errno-is-refused` | protocol | required |  |  |
| `predicted-size-is-the-written-size` | protocol | required |  |  |
| `size-field-holds-the-written-length` | protocol | required |  |  |
| `walk-beyond-max-welem-is-refused` | protocol | required |  |  |
| `walk-without-its-element-list-is-refused` | protocol | required |  |  |
| `record-too-long-for-its-own-length-field-is-refused` | protocol | required |  |  |
| `all-dont-touch-wstat-decodes-as-an-fsync-request` | protocol | required |  |  |
| `inner-size-equals-outer-minus-two` | protocol | required | `4.2` |  |
| `record-shorter-than-its-outer-count-is-rejected` | protocol | required |  |  |
| `all-dont-touch-is-fsync-request` | protocol | required | `4.2` |  |
| `dont-touch-is-all-ones-per-field-width` | protocol | required |  |  |
| `encoded-size-counts-every-string` | protocol | required |  |  |
| `encoded-size-of-an-empty-record-agrees-with-stat-fix-len` | protocol | required |  |  |
| `textual-fields-are-never-null` | protocol | required |  |  |
| `unix-fields-cost-fourteen-bytes-plus-the-extension` | protocol | required |  |  |
| `accepts-the-longest-possible-string` | protocol | required |  |  |
| `empty-name-is-only-legal-for-xattrwalk` | protocol | required |  |  |
| `dot-and-dot-dot-follow-the-walk-rule` | protocol | required |  |  |
| `name-length-cap-is255-bytes` | protocol | required |  |  |
| `read-bytes-takes-a-view-of-the-frame` | protocol | required |  |  |
| `reads-primitives-little-endian` | protocol | required |  |  |
| `primitives-round-trip` | protocol | required |  |  |
| `size-is-back-patched-after-the-body` | protocol | required |  |  |
| `string-is-length-prefixed-utf-8-without-terminator` | protocol | required |  |  |
| `arbitrary-bytes-never-throw-anything-else` | protocol | recommended |  |  |
| `corrupted-golden-frames-never-throw-anything-else` | protocol | recommended |  |  |
| `deterministic-mutation-loop-finds-nothing-untyped` | protocol | recommended |  |  |
| `encode-after-decode-is-byte-identical` | protocol | recommended |  |  |
| `fuzz-corpus-is-seeded-from-the-vectors` | protocol | recommended |  |  |
| `rerror-round-trips-in-both-dialects` | protocol | recommended |  |  |
| `rgetattr-round-trips-at-a-fixed-length` | protocol | recommended |  |  |
| `tversion-round-trips` | protocol | recommended |  |  |
| `twalk-round-trips` | protocol | recommended |  |  |
| `twrite-payload-round-trips` | protocol | recommended |  |  |

## `codec-robustness`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bad-stat-inner-size-is-rejected` | client | required |  |  |
| `more-qids-than-names-is-rejected` | client | required |  |  |
| `over-count-replies-are-rejected` | client | required | `8.13` |  |
| `split-dirent-is-rejected` | client | required |  |  |
| `multi-segment-frame-is-copied-into-the-pool` | protocol | required |  |  |
| `disposing-a-lease-advances-the-pipe` | protocol | required |  |  |
| `end-of-stream-is-clean-between-frames-and-a-violation-inside-one` | protocol | required |  |  |
| `pre-negotiation-cap-is8192` | protocol | required | `8.1` |  |
| `reassembles-split-frame` | protocol | required |  |  |
| `rejects-size-below7` | protocol | required |  |  |
| `size-lie-closes-connection` | protocol | required | `8.1` |  |
| `negotiated-msize-becomes-the-bound` | protocol | required |  |  |
| `claimed-length-never-allocates` | protocol | required |  |  |
| `nul-in-a-dirent-name-is-rejected` | protocol | required |  |  |
| `outer-stat-count-must-match-the-record` | protocol | required |  |  |
| `every-mutation-yields-a-typed-error` | protocol | required | `8.2`, `AC-d` |  |
| `maximum-payload-count-is-bounds-without-allocating-the-claim` | protocol | required |  |  |
| `only-nine-p-protocol-exception-escapes` | protocol | required |  |  |
| `twrite-count-disagrees-with-size` | protocol | required | `8.4` |  |
| `errno-above-int32-max-is-rejected` | protocol | required |  |  |
| `errno-at-int32-max-is-decoded` | protocol | required |  |  |
| `frame-shorter-than-a-header-is-a-size-failure` | protocol | required |  |  |
| `read-offset-at-the-end-of-the-address-space-is-decoded` | protocol | required |  |  |
| `size-must-equal-the-frames-length` | protocol | required |  |  |
| `overflow-trailing-bytes-are-rejected` | protocol | required |  |  |
| `walk-beyond-max-welem-is-rejected` | protocol | required |  |  |
| `write-count-must-agree-with-size` | protocol | required |  |  |
| `write-offset-at-the-edge-is-accepted` | protocol | required |  |  |
| `write-offset-overflow-rejected` | protocol | required | `8.5` |  |
| `first-failure-is-sticky` | protocol | required |  |  |
| `rejects-integer-past-the-end` | protocol | required |  |  |
| `rejects-invalid-utf-8` | protocol | required | `8.3` |  |
| `rejects-nul-in-string` | protocol | required | `8.3` |  |
| `rejects-slash-in-name` | protocol | required | `8.3` |  |
| `rejects-string-that-runs-past-the-frame` | protocol | required |  |  |
| `wire-reader-rejection-trailing-bytes-are-rejected` | protocol | required |  |  |

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
| `create-whose-flags-the-handler-dropped-is-removed-and-refused` | server | required | `8.19` |  |
| `created-exclusive-file-is-held-by-its-creator` | server | required | `5.5` |  |
| `create-carries-the-file-flags-to-the-handler` | server | required | `8.19` |  |
| `create-with-a-server-owned-bit-is-refused` | server | required | `8.19` |  |
| `create-without-those-bits-still-works` | server | required |  |  |
| `device-create-parses-the-extension` | server | required | `8.24` |  |
| `device-create-with-a-bad-extension-is-einval` | server | required |  |  |
| `directory-create-refuses-truncate-and-remove-on-close` | server | required | `8.25` |  |
| `directory-create-without-those-flags-still-works` | server | required |  |  |
| `symlink-create-leaves-a-fid-that-is-not-open` | server | required |  |  |

## `dialect`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `downgrade-above-the-floor-is-accepted` | client | required |  |  |
| `legacy-client-asks-for-the-legacy-msize` | client | required |  |  |
| `refused-dialect-is-retried-with-the-next-on-the-list` | client | required |  |  |
| `unknown-answer-throws` | client | required |  |  |
| `negotiation-reports-what-was-agreed` | client | required |  |  |
| `version-downgrade-below-min-throws` | client | required |  |  |
| `every-step-is-gated-on-the-configured-set` | protocol | required | `5.1` |  |
| `msize-below-floor-is-unknown` | protocol | required | `5.1` |  |
| `msize-is-clamped-to-the-client-and-to-the-maximum` | protocol | required |  |  |
| `oracle-matches-committed-file` | protocol | required | `5.1` |  |
| `unknown-echoes-client-msize` | protocol | required | `5.1` |  |
| `version-strings-round-trip` | protocol | required |  |  |
| `version-suffix-is-stripped` | protocol | required | `5.1` |  |
| `base-frame-in-unix-session-is-rejected` | protocol | required |  |  |
| `every-unix-vector-round-trips-byte-exactly` | protocol | required |  |  |
| `n-uname-is-on-the-wire-in-dot-l-too` | protocol | required |  |  |
| `unix-fields-decode-into-their-records` | protocol | required |  |  |
| `unix-frame-in-base-session-is-rejected` | protocol | required |  |  |
| `dialects-do-not-share-their-own-messages` | protocol | required |  |  |
| `every-wire-legal-type-is-checked-in-every-dialect` | protocol | required |  |  |
| `undefined-numbers-are-legal-nowhere` | protocol | required |  |  |
| `unix-carries-the-same-types-as-base` | protocol | required |  |  |
| `unknown-version-after-a-good-one-returns-to-pre-negotiation` | server | required |  |  |
| `pre-negotiation-error-is9-p2000-shaped` | server | required | `8.9` |  |
| `second-tversion-resets-session` | server | required | `8.9` |  |
| `unknown-version-keeps-connection-for-tversion-only` | server | required |  |  |

## `directory`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `budget-below-the-first-record-packs-nothing` | protocol | required |  |  |
| `split-trailing-record-is-rejected` | protocol | required |  |  |
| `dirent-type-mirrors-file-kind` | protocol | required |  |  |
| `dot-and-dot-dot-are-readable` | protocol | required |  |  |
| `dir-entry-codec-entries-are-never-split` | protocol | required | `4.3` |  |
| `golden-entries-round-trip` | protocol | required |  |  |
| `budget-under-one-record-is-erange` | server | required |  |  |
| `directory-packer-entries-are-never-split` | server | required | `4.3` |  |
| `no-dot-or-dot-dot-entries` | server | required | `4.3` |  |
| `stat-records-carry-their-size-once` | server | required |  |  |
| `bad-offset-rejected` | server | required |  |  |
| `count-clamped-not-rejected` | server | required |  |  |
| `no-dot-entries` | server | required |  |  |
| `readdir-cookie-resumes-after-entry` | server | required |  |  |
| `record-never-split` | server | required |  |  |
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
| `former-ename-is-still-understood-but-not-sent` | protocol | required |  |  |
| `written-ename-never-degrades-to-eio` | protocol | required | `8.23` |  |
| `all-carries-every-row` | protocol | required |  |  |
| `ename-for-matches-the-table` | protocol | required |  |  |
| `every-linux-ename-maps-back-to-its-errno` | protocol | required |  |  |
| `every-linux-errno-is-a-constant-and-a-row` | protocol | required |  |  |
| `every-sent-ename-is-one-linux-maps-to-that-errno` | protocol | required | `8.39` |  |
| `linux-table-matches-the-fixture` | protocol | required |  |  |
| `permission-denied-maps-back-to-eacces` | protocol | required |  |  |
| `server-enames-map-back` | protocol | required |  |  |
| `error-table-unknown-ename-is-eio` | protocol | required |  |  |
| `unknown-errno-projects-to-io-error` | protocol | required |  |  |
| `negative-errno-is-projected-as-eio` | protocol | required |  |  |
| `dot-l-always-uses-rlerror` | protocol | required | `5.9` |  |
| `error-type-follows-the-dialect` | protocol | required |  |  |
| `every-table-row-round-trips-through-every-dialect` | protocol | required |  |  |
| `legacy-dialects-carry-the-ename` | protocol | required |  |  |
| `legacy-reply-errno-comes-from-the-table` | protocol | required |  |  |
| `nine-p-failures-keep-their-value` | protocol | required |  |  |
| `non-nine-p-failures-project-to-eio` | protocol | required |  |  |
| `projected-ename-is-truncated` | protocol | required |  |  |
| `unix-reply-errno-wins-over-the-table` | protocol | required |  |  |
| `ename-of-exactly-the-cap-survives` | protocol | required |  |  |
| `unknown-ename-is-eio` | protocol | required |  |  |
| `ename-truncated-at-rune-boundary` | protocol | required | `8.10` |  |
| `from-errno-and-from-ename-agree` | protocol | required |  |  |
| `short-enames-are-not-touched` | protocol | required |  |  |

## `examples`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `bind-failure-is-reported-rather-than-spun-on` | server | recommended |  |  |
| `unused-transport-flags-are-warned-about` | server | recommended | `arch:7` |  |
| `write-back-implies-writable-and-says-so` | server | recommended | `arch:7` |  |

## `fid`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `second-attach-does-not-displace-the-root` | client | required |  |  |
| `attach-runs-afid-exchange` | client | required |  |  |
| `attach-with-a-uname-publishes-the-root` | client | required |  |  |
| `attach-without-credential-uses-nofid` | client | required |  |  |
| `client-fid-dispose-clunks` | client | required |  |  |
| `double-dispose-is-safe` | client | required |  |  |
| `refused-auth-is-not-downgraded-to-nofid` | client | required |  |  |
| `early-short-reply-cannot-hide-a-later-overcount` | client | required |  |  |
| `append-transfer-retries-only-the-unacknowledged-suffix` | client | required | `8.30` |  |
| `append-zero-progress-fails-rather-than-looping` | client | required |  |  |
| `close-waits-for-in-flight-read-or-write` | client | required |  |  |
| `directory-fetch-drains-before-close-and-resumed-enumeration-cannot-use-recycled-fid` | client | required |  |  |
| `disposal-during-clone-or-walk-waits-without-nested-lease-failure` | client | required |  |  |
| `disposal-waits-for-the-entire-chunked-transfer` | client | required |  |  |
| `disposing-a-session-whose-server-is-gone-is-quiet` | client | required | `8.41` |  |
| `earlier-xattr-write-error-survives-cleanup-error` | client | required |  |  |
| `invalid-window-fails-before-dial-or-negotiation` | client | required | `8.34` |  |
| `minimum-window-works-and-cancelled-transfers-fail-before-sending` | client | required |  |  |
| `ordinary-writes-stay-pipelined-and-repair-short-write-gaps-with-reordered-replies` | client | required |  |  |
| `real-server-xattr-handler-commit-refusal-reaches-caller` | client | required | `8.29` |  |
| `released-handles-cannot-use-recycled-fids` | client | required | `8.28` |  |
| `xattr-commit-errors-are-propagated-and-sink-is-released` | client | required |  |  |
| `xattr-commit-must-be-acknowledged` | client | required |  |  |
| `generated-lifecycles-match-the-independent-model` | server | recommended |  |  |
| `fid-table-cap-overflow` | server | required | `8.7` |  |
| `clear-releases-every-handler` | server | required |  |  |
| `duplicate-fid-rejected` | server | required |  |  |
| `nofid-cannot-be-bound` | server | required |  |  |
| `per-fid-operations-serialise` | server | required |  |  |
| `unknown-fid-rejected` | server | required |  |  |

## `flush`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `failed-walk-reports-its-own-error-when-the-cleanup-clunk-times-out` | client | required |  |  |
| `failed-walks-cleanup-clunk-is-bounded` | client | required |  |  |
| `late-error-reply-to-a-tflush-reclaims-both-tags` | client | required |  |  |
| `late-rflush-does-not-terminate-the-session` | client | required |  |  |
| `racey-error-reply-is-delivered-too` | client | required |  |  |
| `request-timeout-is-flushed-too` | client | required |  |  |
| `tflush-answered-with-an-error-frees-both-tags` | client | required |  |  |
| `already-cancelled-token-sends-nothing` | client | required |  |  |
| `unanswered-rflush-times-out-rather-than-hanging` | client | required |  |  |
| `delivers-racey-reply` | client | required | `8.14` |  |
| `disposal-against-a-silent-server-is-bounded-whatever-the-fid-count` | client | required |  |  |
| `disposal-completes-against-a-silent-server` | client | required |  |  |
| `disposing-a-fid-against-a-silent-server-throws-nothing` | client | required |  |  |
| `flush-of-unknown-tag-is-answered` | client | required |  |  |
| `waits-for-rflush-before-tag-reuse` | client | required | `8.14` |  |
| `tag-reused-the-instant-its-rflush-arrives-is-served` | server | required |  |  |
| `flushed-request-answered-once` | server | required | `5.3` |  |
| `multiple-flushes-answered-in-order` | server | required |  |  |
| `no-reply-after-rflush` | server | required |  |  |
| `rflush-always-sent` | server | required |  |  |
| `flush-behind-one-queued-ordinary-request-must-reach-its-reserved-slot` | server | required |  |  |

## `handler`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `throwing-handler-is-io-error-and-the-session-lives` | server | required |  |  |
| `missing-capability-is-eopnotsupp` | server | required |  |  |
| `readlink-on-a-plain-file-is-refused` | server | required |  |  |

## `interop`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `error-answering-the-version-request-is-a-version-error` | client | required | `8.38` |  |
| `rename-falls-back-to-trename-when-the-server-lacks-trenameat` | client | required | `8.37` |  |
| `linux-v9fs-against-our-jsonfs` | client | recommended |  |  |
| `our-client-against-diod` | client | recommended |  |  |
| `our-client-against-p9ufs` | client | recommended |  |  |
| `plan9port-client-against-our-jsonfs` | client | recommended |  |  |

## `jsonfs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `encoded-keys-remain-distinct-after-write-back-and-reload` | server | required |  | `E17` |
| `empty-writes-preserve-content-and-sparse-writes-survive-reload` | server | required |  |  |
| `cap-rejects-create-write-and-rename-without-changing-open-objects` | server | required |  | `F12c` |
| `exact-serialized-threshold-and-concurrent-growth` | server | required |  | `F12c` |
| `incomplete-utf-8-at-clunk-is-an-error-and-fid-is-released` | server | required |  | `F28` |
| `utf-8-characters-may-cross-write-boundaries-and-invalid-bytes-are-atomic` | server | required |  | `F28` |
| `whole-file-pipeline-splits-utf-8-across-wire-chunks` | server | required |  | `F28` |
| `create-and-move-cannot-exceed-reloadable-depth` | server | required |  | `F29` |
| `write-back-failure-reports-error-and-leaves-a-complete-document` | server | required |  | `F30` |
| `mutation-between-pages-does-not-skip-untouched-entries` | server | required |  | `F8` |
| `create-asking-for-a-flag-is-refused` | server | required | `8.27` |  |
| `move-into-own-subtree-is-refused` | server | required |  |  |
| `write-beyond-the-scalar-bound-is-refused-before-it-allocates` | server | required |  |  |
| `array-append-only-at-next-index` | server | required |  |  |
| `create-beyond-max-entries-is-enospc` | server | required | `8.40` |  |
| `directory-sync-fsyncs-a-real-directory-and-fails-loudly-on-a-missing-one` | server | required |  |  |
| `dot-and-dot-dot-keys-are-encoded` | server | required |  |  |
| `empty-key-is-percent` | server | required |  |  |
| `key-encoding-is-injective` | server | required |  |  |
| `number-formatting-is-invariant` | server | required |  |  |
| `refuses-deep-document` | server | required |  |  |
| `refuses-oversize-document` | server | required |  |  |
| `scalar-type-demotion-is-documented` | server | required |  |  |
| `set-attr-applies-a-truncation-and-a-rename-together` | server | required | `8.27` |  |
| `set-attr-refuses-a-length-it-cannot-produce` | server | required |  |  |
| `set-attr-with-an-unsupported-field-changes-nothing` | server | required | `8.27` |  |
| `write-back-indents-numbers` | server | required |  |  |
| `write-back-is-atomic` | server | required |  |  |
| `write-back-is-coalesced-and-flushed-on-shutdown` | server | required | `8.40` |  |
| `write-back-keeps-non-ascii-as-itself` | server | required |  |  |

## `limits`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `defaults-are-the-architectures-numbers` | protocol | required |  |  |
| `defaults-validate` | protocol | required |  |  |
| `pre-negotiation-cap-is-not-the-configured-maximum` | protocol | required |  |  |
| `validate-rejects-inconsistent-limits` | protocol | required |  |  |
| `window-reused-the-instant-its-reply-arrives-is-never-refused` | server | required | `8.8` |  |
| `flush-answered-while-window-full` | server | required | `8.8` |  |
| `listener-wide-bound-holds` | server | required | `8.8` |  |
| `one-connection-cannot-stall-another` | server | required |  |  |
| `allocation-stays-within-msize` | server | required | `AC-b` |  |
| `fid-flood-hits-cap-not-memory` | server | required |  |  |
| `half-header-times-out` | server | required |  |  |
| `oversize-read-count-is-clamped` | server | required |  |  |
| `oversize-read-count-on-a-file-larger-than-msize-is-clamped-to-msize` | server | required |  |  |
| `pre-version-oversize-frame-closes-the-connection` | server | required |  |  |
| `size-lie-closes-only-that-connection` | server | required |  |  |
| `tag-flood-backpressures` | server | required |  |  |
| `walk-of-seventeen-elements-is-refused` | server | required |  |  |
| `budget-of-zero-throttles-nothing` | server | required | `8.40` |  |
| `request-flood-is-metered-and-flush-still-answered` | server | required | `8.40` |  |
| `success-clears-the-address-budget` | server | required | `8.40` |  |
| `auth-flood-stops-paying-the-derivation` | server | required | `8.40` |  |
| `one-address-cannot-hold-every-connection` | server | required | `8.40` |  |

## `lock`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `constants-match-reference` | protocol | required | `4.8` |  |
| `synthetic-filesystem-magic-is-v9fs` | protocol | required |  |  |
| `zero-length-and-unlock-are-the-references-conventions` | protocol | required |  |  |

## `mode`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `exec-is-sent-as-read-only-in-dot-l` | protocol | required |  |  |
| `linux-only-flags-have-no9-p2000-spelling` | protocol | required |  |  |
| `server-decoding-still-reads-access-mode-three` | protocol | required |  |  |
| `flags-9p2000-has-still-project` | protocol | required |  |  |
| `qid-mirror-skips-only-the-mount-bit` | protocol | required |  |  |
| `remove-on-close-flag-has-no-linux-spelling` | protocol | required |  |  |
| `values-match-reference` | protocol | required | `4.4`, `4.6` |  |
| `access-modes-are-the-low-two-bits` | protocol | required |  |  |
| `append-is-a-flag-not-an-access-mode` | protocol | required | `4.5` |  |
| `open-flags-are-distinct-single-bits` | protocol | required |  |  |

## `names`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `malformed-names-are-answered-then-only-that-connection-closes` | server | required |  | `F7a` |
| `configured-limit-covers-every-name-field-and-keeps-session-usable` | server | required |  | `F7c` |
| `malformed-names-respect-each-fields-legal-exceptions` | server | required | `8.2–3` |  |

## `observability`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `backslashes-are-escaped` | protocol | required |  |  |
| `null-arguments-throw` | protocol | required |  |  |
| `cap-falls-on-a-rune-boundary` | protocol | required |  |  |
| `untrusted-strings-escaped-and-capped` | protocol | required | `8.11` |  |
| `message-the-dialect-does-not-carry-is-answered-and-the-session-stays-up` | server | required |  |  |
| `read-at-the-end-of-the-address-space-is-an-empty-read` | server | required |  |  |
| `reply-too-long-to-encode-is-an-error-reply-rather-than-silence` | server | required |  |  |
| `throwing-sink-does-not-break-the-session` | server | required |  |  |
| `messages-are-counted-by-type` | server | required |  |  |
| `request-log-sees-escaped-summaries` | server | required |  |  |

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
| `attach-identity-reaches-the-filesystem` | server | required |  |  |
| `core-denies-before-handler-is-called` | server | required |  |  |
| `create-needs-write-on-the-directory` | server | required |  |  |
| `only-the-owner-may-change-mode` | server | required |  |  |
| `remove-needs-write-in-the-parent` | server | required |  |  |
| `walk-needs-search-permission` | server | required |  |  |

## `projection`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `a-9p2000-symlink-qid-is-a-symlink-in-a-listing-too` | client | required |  |  |
| `a-9p2000-symlink-qid-is-reported-as-a-symlink` | client | required | `8.17` |  |
| `create-asking-for-a-server-owned-flag-is-refused` | client | required | `8.19` |  |
| `create-of-a-kind-with-no-extension-field-is-refused` | client | required | `8.15` |  |
| `half-stated-mode-word-is-completed-from-the-record` | client | required | `8.19` |  |
| `numeric-group-is-refused-on-plain9-p2000` | client | required | `8.15` |  |
| `server-clock-update-is-not-sent-as-an-fsync` | client | required | `8.15` |  |
| `textual-group-and-a-name-are-refused-on-dot-l` | client | required |  |  |
| `answer-that-is-not-the-offer-is-a-version-error` | client | required | `8.18` |  |
| `atime-update-never-reaches-a-wstat-server` | client | required |  |  |
| `unmarked-getattr-field-keeps-its-default` | client | required | `8.17` |  |
| `unsolicited-rversion-terminates-the-session` | client | required | `8.18` |  |
| `exec-opens-read-only-on-a-dot-l-server` | client | required | `8.16` |  |
| `linux-only-open-flags-never-reach-a-9p2000-server` | client | required | `8.15` |  |
| `file-flags-never-reach-a-dot-l-create` | client | required | `8.15` |  |
| `file-flags-never-reach-a-dot-l-setattr` | client | required | `8.15` |  |
| `remove-on-close-flag-never-reaches-a-dot-l-create` | client | required |  |  |
| `remove-on-close-flag-never-reaches-a-dot-l-open` | client | required | `8.15` |  |
| `suffix-stripping-downgrade-is-the-only-one` | client | required | `8.18` |  |

## `qid`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `equality-is-by-all-three-fields` | protocol | required |  |  |
| `type-bits-follow-linux` | protocol | required | `4.1` |  |
| `wire-size-is-thirteen` | protocol | required |  |  |

## `remove`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `unknown-flag-is-einval` | server | required | `8.20` |  |
| `directory-with-removedir-is-removed` | server | required |  |  |
| `directory-without-removedir-is-eisdir` | server | required | `8.20` |  |
| `file-with-removedir-is-enotdir` | server | required |  |  |
| `file-without-flags-is-removed` | server | required |  |  |

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
| `unbound-scheme-is-refused` | server | required |  |  |
| `connections-are-counted` | server | required |  |  |
| `disposal-completes-while-the-peer-has-stopped-reading` | server | required |  |  |
| `endpoints-are-published-once-bound` | server | required |  |  |
| `graceful-shutdown-drains` | server | required |  |  |
| `no-listen-address-is-refused` | server | required |  |  |
| `concurrent-replies-are-serialised` | server | required |  |  |
| `append-writes-across-connections-do-not-overwrite-each-other` | server | required | `8.30` |  |
| `cleanup-continues-when-reporting-a-handler-failure-also-throws` | server | required |  |  |
| `closing-a-file-waits-for-its-active-write` | server | required | `8.28` |  |
| `deep-cloned-walk-preserves-all-ancestors` | server | required | `8.31` |  |
| `disconnect-finalizes-open-state` | server | required |  |  |
| `dot-dot-from-a-file-is-rejected` | server | required |  |  |
| `incomplete-xattr-reset-logs-failure-and-continues-cleanup` | server | required |  |  |
| `listener-retains-only-active-sessions` | server | required | `8.35` |  |
| `partial-permission-failure-does-not-change-fids` | server | required | `8.31` |  |
| `permission-classes-follow-the-dialect` | server | required | `8.32` |  |
| `queued-operation-rejects-retired-fid-after-number-reuse` | server | required |  |  |
| `rename-outside-restricted-attach-clamps-parent-walk-and-keeps-removal-location` | server | required | `8.31` |  |
| `renamed-ancestor-rebases-existing-aliases-across-connections` | server | required | `8.31` |  |
| `renamed-directory-ascends-through-its-new-parents` | server | required |  |  |
| `shutdown-defers-disposal-until-an-uncooperative-read-returns` | server | required |  |  |
| `slow-lookup-does-not-block-other-clients-and-revalidates-after-rename` | server | required | `8.31` |  |
| `thrown-lookup-errors-preserve-walk-bindings` | server | required |  |  |
| `version-reset-commits-a-complete-xattr-sink` | server | required |  |  |
| `xattr-reads-check-permissions-before-calling-the-handler` | server | required | `8.33` |  |
| `shutdown-keeps-flush-gates-and-handlers-alive-until-inline-reader-unwinds` | server | required | `8.28` |  |

## `session`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `oversize-msize-answer-throws` | client | required |  |  |
| `unexpected-reply-type-terminates-session` | client | required |  |  |
| `reply-larger-than-msize-terminates-session` | client | required |  |  |
| `unknown-tag-terminates-session` | client | required | `8.12` |  |

## `statfs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `statfs-is-linux-only` | server | required |  |  |
| `synthetic-servers-report-v9fs-magic` | server | required | `4.9` |  |
| `filesystem-answers-when-the-handler-does-not` | server | required |  |  |

## `tag`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `memory-address-needs-its-transport` | client | required |  |  |
| `error-reply-releases-its-tag-exactly-once` | client | required |  |  |
| `illegal-message-for-the-dialect-is-refused-before-the-wire` | client | required |  |  |
| `rerror-carries-both-halves-in-dot-u` | client | required |  |  |
| `rlerror-becomes-a-typed-exception` | client | required |  |  |
| `requests-are-pipelined-and-replies-may-arrive-out-of-order` | client | required |  |  |
| `tags-are-reused-after-their-reply` | client | required |  |  |
| `transport-overload-dials-and-negotiates` | client | required |  |  |
| `whole-tag-pool-is-rentable-after-an-error-reply` | client | required |  |  |
| `generated-reply-and-flush-schedules-preserve-tag-ownership` | client | recommended |  |  |
| `tag-reused-the-instant-its-reply-arrives-is-never-refused` | server | required |  |  |
| `flush-that-suppresses-a-reply-frees-the-tag-at-once` | server | required |  |  |
| `late-release-does-not-evict-the-next-request-on-the-same-tag` | server | required |  |  |
| `clear-cancels-everything-in-flight` | server | required |  |  |
| `completion-is-exactly-once` | server | required |  |  |
| `duplicate-tag-rejected` | server | required | `8.6` |  |
| `flushing-an-unknown-tag-suppresses-nothing` | server | required |  |  |
| `notag-is-ordinary-outside-version` | server | required | `8.6` |  |
| `release-frees-the-tag-for-reuse` | server | required |  |  |

## `todofs`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `read-after-a-write-answers-the-user-list` | server | required | `arch:7` |  |
| `add-and-remove-round-trip` | server | required |  |  |
| `listing-is-sorted-by-name` | server | required |  |  |
| `malformed-command-is-einval` | server | required |  |  |
| `non-admin-gets-eacces` | server | required |  |  |
| `remove-takes-the-users-lists-and-items` | server | required |  |  |
| `trailing-newline-is-optional` | server | required |  |  |
| `create-asking-for-a-flag-is-refused` | server | required | `8.27` |  |
| `read-after-a-write-answers-the-stored-value` | server | required |  |  |
| `field-write-capped-at64-kib` | server | required |  |  |
| `fields-round-trip` | server | required |  |  |
| `list-and-item-quotas-are-enospc` | server | required | `8.40` |  |
| `mkdir-takes-the-next-number-only` | server | required |  |  |
| `new-list-and-item-start-empty` | server | required |  |  |
| `rmdir-removes-items-and-empty-lists` | server | required |  |  |
| `status-rejects-other-values` | server | required |  |  |
| `two-opens-of-one-field-see-one-file` | server | required |  |  |
| `truncating-open-clunked-without-a-write-empties-the-field` | server | required | `8.27` |  |
| `truncating-open-of-status-resets-it-to-open` | server | required |  |  |
| `truncating-open-of-the-control-file-keeps-the-users` | server | required | `8.27` |  |
| `set-attr-length-zero-empties-a-free-text-field` | server | required | `8.27` |  |
| `set-attr-length-zero-on-status-is-refused` | server | required | `8.27` |  |
| `set-attr-length-zero-on-the-control-file-is-refused` | server | required |  |  |
| `set-attr-on-a-directory-is-refused` | server | required |  |  |
| `set-attr-refuses-a-length-it-cannot-produce` | server | required |  |  |
| `set-attr-with-an-unsupported-field-changes-nothing` | server | required | `8.27` |  |
| `cascade-delete-removes-lists-and-items` | server | required |  |  |
| `concurrent-writers-serialise` | server | required |  |  |
| `ensure-user-is-idempotent` | server | required |  |  |
| `qid-path-carries-the-table-tag` | server | required |  |  |
| `queries-are-scoped-by-user` | server | required |  |  |
| `schema-version-refuses-newer` | server | required |  |  |
| `two-concurrent-creates-at-the-quota-have-exactly-one-winner` | server | required | `8.40` |  |
| `shipped-wiring-refuses-an-unauthenticated-attach` | server | required |  |  |
| `store-queries-are-scoped-even-when-the-row-id-is-known` | server | required |  |  |
| `user-a-cannot-reach-user-b` | server | required | `AC-c` |  |
| `user-a-cannot-walk-read-stat-or-list-user-b` | server | required |  |  |

## `transport`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `dropped-reply-times-out-flushes-and-leaves-the-session-usable` | client | required |  |  |
| `duplicate-after-retirement-fails-every-pending-caller` | client | required |  |  |
| `mid-frame-close-fails-the-pending-calls` | client | required |  |  |
| `throttled-quarter-megabyte-transfer-completes-with-the-window` | client | required |  |  |
| `delayed-reply-respects-the-flush-boundary` | client | required |  |  |
| `single-byte-reads-reassemble-the-reply` | client | required |  |  |
| `memory-transport-disposed-listener-accepts-null` | protocol | required |  |  |
| `name-binds-once` | protocol | required |  |  |
| `close-reason-reaches-the-peer` | protocol | required |  |  |
| `dialling-an-unbound-name-fails` | protocol | required |  |  |
| `dispose-is-a-normal-close-and-is-idempotent` | protocol | required |  |  |
| `disposing-a-listener-unbinds-the-name` | protocol | required |  |  |
| `listen-accept-and-dial-meet` | protocol | required |  |  |
| `only-memory-addresses-are-accepted` | protocol | required |  |  |
| `memory-transport-round-trips-frames` | protocol | required |  |  |
| `first-close-reason-wins` | protocol | required |  |  |
| `transports-are-isolated-from-each-other` | protocol | required |  |  |
| `writing-after-close-throws` | protocol | required |  |  |
| `host-named-host-is-still-a-host` | protocol | required |  |  |
| `pathless-web-socket-url-gains-the-root` | protocol | required |  |  |
| `canonical-form-round-trips` | protocol | required |  |  |
| `illegal-addresses-are-refused` | protocol | required |  |  |
| `is-secure-follows-the-scheme` | protocol | required |  |  |
| `legal-addresses-parse` | protocol | required |  |  |
| `null-is-not-an-address` | protocol | required |  |  |
| `parse-names-the-offending-text` | protocol | required |  |  |
| `port-zero-is-the-wildcard-listen-port` | protocol | required |  |  |
| `tcp-transport-disposed-listener-accepts-null` | protocol | required |  |  |
| `closing-is-an-end-of-stream-at-the-peer` | protocol | required |  |  |
| `connection-cap-refuses` | protocol | required |  |  |
| `impossible-bounds-are-refused` | protocol | required |  |  |
| `no-delay-is-set` | protocol | required |  |  |
| `only-tcp-addresses-are-accepted` | protocol | required |  |  |
| `tcp-transport-round-trips-frames` | protocol | required |  |  |
| `accepted-connection-knows-the-peer` | protocol | required |  |  |
| `bound-address-carries-the-real-port` | protocol | required |  |  |
| `actual-tls-and-wss-handshakes-enforce-server-purpose` | protocol | required |  |  |
| `cancelling-one-accept-preserves-handshake-and-disposal-drains-pending-and-queued-connections` | protocol | required |  |  |
| `concurrent-tcp-listener-and-accepted-connection-disposal-does-not-race-slot-release` | protocol | required |  |  |
| `custom-root-preserves-peer-role-on-ca-issued-leaf` | protocol | required | `8.36` |  |
| `disposing-tcp-listener-wakes-accept-blocked-at-connection-cap` | protocol | required |  |  |
| `expired-handshake-returns-capacity-for-waiting-healthy-peer` | protocol | required |  |  |
| `intermediate-purpose-restriction-cannot-be-repaired-by-custom-trust` | protocol | required | `8.36` |  |
| `silent-peer-does-not-block-healthy-handshake-and-pending-plus-active-connections-stay-capped` | protocol | required | `8.35` |  |
| `fatal-failure-ends-the-loop` | protocol | required |  |  |
| `transient-failure-is-retried-and-the-listener-lives-on` | protocol | required |  |  |
| `cancellation-ends-the-loop` | protocol | required |  |  |
| `disposal-ends-the-loop` | protocol | required |  |  |
| `delayed-writer-never-exceeds-the-reply-channel-bound` | server | required |  |  |
| `duplicate-request-is-rejected-while-the-original-remains-pending` | server | required |  |  |
| `partial-header-close-or-stall-affects-only-its-connection` | server | required |  |  |

## `transport-tls`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `clean-shutdown-sends-close-notify` | protocol | required |  |  |
| `negotiated-protocol-is-at-least-tls-12` | protocol | required |  |  |
| `only-tls-addresses-are-accepted` | protocol | required |  |  |
| `additional-peer-check-can-refuse-a-trusted-certificate` | protocol | required |  |  |
| `additional-peer-check-cannot-rescue-a-bad-certificate` | protocol | required |  |  |
| `hostname-mismatch-rejected-by-default` | protocol | required |  |  |
| `insecure-opt-out-logs` | protocol | required |  |  |
| `listening-without-a-certificate-is-refused` | protocol | required |  |  |
| `mutual-tls-identity-exposed` | protocol | required |  |  |
| `renegotiation-is-off-on-both-sides` | protocol | required |  |  |
| `insecure-opt-out-does-not-reach-the-listener` | protocol | required |  |  |
| `tls-11-refused` | protocol | required |  |  |
| `untrusted-self-signed-rejected-by-default-and-accepted-by-the-trust-hook` | protocol | required |  |  |

## `transport-websocket`

| Id | Layer | Tier | Rules | Conformance |
| --- | --- | --- | --- | --- |
| `plain-http-request-is-refused` | protocol | required |  |  |
| `client-web-socket-handshake-succeeds-over-test-certificate` | protocol | required |  |  |
| `close-reasons-map-to-status-codes` | protocol | required |  |  |
| `fragmented-oversize-closes1009` | protocol | required |  |  |
| `fragmented-under-cap-accepted` | protocol | required |  |  |
| `only-a-sixteen-byte-key-is-legal` | protocol | required |  |  |
| `only-web-socket-addresses-are-accepted` | protocol | required |  |  |
| `plain-web-socket-round-trips-frames` | protocol | required |  |  |
| `text-frame-closes` | protocol | required |  |  |
| `accept-token-follows-the-rfc` | protocol | required |  |  |
| `missing-origin-is-refused-when-an-allow-list-exists` | protocol | required |  |  |
| `allowed-origin-is-accepted-and-exposed` | protocol | required |  |  |
| `empty-allow-list-logs-a-warning-at-listen-time` | protocol | required |  |  |
| `disallowed-origin-refused` | protocol | required |  |  |

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
