#!/usr/bin/env python3
"""Hydrate docker-compose.deploy.yml from upstream's layered compose files.

Coolify runs exactly ONE `-f` file, and the embedding/stealth files are overlays
that re-define web/mcp/worker (only layerable via multiple `-f`). So we merge them
into one self-contained file that Coolify can deploy.

Unlike `docker compose config` (which resolves every ${VAR} and would bake secrets
to empty), this merge operates on the YAML structure and PRESERVES ${VAR:-...}
placeholders so Coolify can still inject secrets at deploy time.

Merge order (later overrides earlier):
    docker-compose.yml            base (db/web/mcp/worker)
    docker-compose.embedding.yml  embeddings ON + Ollama services
    docker-compose.stealth.yml    CloakBrowser sidecar + worker stealth fetch

Meridian-only change applied here (NOT in any upstream file): db host port ->
${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432 so it can't collide with the host's Postgres.
"""
from __future__ import annotations

import copy
import sys

import yaml

BASE = "docker-compose.yml"
EMBED = "docker-compose.embedding.yml"
STEALTH = "docker-compose.stealth.yml"
OUT = "docker-compose.deploy.yml"

DB_PORT = "${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432"


def merge(base: object, over: object) -> object:
    """Compose-style merge: dicts merge recursively, everything else is overridden."""
    if isinstance(base, dict) and isinstance(over, dict):
        out = dict(base)
        for k, v in over.items():
            out[k] = merge(base.get(k), v) if k in base else copy.deepcopy(v)
        return out
    return copy.deepcopy(over)


def load(path: str) -> dict:
    with open(path) as fh:
        return yaml.safe_load(fh) or {}


def main() -> int:
    merged: object = {}
    for path in (BASE, EMBED, STEALTH):
        merged = merge(merged, load(path))

    merged = dict(merged)

    # Drop compose extension anchors (x-*) now that `<<:` merge keys are expanded.
    for key in [k for k in list(merged.keys()) if str(k).startswith("x-")]:
        del merged[key]

    # Force the env-settable db host port (Meridian's only divergence).
    merged.setdefault("services", {})["db"]["ports"] = [DB_PORT]

    with open(OUT, "w") as fh:
        yaml.safe_dump(merged, fh, sort_keys=False, default_flow_style=False, width=1000)

    svcs = list(merged["services"].keys())
    print(f"wrote {OUT} with services: {', '.join(svcs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
