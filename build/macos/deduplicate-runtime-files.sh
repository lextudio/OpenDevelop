#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 || ! -d "$1" ]]; then
  echo "Usage: $0 <bundle-Contents/MacOS-directory>" >&2
  exit 1
fi

root="$1"
before_kb="$(du -sk "$root" | awk '{print $1}')"
previous_hash=""
canonical=""
linked=0
# BSD mktemp's -t form chooses a live system temporary directory even if a caller
# inherited a stale TMPDIR from a previous shell or IDE session.
candidate_list="$(mktemp -t opendevelop-runtime-files)"
trap 'rm -f "$candidate_list"' EXIT

# Keep every probing path while sharing the inode for byte-identical, runtime-loadable files.
# Satellite resource assemblies are intentionally included regardless of size: locale folders
# repeat thousands of otherwise-small files across child hosts. Other user data and sub-512 KiB
# files remain ordinary copies; this is not a general-purpose filesystem optimizer.
find "$root" -type f \( \( -name '*.dll' -o -name '*.dylib' -o -name '*.json' \) -size +512k -o -name '*.resources.dll' \) -print0 > "$candidate_list"
if [[ -s "$candidate_list" ]]; then
  while read -r hash path; do
    if [[ "$hash" != "$previous_hash" ]]; then
      previous_hash="$hash"
      canonical="$path"
      continue
    fi
    if [[ "$(stat -f '%Lp' "$canonical")" != "$(stat -f '%Lp' "$path")" ]] || ! cmp -s "$canonical" "$path"; then
      continue
    fi
    rm -f "$path"
    ln "$canonical" "$path"
    linked=$((linked + 1))
  done < <(xargs -0 shasum -a 256 < "$candidate_list" | LC_ALL=C sort)
fi

after_kb="$(du -sk "$root" | awk '{print $1}')"
printf 'Deduplicated %d runtime files: %.1f MiB -> %.1f MiB\n' "$linked" \
  "$((before_kb / 1024))" "$((after_kb / 1024))"
