#!/usr/bin/env python3
"""Hydrate docker-compose.deploy.yml from upstream's layered compose files.

Coolify runs exactly ONE `-f` file, and the embedding/stealth files are overlays
that re-define web/mcp/worker (only layerable via multiple `-f`). So we merge them
into one self-contained file that Coolify can deploy.

Unlike `docker compose config` (which resolves every ${VAR} and would bake secrets
to empty), this merge operates on the YAML structure and PRESERVES ${VAR:-...}
placeholders so Coolify can still inject secrets at deploy time.

Merge order (later overrides earlier; overlays optional via EQUIBLES_EMBEDDINGS /
EQUIBLES_CLOAK, default on):
    docker-compose.yml            base (db/web/mcp/worker)
    docker-compose.embedding.yml  embeddings ON + Ollama services  (EQUIBLES_EMBEDDINGS)
    docker-compose.stealth.yml    CloakBrowser sidecar + stealth    (EQUIBLES_CLOAK)

Meridian-only change applied here (NOT in any upstream file): db host port ->
${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432 so it can't collide with the host's Postgres.
"""
from __future__ import annotations

import copy
import os
import sys

import yaml

BASE = "docker-compose.yml"
EMBED = "docker-compose.embedding.yml"
STEALTH = "docker-compose.stealth.yml"
OUT = "docker-compose.deploy.yml"

DB_PORT = "${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432"


def _truthy(v) -> bool:
    return v is not None and str(v).strip().lower() in {"1", "true", "yes", "on"}


# Optional overlays, each independently disableable via env/repo vars.
# Default on (upstream files are all included when vars are unset).
INCLUDE_EMBEDDINGS = _truthy(os.environ.get("EQUIBLES_EMBEDDINGS", "1"))
INCLUDE_CLOAK = _truthy(os.environ.get("EQUIBLES_CLOAK", "1"))


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


GHCR_REPO = os.environ.get("EQUIBLES_IMAGE_REPO", "ghcr.io/stoyanov-x")
IMAGE_TAG = os.environ.get("EQUIBLES_IMAGE_TAG", "main")
# service -> image name under GHCR_REPO
IMAGE_MAP = {"web": "equibles-web", "mcp": "equibles-mcp", "worker": "equibles-worker"}


def rewrite_to_images(services: dict) -> None:
    """Replace each build: service with a prebuilt GHCR image.

    Enabled only when EQUIBLES_USE_GHCR is truthy (1/true/yes). Kept off by
    default so the deploy file keeps building from source until GHCR images exist.
    """
    if os.environ.get("EQUIBLES_USE_GHCR", "0").lower() not in {"1", "true", "yes"}:
        return
    for svc, img in IMAGE_MAP.items():
        if svc in services:
            services[svc].pop("build", None)
            services[svc]["image"] = f"{GHCR_REPO}/{img}:{IMAGE_TAG}"


def main() -> int:
    files = [BASE]
    if INCLUDE_EMBEDDINGS:
        files.append(EMBED)
    if INCLUDE_CLOAK:
        files.append(STEALTH)

    merged: object = {}
    for path in files:
        merged = merge(merged, load(path))

    merged = dict(merged)

    # Drop compose extension anchors (x-*) now that `<<:` merge keys are expanded.
    for key in [k for k in list(merged.keys()) if str(k).startswith("x-")]:
        del merged[key]

    services = merged.setdefault("services", {})
    # Force the env-settable db host port (Meridian's only divergence).
    services["db"]["ports"] = [DB_PORT]

    # Optional: serve prebuilt GHCR images instead of building from source.
    rewrite_to_images(services)

    with open(OUT, "w") as fh:
        yaml.safe_dump(merged, fh, sort_keys=False, default_flow_style=False, width=1000)

    svcs = list(merged["services"].keys())
    print(f"wrote {OUT} with services: {', '.join(svcs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
