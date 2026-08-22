#!/usr/bin/env bash
#
# Fetches a real-world T-SQL corpus into ./corpus (gitignored).
#
# Every source is unambiguously Microsoft T-SQL, which matters: a non-T-SQL dialect would produce
# parse failures that look like maxdop bugs. GitHub reports NOASSERTION for two of these because
# their licence files carry a custom preamble; the text is MIT in both cases.
#
# Licences differ and are recorded per source in the manifest. All are fine to *read* for
# measurement, which is all this does. sp_whoisactive is GPL-3.0, so take particular care that it
# never ends up inside a build artifact — ./corpus is gitignored and nothing here is redistributed.
#
# Nothing fetched here is ever committed. When a file exposes a bug, the fix is a minimal
# hand-written reproduction in tests/fixtures/, so the repo stays clean-room.
#
# This corpus has a shape, and it is worth knowing before trusting a number from it: First
# Responder Kit and Ola Hallengren are stored procedures almost exclusively, and ScriptDom's
# suite is one file per grammar feature. Between them there is very little plain application
# SQL — which is how a file holding one view and one function once measured 2.5% while this
# corpus reported 99.2%. tests/corpus/ is the committed counterweight: one hand-written file
# per construct family, and the only broad corpus CI can depend on.
#
# Usage: tools/corpus/fetch.sh [name ...]      (default: all)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CORPUS="$ROOT/corpus"
MANIFEST="$CORPUS/MANIFEST.txt"

# name|repo|licence|why it earns its place
SOURCES=(
  "scriptdom|microsoft/SqlScriptDOM|MIT|The parser's own test suite. Grammar-exhaustive by construction, and its Baselines<NNN> directories are organised by compatibility level, which exercises --parser-version directly."
  "firstresponderkit|BrentOzarULTD/SQL-Server-First-Responder-Kit|MIT|sp_Blitz and friends. The gnarliest widely-read T-SQL there is: enormous procedures, heavy dynamic SQL, decades of accreted style."
  "olahallengren|olahallengren/sql-server-maintenance-solution|MIT|The most-deployed T-SQL scripts in existence. A few very large procedures rather than many small ones."
  "sqlserversamples|microsoft/sql-server-samples|MIT|AdventureWorks and WideWorldImporters. Idiomatic modern Microsoft-authored T-SQL. Large repo, so this one is a blobless clone."
  "whoisactive|amachanic/sp_whoisactive|GPL-3.0|One 278 KB procedure that is almost entirely dynamic SQL assembled from string fragments. The densest single file of hostile T-SQL in circulation, and the style least like anything else here."
)

mkdir -p "$CORPUS"

want() {
  [ "$#" -eq 0 ] && return 0
  for arg in "$@"; do [ "$arg" = "$1" ] && return 0; done
  return 1
}

REQUESTED=("$@")

for entry in "${SOURCES[@]}"; do
  IFS='|' read -r name repo licence why <<< "$entry"

  if [ "${#REQUESTED[@]}" -gt 0 ]; then
    match=no
    for arg in "${REQUESTED[@]}"; do [ "$arg" = "$name" ] && match=yes; done
    [ "$match" = yes ] || continue
  fi

  dir="$CORPUS/$name"
  echo "==> $name  ($repo, $licence)"

  if [ -d "$dir/.git" ]; then
    git -C "$dir" fetch --depth 1 origin HEAD --quiet
    git -C "$dir" reset --hard FETCH_HEAD --quiet
  else
    rm -rf "$dir"
    # Blobless clone keeps the big repos manageable while still giving real file contents.
    git clone --filter=blob:none --depth 1 --quiet "https://github.com/$repo.git" "$dir"
  fi

  echo "    $(find "$dir" -name '*.sql' -type f | wc -l | tr -d ' ') .sql files at $(git -C "$dir" rev-parse HEAD)"
done

# The manifest is rebuilt from what is on disk, not from what this run fetched. Writing it inside
# the loop above meant `fetch.sh firstresponderkit` truncated the file and then described one
# source, silently discarding the provenance of every other — which is the opposite of what a
# manifest is for.
: > "$MANIFEST"

for entry in "${SOURCES[@]}"; do
  IFS='|' read -r name repo licence why <<< "$entry"
  dir="$CORPUS/$name"
  [ -d "$dir/.git" ] || continue

  sha="$(git -C "$dir" rev-parse HEAD)"
  count="$(find "$dir" -name '*.sql' -type f | wc -l | tr -d ' ')"

  printf '%s\n  repo    : %s\n  licence : %s\n  commit  : %s\n  .sql    : %s files\n  why     : %s\n\n' \
    "$name" "$repo" "$licence" "$sha" "$count" "$why" >> "$MANIFEST"
done

echo
echo "Manifest written to $MANIFEST"
