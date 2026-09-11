#!/usr/bin/env python3
"""Bootstrap the shared test index from this port's suite.

The C# port is the reference implementation (workspace ARCHITECTURE.md §12), so its
suite is where the shared obligation list comes from. This script runs **once** to
seed `docs/9p/fixtures/test-index.json` in the workspace; after that the index is
authored by hand and every port maps its own tests onto it.

  test-index.json   language-neutral obligations, vendored into every port
  docs/test-map.json  this port's id -> test method, plus its local-only tests

Usage: python3 scripts/gen-test-index.py [--workspace ../]
"""
import argparse
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

# area, tier for every test class. A class not listed here is an error: a new class
# must be classified deliberately, which is the whole point of the index.
CLASSES = {
    # --- client ------------------------------------------------------------
    'Client.Chaos.ClientTransportFaultTests': ('transport', 'required'),
    'Client.Chaos.FaultyTransportTests': ('harness', 'local'),
    'Client.Compat.ClientInteropRegressionTests': ('interop', 'required'),
    'Client.Compat.DialectNegotiationClientTests': ('dialect', 'required'),
    'Client.Compat.InteropTests': ('interop', 'recommended'),
    'Client.Compat.TargetFrameworkTests': ('harness', 'local'),
    'Client.Conformance.CliExitCodeTests': ('cli', 'required'),
    'Client.Conformance.CliListingTests': ('cli', 'required'),
    'Client.Conformance.CliTests': ('cli', 'required'),
    'Client.Conformance.ClientBoundaryTests': ('client-api', 'required'),
    'Client.Conformance.ClientFidTests': ('fid', 'required'),
    'Client.Conformance.ClientFileApiTests': ('client-api', 'required'),
    'Client.Conformance.ClientFlushTests': ('flush', 'required'),
    'Client.Conformance.ClientProjectionTests': ('projection', 'required'),
    'Client.Conformance.TagMultiplexerTests': ('tag', 'required'),
    'Client.Regression.ClientLifetimeRegressionTests': ('fid', 'required'),
    'Client.Robustness.ClientProtocolErrorTests': ('codec-robustness', 'required'),
    'Client.Robustness.ClientSessionTerminationTests': ('session', 'required'),
    'Client.Security.CliOidcTests': ('auth', 'recommended'),
    'Client.StateMachine.TagMultiplexerMachine': ('tag', 'recommended'),
    # --- protocol ----------------------------------------------------------
    'Protocol.Compat.ErrorTableTests': ('error', 'required'),
    'Protocol.Compat.NegotiationTests': ('dialect', 'required'),
    'Protocol.Compat.TargetFrameworkTests': ('harness', 'local'),
    'Protocol.Compat.TfsyncTests': ('codec', 'required'),
    'Protocol.Conformance.AttrProjectorTests': ('attr', 'required'),
    'Protocol.Conformance.AuthenticatorTests': ('auth', 'required'),
    'Protocol.Conformance.BufferLeaseTests': ('harness', 'local'),
    'Protocol.Conformance.ConstantsTests': ('constants', 'required'),
    'Protocol.Conformance.DialectFieldTests': ('dialect', 'required'),
    'Protocol.Conformance.DialectLegalityTests': ('dialect', 'required'),
    'Protocol.Conformance.DirEntryCodecTests': ('directory', 'required'),
    'Protocol.Conformance.DotLMessageTests': ('codec', 'required'),
    'Protocol.Conformance.EmptyFieldTests': ('codec', 'required'),
    'Protocol.Conformance.ErrorProjectionTests': ('error', 'required'),
    'Protocol.Conformance.ErrorTests': ('error', 'required'),
    'Protocol.Conformance.GetAttrMaskTests': ('attr', 'required'),
    'Protocol.Conformance.GoldenVector9P2000Tests': ('codec', 'required'),
    'Protocol.Conformance.GoldenVectorTests': ('codec', 'required'),
    'Protocol.Conformance.LimitsTests': ('limits', 'required'),
    'Protocol.Conformance.LockTests': ('lock', 'required'),
    'Protocol.Conformance.MemoryTransportTests': ('transport', 'required'),
    'Protocol.Conformance.MessageTypeTests': ('codec', 'required'),
    'Protocol.Conformance.MessageWriterTests': ('codec', 'required'),
    'Protocol.Conformance.ModeBitsTests': ('mode', 'required'),
    'Protocol.Conformance.NinePAddressTests': ('transport', 'required'),
    'Protocol.Conformance.OpenModeTests': ('mode', 'required'),
    'Protocol.Conformance.QidTests': ('qid', 'required'),
    'Protocol.Conformance.RgetattrTests': ('attr', 'required'),
    'Protocol.Conformance.SetAttrTests': ('attr', 'required'),
    'Protocol.Conformance.StatCodecTests': ('codec', 'required'),
    'Protocol.Conformance.StatRecordTests': ('codec', 'required'),
    'Protocol.Conformance.TcpTransportTests': ('transport', 'required'),
    'Protocol.Conformance.TlsTransportTests': ('transport-tls', 'required'),
    'Protocol.Conformance.WebSocketTransportTests': ('transport-websocket', 'required'),
    'Protocol.Conformance.WireReaderTests': ('codec', 'required'),
    'Protocol.Conformance.WireWriterTests': ('codec', 'required'),
    'Protocol.Regression.TransportHandshakeRegressionTests': ('transport', 'required'),
    'Protocol.Robustness.AcceptPolicyTests': ('transport', 'required'),
    'Protocol.Robustness.FrameReaderTests': ('codec-robustness', 'required'),
    'Protocol.Robustness.MutationMatrixTests': ('codec-robustness', 'required'),
    'Protocol.Robustness.OverflowTests': ('codec-robustness', 'required'),
    'Protocol.Robustness.WireReaderRejectionTests': ('codec-robustness', 'required'),
    'Protocol.Security.AuthenticatorSecurityTests': ('auth', 'required'),
    'Protocol.Security.PasswordAuthenticatorTests': ('auth', 'required'),
    'Protocol.Security.TlsSecurityTests': ('transport-tls', 'required'),
    'Protocol.Security.TokenAuthenticatorTests': ('auth', 'required'),
    'Protocol.Security.UntrustedTextTests': ('observability', 'required'),
    'Protocol.Security.WebSocketOriginTests': ('transport-websocket', 'required'),
    'Protocol.StateMachine.CodecProperties': ('codec', 'recommended'),
    # --- server ------------------------------------------------------------
    'Server.Chaos.BenchmarkProgressTests': ('harness', 'local'),
    'Server.Chaos.BenchmarkRssTests': ('harness', 'local'),
    'Server.Chaos.ReadPathTests': ('harness', 'local'),
    'Server.Chaos.ScaleTests': ('scale', 'recommended'),
    'Server.Chaos.ServerTransportFaultTests': ('transport', 'required'),
    'Server.Compat.TargetFrameworkTests': ('harness', 'local'),
    'Server.Conformance.AuthRefusalTests': ('auth', 'required'),
    'Server.Conformance.BoundaryTests': ('boundary', 'required'),
    'Server.Conformance.CapabilityTests': ('handler', 'required'),
    'Server.Conformance.ClunkRemoveTests': ('clunk', 'required'),
    'Server.Conformance.ConformanceTests': ('cli', 'required'),
    'Server.Conformance.ContentBoundaryTests': ('boundary', 'required'),
    'Server.Conformance.CreateTests': ('create', 'required'),
    'Server.Conformance.DirectoryPackerTests': ('directory', 'required'),
    'Server.Conformance.DirectoryReadTests': ('directory', 'required'),
    'Server.Conformance.DispatcherTests': ('dispatch', 'required'),
    'Server.Conformance.ExampleHostTests': ('examples', 'recommended'),
    'Server.Conformance.FlushTests': ('flush', 'required'),
    'Server.Conformance.GetattrTests': ('attr', 'required'),
    'Server.Conformance.JsonFsBoundaryTests': ('jsonfs', 'required'),
    'Server.Conformance.JsonFsTests': ('jsonfs', 'required'),
    'Server.Conformance.NameLimitTests': ('names', 'required'),
    'Server.Conformance.ObservabilityTests': ('observability', 'required'),
    'Server.Conformance.OpenStateTests': ('open', 'required'),
    'Server.Conformance.PermissionTests': ('permission', 'required'),
    'Server.Conformance.ServerDocTests': ('docs', 'recommended'),
    'Server.Conformance.ServerLifecycleTests': ('server-lifecycle', 'required'),
    'Server.Conformance.ServerSetattrTests': ('attr', 'required'),
    'Server.Conformance.ServerVersionTests': ('dialect', 'required'),
    'Server.Conformance.StatFsTests': ('statfs', 'required'),
    'Server.Conformance.TodoFsCtlTests': ('todofs', 'required'),
    'Server.Conformance.TodoFsItemTests': ('todofs', 'required'),
    'Server.Conformance.TodoFsSetAttrTests': ('todofs', 'required'),
    'Server.Conformance.TodoFsStoreTests': ('todofs', 'required'),
    'Server.Conformance.TwriteTests': ('write', 'required'),
    'Server.Conformance.UnlinkatTests': ('remove', 'required'),
    'Server.Conformance.WalkTests': ('walk', 'required'),
    'Server.Conformance.WriterTests': ('server-lifecycle', 'required'),
    'Server.Conformance.WstatTests': ('attr', 'required'),
    'Server.Regression.FlushBackpressureRegressionTests': ('flush', 'required'),
    'Server.Regression.ServerLifecycleRegressionTests': ('server-lifecycle', 'required'),
    'Server.Regression.ServerSemanticsRegressionTests': ('semantics', 'required'),
    'Server.Regression.SessionShutdownRegressionTests': ('server-lifecycle', 'required'),
    'Server.Regression.TagReuseStressTests': ('tag', 'required'),
    'Server.Robustness.AfidLimitTests': ('auth', 'required'),
    'Server.Robustness.BackpressureTests': ('limits', 'required'),
    'Server.Security.AfidTests': ('auth', 'required'),
    'Server.Security.HostileClientTests': ('limits', 'required'),
    'Server.Security.JwksTests': ('auth', 'recommended'),
    'Server.Security.KeycloakAuthTests': ('auth', 'recommended'),
    'Server.Security.KeycloakAuthenticatorTests': ('auth', 'recommended'),
    'Server.Security.ResourceLimitTests': ('limits', 'required'),
    'Server.Security.ShippedTodoFsTests': ('todofs', 'required'),
    'Server.Security.TodoFsIsolationTests': ('todofs', 'required'),
    'Server.StateMachine.FidLifecycleMachine': ('fid', 'recommended'),
    'Server.StateMachine.FidTableTests': ('fid', 'required'),
    'Server.StateMachine.TagTableTests': ('tag', 'required'),
}

# Per-method tier overrides, where a class is not uniform.
OVERRIDES = {
    # Opt-in full-scale workloads: the bounded CI variant is the obligation.
    'Server.Chaos.ScaleTests.F10_FullFileStreamsBeyondFourGiB': 'local',
    'Server.Chaos.ScaleTests.F11_FullMillionEntryDirectory': 'local',
    'Server.Chaos.ScaleTests.F12_FullMillionMutableCreates': 'local',
    # HttpListener's https prefix is a .NET platform quirk (Decision Log S-1/S-2).
    'Protocol.Conformance.WebSocketTransportTests.HttpListenerHttpsIsUnusableHere': 'local',
}

ARTICLE = re.compile(r'^(a|an|the)-')
BOUNDARY = re.compile(r'(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])')
CONFORMANCE_REF = re.compile(r'^((?:[EF]\d+[a-z]?_)+)')

# Names the PascalCase splitter cannot read: a protocol literal glued to a word, or a
# figure glued to a unit. Spelled here rather than guessed, because an id is a contract.
SLUG_FIXUPS = {
    'a9-p2000': 'a-9p2000',
    'nine-p2000': '9p2000',
    'is160': 'is-160',
    'tls11': 'tls-11',
    'tls12': 'tls-12',
    'utf8': 'utf-8',
    '64-ki-b': '64kib',
    'ki-b': 'kib',
    'mi-b': 'mib',
    'gi-b': 'gib',
}

# A class whose hint reads better than the bare class name.
HINTS = {
    'AuthRefusalTests': 'refusal-shape',
    'CodecProperties': 'property',
    'FidLifecycleMachine': 'model',
    'TagMultiplexerMachine': 'model',
}


def hint_for(cls: str) -> str:
    """A short qualifier taken from the test class, for ids the method name cannot carry alone."""
    name = cls.rsplit('.', 1)[-1]
    if name in HINTS:
        return HINTS[name]
    name = re.sub(r'Tests$', '', name)
    for noise in ('Conformance', 'Regression', 'Robustness', 'Security', 'Compat', 'Chaos'):
        name = name.replace(noise, '')
    body = BOUNDARY.sub('-', name).lower().strip('-')
    return apply_fixups(body)


def apply_fixups(body: str) -> str:
    for wrong, right in SLUG_FIXUPS.items():
        body = re.sub(rf'(?<![a-z0-9]){re.escape(wrong)}(?![a-z0-9])', right, body)
    return re.sub(r'-+', '-', body).strip('-')


def slug(method: str):
    """PascalCase test name -> kebab-case behaviour slug, leading article dropped."""
    name = method
    refs = []
    m = CONFORMANCE_REF.match(name)
    if m:
        refs = [r for r in m.group(1).rstrip('_').split('_') if r]
        name = name[m.end():]
    body = BOUNDARY.sub('-', name).lower().replace('_', '-')
    body = re.sub(r'-+', '-', body).strip('-')
    body = ARTICLE.sub('', body)
    return apply_fixups(body), refs


def discover():
    out = {}
    for layer in ('Protocol', 'Client', 'Server'):
        dll = ROOT / f'tests/NineP.{layer}.Tests/bin/Debug/net10.0/NineP.{layer}.Tests.dll'
        if not dll.exists():
            sys.exit(f'{dll} not built; run `dotnet build` first')
        run = subprocess.run(['dotnet', str(dll), '--xunit-list', 'full'],
                             text=True, capture_output=True)
        if run.returncode not in (0, 8):
            sys.exit(run.stdout + run.stderr)
        for block in run.stdout.split('  - Display name:')[1:]:
            m = re.search(r'Test method:\s+(\S+)', block)
            c = re.search(r'"Category": \["([^"]+)"\]', block)
            if not m:
                continue
            full = m.group(1).replace('NineP.', '', 1).replace('.Tests.', '.', 1)
            e = out.setdefault(full, {'layer': layer.lower(),
                                      'suite': c.group(1) if c else None, 'cases': 0})
            e['cases'] += 1
    return out


def rules_by_method(path: Path):
    """docs/rule-index.md maps a rule to the test that proves it; invert that."""
    table = {}
    for line in path.read_text().splitlines():
        if not line.startswith('| ') or not line[2:3].isdigit():
            continue
        cells = [c.strip() for c in line.strip('|').split('|')]
        if len(cells) < 4:
            continue
        source, test = cells[1], cells[3].strip('`')
        ref = source.replace('Ref §', '').replace('Arch §', 'arch:')
        table.setdefault(test.split('.')[-1], []).append(ref)
    return table


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--workspace', default=str(ROOT.parent))
    args = ap.parse_args()
    ws = Path(args.workspace).resolve()

    found = discover()
    rules = rules_by_method(ROOT / 'docs/rule-index.md')

    tests, mapping, local = [], {}, []
    unknown = sorted({m.rsplit('.', 1)[0] for m in found} - set(CLASSES))
    if unknown:
        sys.exit('unclassified test classes (add them to CLASSES):\n  ' + '\n  '.join(unknown))

    # Pass 1: the bare behaviour slug, plus the class qualifier for a slug too short to name a
    # behaviour on its own ('dot-l', 'cap-overflow').
    draft = {}
    for method in sorted(found):
        cls, name = method.rsplit('.', 1)
        area, tier = CLASSES[cls]
        if OVERRIDES.get(method, tier) == 'local':
            local.append(method)
            continue
        body, refs = slug(name)
        hint = hint_for(cls)
        if hint in (area, ''):
            hint = ''                                   # 'limits/limits-...' says nothing twice
        if len(body.split('-')) <= 2 and hint and not body.startswith(hint):
            body = f'{hint}-{body}'
        draft[method] = (area, body, hint, refs)

    # Pass 2: where two classes produce the same slug, the class is what distinguishes them
    # ('memory-transport' from 'tcp-transport'), so qualify *both* — leaving one bare would
    # hand it the plain id by accident of sort order.
    clashing = {body for body, n in Counter(
        f'{a}/{b}' for a, b, _, _ in draft.values()).items() if n > 1}
    seen = set()
    for method in sorted(draft):
        area, body, hint, refs = draft[method]
        if f'{area}/{body}' in clashing and hint and not body.startswith(hint):
            body = f'{hint}-{body}'
        cls, name = method.rsplit('.', 1)
        tier = OVERRIDES.get(method, CLASSES[cls][1])
        info = found[method]
        test_id = f'{area}/{body}'
        if test_id in seen:
            sys.exit(f'duplicate id {test_id} from {method}; give its class a HINTS entry')
        seen.add(test_id)
        entry = {
            'id': test_id,
            'area': area,
            'layer': info['layer'],
            'suite': info['suite'].lower(),
            'tier': tier,
            'cases': info['cases'],
        }
        if refs:
            entry['conformance'] = refs
        if name in rules:
            entry['rules'] = sorted(set(rules[name]))
        tests.append(entry)
        mapping[test_id] = method

    index = {
        'version': 1,
        'description': (
            'Language-neutral test obligations shared by every 9P port. An id names a '
            'behaviour, not a test method: each port maps the id onto its own test in its '
            'docs/test-map.json. Seeded from the C# reference port (workspace '
            'ARCHITECTURE.md §12); thereafter authored by hand, and a test added in any port '
            'for a transferable behaviour is added here and owed by every port.'),
        'tiers': {
            'required': 'Every port must have a test for this behaviour before it is done.',
            'recommended': ('Every port should; a port that skips one records the reason in its '
                            'docs/test-map.json "skipped" map. Typically needs a facility not '
                            'every ecosystem has: property testing, an external peer, an OIDC '
                            'issuer, a scale budget.'),
        },
        'areas': dict(sorted(Counter(t['area'] for t in tests).items())),
        'tests': tests,
    }

    out_index = ws / 'docs/9p/fixtures/test-index.json'
    out_index.write_text(json.dumps(index, indent=1) + '\n')

    (ROOT / 'docs/test-map.json').write_text(json.dumps({
        'description': ('Maps each shared test-index.json id onto this port\'s test method, and '
                        'lists the tests that are this port\'s own. Checked by '
                        'TestIndexTests.'),
        'index': 'docs/9p/fixtures/test-index.json',
        'map': mapping,
        'skipped': {},
        'local': local,
    }, indent=1) + '\n')

    counts = Counter(t['tier'] for t in tests)
    print(f'{out_index}: {len(tests)} obligations '
          f'({counts["required"]} required, {counts["recommended"]} recommended)')
    print(f'docs/test-map.json: {len(mapping)} mapped, {len(local)} local')


if __name__ == '__main__':
    main()
