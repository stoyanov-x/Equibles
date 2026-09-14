#!/usr/bin/env bash
#
# Regenerate docker-compose.deploy.yml locally with the SAME flags the
# "Hydrate deploy compose" GitHub Actions workflow uses.
#
# WHY THIS EXISTS
# ---------------
# scripts/hydrate_deploy.py reads plain environment variables, and its built-in
# defaults deliberately do NOT match this fork's production settings:
#
#     EQUIBLES_EMBEDDINGS   defaults to 1   -> production uses 0
#     EQUIBLES_USE_GHCR     defaults to 0   -> production uses 1
#
# Running `python3 scripts/hydrate_deploy.py` bare therefore emits a deploy file
# that ADDS the embedding/Ollama services and KEEPS `build:` blocks instead of
# referencing the prebuilt ghcr.io images. Committing that would break the
# Coolify deploy, so use this wrapper instead.
#
# NOTE: putting these in .env does NOT work. .env is read by `docker compose`,
# not by hydrate_deploy.py, which only looks at the real process environment.
#
# SOURCE OF TRUTH
# ---------------
# Repository variables (Settings -> Secrets and variables -> Actions ->
# Variables), consumed by .github/workflows/refresh-deploy.yml. Keep the
# defaults below in sync with:
#
#     gh variable list -R stoyanov-x/Equibles
#
# Usage:
#     scripts/hydrate-local.sh                     # production flag set
#     EQUIBLES_EMBEDDINGS=1 scripts/hydrate-local.sh   # local override
#
# If PyYAML is not on your default interpreter, point PYTHON at one that has it:
#     PYTHON=/tmp/hydrate-venv/bin/python scripts/hydrate-local.sh
set -euo pipefail

cd "$(dirname "$0")/.."

PYTHON="${PYTHON:-python3}"

# Defaults mirror the repository variables above. `:` assignments let an inline
# environment override win (see usage).
: "${EQUIBLES_USE_GHCR:=1}"
: "${EQUIBLES_EMBEDDINGS:=0}"
: "${EQUIBLES_CLOAK:=1}"
export EQUIBLES_USE_GHCR EQUIBLES_EMBEDDINGS EQUIBLES_CLOAK

if ! "$PYTHON" -c 'import yaml' 2>/dev/null; then
  echo "error: PyYAML is required but not importable by '$PYTHON'." >&2
  echo "  pip install pyyaml" >&2
  echo "  or: PYTHON=/path/to/venv/bin/python $0" >&2
  exit 1
fi

echo "hydrating with USE_GHCR=$EQUIBLES_USE_GHCR EMBEDDINGS=$EQUIBLES_EMBEDDINGS CLOAK=$EQUIBLES_CLOAK"

"$PYTHON" scripts/hydrate_deploy.py

# Guard rail 1: with USE_GHCR=1 every source-built service must have been
# rewritten to a registry image reference. A leftover `build:` means the rewrite
# did not run and Coolify would try to compile on the small-disk host.
if [ "$EQUIBLES_USE_GHCR" = "1" ] && grep -qE '^    build:' docker-compose.deploy.yml; then
  echo "error: USE_GHCR=1 but the deploy file still contains build: blocks" >&2
  exit 1
fi

# Guard rail 2: surface what changed so a wrong flag combination is obvious
# before it gets committed.
if git diff --quiet -- docker-compose.deploy.yml; then
  echo "ok: no changes -- deploy file already matches the committed revision"
else
  echo "changed -- review before committing:"
  git --no-pager diff --stat -- docker-compose.deploy.yml
fi
