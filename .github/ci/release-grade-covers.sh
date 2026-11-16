#!/usr/bin/env bash
# Decides whether a ci.yml run's release-grade marker (release-grade.txt, written by release-grade.ps1) may gate a release.
#   release-grade-covers.sh <marker file> <platforms the release skips, comma separated or empty>
# Exit 0 when every platform the CI run skipped (its "skipped: a,b" line) is also skipped by the release, so a CI run without macOS
# cannot vouch for a release that ships a macOS build; exit 1 otherwise. A marker without a skipped line (an older run) skipped
# nothing. Prints the CI run's skipped list on stdout.
set -euo pipefail
marker=$1
release_skipped=${2:-}
[[ -s "$marker" ]] || { echo "marker $marker is missing or empty" >&2; exit 2; }
ci_skipped=$(sed -n 's/^skipped: *//p' "$marker" | head -n1 | tr -d '\r')
echo "$ci_skipped"
for platform in ${ci_skipped//,/ }; do
  case ",$release_skipped," in *",$platform,"*) ;; *) exit 1 ;; esac
done
exit 0
