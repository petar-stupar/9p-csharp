#!/usr/bin/env bash
# Fetches or builds every external 9P peer InteropTests can run against, into one directory,
# and prints the environment the tests read. Re-runnable; each step is skipped when its result
# already exists. Versions are pinned so that two machines measure the same peers.
#
#   tests/interop/setup.sh [<peers dir>]          # default: ~/.ninep-interop
#   eval "$(tests/interop/setup.sh --env)"         # just print the exports for an existing dir
#   dotnet test --project tests/NineP.Client.Tests -f net10.0 -- --filter-class NineP.Client.Tests.InteropTests
#
# Needs: go (p9ufs), git + a C toolchain (plan9port), docker (diod), and, for the Linux kernel
# client, lima (brew install lima) — the VM is created here and needs the full Debian kernel,
# because Debian's cloud kernel ships without the 9p module.
set -euo pipefail

P9UFS_VERSION=v0.4.1
PLAN9PORT_COMMIT=b6564bd9
DIOD_IMAGE=ninep-diod
LIMA_VM=ninep-interop

env_only=false
if [ "${1:-}" = "--env" ]; then env_only=true; shift; fi
dir="${1:-$HOME/.ninep-interop}"
here="$(cd "$(dirname "$0")" && pwd)"

print_env() {
  echo "export NINEP_INTEROP_P9UFS=$dir/gobin/p9ufs"
  echo "export NINEP_INTEROP_PLAN9PORT=$dir/plan9port"
  echo "export NINEP_INTEROP_DIOD_IMAGE=$DIOD_IMAGE"
  echo "export NINEP_INTEROP_LIMA_VM=$LIMA_VM"
}

if $env_only; then print_env; exit 0; fi
mkdir -p "$dir"

# hugelgupf/p9 p9ufs: a 9P2000.L server in Go.
if [ ! -x "$dir/gobin/p9ufs" ]; then
  GOBIN="$dir/gobin" go install "github.com/hugelgupf/p9/cmd/p9ufs@$P9UFS_VERSION"
fi

# plan9port: the 9p command is a 9P2000 client.
if [ ! -x "$dir/plan9port/bin/9p" ]; then
  rm -rf "$dir/plan9port"
  git clone -q https://github.com/9fans/plan9port.git "$dir/plan9port"
  (cd "$dir/plan9port" && git checkout -q "$PLAN9PORT_COMMIT" && ./INSTALL -b > "$dir/plan9port-build.log" 2>&1)
fi

# diod: the 9P2000.L server Debian packages, in a container.
if ! docker image inspect "$DIOD_IMAGE" > /dev/null 2>&1; then
  docker build -q -t "$DIOD_IMAGE" -f "$here/diod.Dockerfile" "$here" > /dev/null
fi

# Linux v9fs: a Debian VM under lima, booted on the full kernel so that the 9p module exists.
if command -v limactl > /dev/null 2>&1; then
  if ! limactl list --format '{{.Name}}' 2> /dev/null | grep -qx "$LIMA_VM"; then
    limactl start --name="$LIMA_VM" --tty=false template://debian-12 > /dev/null
    limactl shell "$LIMA_VM" -- sudo sh -c '
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq && apt-get install -y -qq linux-image-arm64 linux-image-amd64 > /dev/null 2>&1 || true
      apt-get remove -y -qq --purge "linux-image-*-cloud-*" linux-image-cloud-arm64 linux-image-cloud-amd64 > /dev/null 2>&1 || true
      update-grub > /dev/null 2>&1 || true'
    limactl stop "$LIMA_VM" > /dev/null && limactl start "$LIMA_VM" --tty=false > /dev/null
  fi
  limactl shell "$LIMA_VM" -- sh -c 'sudo modprobe 9p && sudo modprobe 9pnet_fd && grep -q 9p /proc/filesystems' \
    || echo "warning: the $LIMA_VM kernel has no 9p support; the v9fs tests will skip" >&2
else
  echo "note: limactl not found; the Linux v9fs tests will skip (brew install lima)" >&2
fi

print_env
