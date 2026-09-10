#!/usr/bin/env python3
"""Report xUnit's discovered cases by suite, layer and framework (does not run tests)."""
import argparse
from collections import Counter
import re
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--configuration', default='Debug')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
suites = ('Conformance', 'Robustness', 'Regression', 'Chaos', 'StateMachine', 'Security', 'Compat')
print('Discovery counts include opt-in cases; execution/skips are reported by the test run.')
print('Layer/framework ' + ' '.join(suites))
for layer in ('Protocol', 'Client', 'Server'):
    for framework in ('net8.0', 'net10.0'):
        assembly = root / f'tests/NineP.{layer}.Tests/bin/{args.configuration}/{framework}/NineP.{layer}.Tests.dll'
        result = subprocess.run(['dotnet', str(assembly), '--xunit-list', 'full'], text=True, capture_output=True)
        # MTP returns 8 for discovery-only execution because no tests were run.
        if result.returncode not in (0, 8):
            raise SystemExit(result.stdout + result.stderr)
        cases = result.stdout.split('  - Display name:')[1:]
        if not cases:
            raise SystemExit(f'No discovered cases in {assembly}')
        counts = Counter()
        for case in cases:
            match = re.search(r'"Category": \["([^"]+)"\]', case)
            if not match or match[1] not in suites:
                raise SystemExit(f'Missing or invalid suite: {case}')
            counts[match[1]] += 1
        print(f'{layer}/{framework} ' + ' '.join(str(counts[s]) for s in suites))
