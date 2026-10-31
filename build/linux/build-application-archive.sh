#!/usr/bin/env bash
# Build the Linux distribution archive: a tar stream piped into xz.
#
# The payload carries the same dependency closure at the application root and inside every
# out-of-process child host, so a plain deflate archive stores each copy in full (measured: 1360 MB
# of payload, 504 MB of unique content, 498 MB of ZIP - 63% of the bytes were duplicates).
# build/linux/deduplicate-runtime-files.sh has already collapsed those copies into one inode, and
# tar records each repeat as a name-only hardlink entry, so a duplicate costs a few bytes. xz -9e
# (LZMA2) then compresses what is left: on 56 MB of this payload's assemblies it produced 26% less
# than `zip -9`.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <payload-directory> <output.tar.xz>" >&2
  exit 1
fi

payload="$1"
output="$2"

[[ -d "$payload" ]] || { echo "payload directory is missing: $payload" >&2; exit 1; }

# --sort=name keeps the archive byte-for-byte reproducible and puts the surviving copies of one
# file next to each other; --owner/--group/--numeric-owner drop the building user's ids so what a
# user extracts does not depend on the runner.
tar --create --file - --sort=name --owner=0 --group=0 --numeric-owner -- "$payload" |
  xz --compress -9e -T0 > "$output"
