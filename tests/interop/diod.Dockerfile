# diod, the 9P2000.L server Linux distributions ship, as an interop peer for InteropTests.
# Build:  docker build -t ninep-diod -f tests/interop/diod.Dockerfile tests/interop
# The test starts it with an export it populates itself; see tests/interop/README.md.
FROM debian:bookworm-slim
RUN apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends diod > /dev/null \
    && rm -rf /var/lib/apt/lists/*
