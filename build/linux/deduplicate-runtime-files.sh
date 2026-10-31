#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 || ! -d "$1" ]]; then
  echo "Usage: $0 <payload-directory>"
  exit 1
fi

root="$1"
before_kb="$(du -sk "$root" | awk '{print $1}')"
previous_hash=""
canonical=""
linked=0
candidate_list="$(mktemp)"
trap 'rm -f "$candidate_list"' EXIT

# Keep every probing path while sharing the inode for byte-identical, runtime-loadable files.
# The same dependency closure is copied into the application base and into each out-of-process
# child host, so the payload holds several copies of most assemblies and native libraries; a tar
# records a hardlink as a name-only entry, and the archive then only pays for one copy. This is the
# Linux counterpart of build/macos/deduplicate-runtime-files.sh (GNU stat/sha256sum instead of the
# BSD ones). Satellite resource assemblies are included regardless of size because locale folders
# repeat thousands of otherwise-small files across child hosts.
find "$root" -type f \( \( -name '*.dll' -o -name '*.so*' -o -name '*.json' \) -size +512k -o -name '*.resources.dll' \) -print0 > "$candidate_list"
if [[ -s "$candidate_list" ]]; then
  while read -r hash path; do
    if [[ "$hash" != "$previous_hash" ]]; then
      previous_hash="$hash"
      canonical="$path"
      continue
    fi
    if [[ "$(stat -c '%a' "$canonical")" != "$(stat -c '%a' "$path")" ]] || ! cmp -s "$canonical" "$path"; then
      continue
    fi
    rm -f "$path"
    ln "$canonical" "$path"
    linked=$((linked + 1))
  done < <(xargs -0 sha256sum < "$candidate_list" | LC_ALL=C sort)
fi

after_kb="$(du -sk "$root" | awk '{print $1}')"
printf 'Deduplicated %d runtime files: %.1f MiB -> %.1f MiB\n' "$linked" \
  "$((before_kb / 1024))" "$((after_kb / 1024))"
