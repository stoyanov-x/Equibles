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

Meridian-only changes applied here (NOT in any upstream file):
  1. db host port -> ${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432 so it can't collide
     with the host's Postgres.
  2. `restart: unless-stopped` on every long-running service. Upstream sets no
     restart policy on db/web/mcp/worker, so a crash left the container stopped
     and Coolify's nightly docker cleanup then deleted it outright -- the whole
     scraper fleet vanished while /status still reported "7/8 workers active"
     (that page reads configuration, not container state).
  3. cloakbrowser pinned to a multi-arch digest (upstream pins a single-image
     manifest, which forces amd64 and dies on the arm64 Pi).
  4. limits raised for a dedicated 4-core host: worker mem_limit 1280m / cpus 2.5,
     db cpus 1.25. The worker's managed heap is 90 % of the cgroup limit read at
     STARTUP, so a 1024m cap pinned the GC near 921 MB and left it thrashing; and
     both worker and db were measurably throttled at their old shares. Beyond this,
     the box is IO-bound (0 % idle, 41 % iowait), so more CPU buys nothing.
     web/mcp/cloakbrowser remain unbounded - see the comment at the change, that is a
     deferred decision, not an oversight.
  5. `Command Timeout=300` appended to every connection string. The Npgsql default
     is 30 s and `/status` runs 13 unbounded COUNT(*) queries (upstream #4779).
  6. `stop_grace_period: 300s` on db. Docker's default grace is 10 s, and the
     image's StopSignal is SIGINT (fast shutdown) - which does not mean fast: it
     still has to roll back open transactions and checkpoint a dirty buffer pool.
     Measured 2026-10-20: with 28 active sessions (one transaction 35 minutes old)
     the 10 s expired mid-rollback, the postmaster was SIGKILLed, and the next
     start spent ~2.5 minutes in crash recovery replaying 1.4 GB of WAL at
     ~14 MB/s while 13,008 connections were refused. A slow clean shutdown costs
     a scheduled restart minutes; an unclean one costs an outage plus a redo.
  7. db healthcheck start_period 300s / interval 10s / timeout 10s / retries 12.
     Upstream tolerates ~55s of unhealthy, which a 2-minute crash recovery blows
     straight past - and because web/mcp/worker wait on `service_healthy`, that
     turns a recovering database into a failed deployment and a Bad Gateway.
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
    # Host publish ports are env-settable because upstream defaults collide on a
    # Coolify host: db 5432 (existing host Postgres) and web 8080 (Coolify's own
    # reverse proxy). Bake safe defaults here; override via EQUIBLES_*_EXPOSE_PORT
    # at deploy time if a given port is taken.
    for svc, (container, env, default) in {
        "db":  ("5432", "EQUIBLES_DB_EXPOSE_PORT", "15432"),
        "web": ("8080", "EQUIBLES_WEB_EXPOSE_PORT", "18080"),
        "mcp": ("8080", "EQUIBLES_MCP_EXPOSE_PORT", "8081"),
    }.items():
        if svc in services:
            services[svc]["ports"] = [f"${{{env}:-{default}}}:{container}"]

    # Meridian-only change #2: a crash must not silently retire a service.
    # On 2026-09-14 the worker OOM-killed, nothing restarted it, and Coolify's
    # 00:00 docker cleanup pruned the stopped container *and* its image, so the
    # box served a stack with no scraper at all until someone noticed the data
    # had stopped. `unless-stopped` restarts a crash without resurrecting a
    # service an operator deliberately stopped. cloakbrowser already sets it.
    for svc in ("worker", "web", "mcp", "db"):
        if svc in services:
            services[svc]["restart"] = "unless-stopped"

    # Optional: serve prebuilt GHCR images instead of building from source.
    rewrite_to_images(services)

    # Meridian-only change #3: cloakbrowser must be pinned to a MULTI-ARCH digest.
    # Upstream pins `cloakhq/cloakbrowser:0.3.31@sha256:61dee1b4...`, and that
    # digest is a single-image manifest (application/vnd.oci.image.manifest),
    # not the index. Pinning a single-image manifest overrides platform
    # selection, so EVERY host pulls amd64: on the arm64 Pi the container died
    # with `exec /entrypoint.sh: exec format error`, and because worker has
    # `depends_on: cloakbrowser: service_started`, the worker never started.
    # The index digest below is the same 0.3.31 release with both platforms.
    if "cloakbrowser" in services:
        services["cloakbrowser"]["image"] = (
            "cloakhq/cloakbrowser:0.3.31@sha256:440907001735f4e2764e614e1b40c40bc0317d34b83c0315897dea443cbe5fcc"
        )

    # Meridian-only change #4: give the worker the headroom it measurably needs.
    # Measured on 2026-10-19, ~4 h into load, from the container's own cgroup:
    #   worker   raised from 1280m to 1536m on 2026-10-30. At 1280m the managed
    #            heap (90 % of the cgroup limit, read at startup) was exhausted by
    #            the as-filed HTML backfill: 90 OutOfMemoryExceptions in 2,000 log
    #            lines, zero documents created, and every other hosted service in
    #            the process stalled behind the same heap. At 1536m the OOM storm
    #            stopped (0 in 5,000 lines) and ingestion resumed.
    #            This is a floor, not a fix: the code-level fix is bounding how many
    #            SEC responses are decoded at once (SecEdgarClient). 1280m was
    #            measured against a 1024m image cap on 2026-10-19 and was simply too
    #            close to what the backfill actually needs.
    #   cpu       the Pi has 4 cores and is dedicated to this stack. Raised to db
    #             1.25 + worker 2.5 (3.75 declared). Both were genuinely throttled
    #             before: the worker at 97-107 % of a 1.0 share, the db at 78 % of
    #             its old 0.75. After the raise (measured 2026-10-19) the worker
    #             uses 1.72 of 2.5 and its throttling fell from 5.7 to 1.8 periods/s,
    #             and the box shows 0 % idle with 41 % IOWAIT - i.e. the remaining
    #             ceiling is NOT what limits anything, the single USB spindle is.
    #             Do not raise these further expecting throughput: the cores would
    #             sit unused while the disk queue grows.
    #   db       1876 MiB peak / 2048 - deliberately NOT changed: 954 MB of that is
    #            shared_buffers and the rest is page cache, which the kernel
    #            reclaims on demand, so reaching the ceiling is not an OOM risk.
    #   web/mcp/cloakbrowser are still UNBOUNDED (measured peaks 247/46/82 MiB). That
    #            is a real hazard - a spike there can OOM-kill Postgres or the worker
    #            - but bounding them was deferred on 2026-10-19 and is NOT done here.
    #            When it is, cap each a few multiples above its peak, not at it.
    SERVICE_LIMITS = {
        "db": {"mem_limit": "2048m", "cpus": "1.25"},
        "worker": {"mem_limit": "1536m", "cpus": "2.5"},
    }
    for svc, limits in SERVICE_LIMITS.items():
        if svc in services:
            services[svc].update(limits)

    # Meridian-only change #6: give Postgres time to stop cleanly. Independent of
    # the resource limits above - it is the one setting that decides whether a
    # restart is an inconvenience or a crash-recovery outage (see the docstring).
    # Deliberately not applied to web/mcp/worker: a web request is disposable and
    # a 10 s grace is right for it; the database is not.
    if "db" in services:
        services["db"]["stop_grace_period"] = "300s"

    # Meridian-only change #7: let the db healthcheck survive a crash recovery.
    # Measured 2026-10-20: after an unclean stop the replacement container spent
    # ~2 minutes in redo plus the end-of-recovery checkpoint, and pg_isready failed
    # throughout. Upstream's start_period 30s with 5 retries (~55s of tolerance)
    # declared it unhealthy, so `depends_on: condition: service_healthy` never
    # started web/mcp/worker, Coolify marked the deployment failed, and the site
    # returned Bad Gateway. That turned a recoverable delay into a dead deploy.
    # A slower first health check costs nothing; a premature unhealthy costs an
    # outage.
    if "db" in services and isinstance(services["db"].get("healthcheck"), dict):
        hc = services["db"]["healthcheck"]
        hc["start_period"] = "300s"
        hc["interval"] = "10s"
        hc["timeout"] = "10s"
        hc["retries"] = 12

    # Meridian-only change #5: raise the Npgsql command timeout (default 30 s).
    # `/status` issues 13 unbounded COUNT(*) queries, one per table (upstream
    # daniel3303/Equibles#4779). Measured on the Pi under backup load on
    # 2026-10-19: `Document` (315k rows) 14.0 s, `FailToDeliver` (1.18M) 14.6 s -
    # roughly 10x degraded - so the 3rd or 4th count exceeds 30 s and the whole
    # page returns the error page. `ConnectionStrings__DefaultConnection` here has
    # no Command Timeout, and Npgsql has no environment variable for it, so the
    # only place to set it is the connection string itself. Applies to web/mcp/
    # worker alike, which also stops the worker's spurious
    # "Timeout during reading attempt" rows.
    # Meridian-only change #7: raise the Npgsql CONNECTION timeout (default 15 s).
    # `Timeout` also bounds the bootstrap read that loads the type catalog before
    # the first query runs. On 2026-10-30 a *cold* catalog made that fail outright:
    # `select count(*) from pg_type` took 11,540 ms for 15 pages, because
    # shared_buffers is 512 MB against a 300 GB database and heavy scans evict the
    # catalog, so every page came from a USB SMR disk at ~750 ms. A freshly started
    # web container exceeded 15 s, threw "Timeout during reading attempt" during
    # Npgsql bootstrap and crash-looped before it could read migrations - the whole
    # deploy looked like a web bug. Warming the catalog dropped the same query to
    # 67 ms and web went healthy in 15 s. 60 s means a cold catalog cannot gate
    # startup; it does not paper over a genuinely unreachable database.
    COMMAND_TIMEOUT_SUFFIX = ";Command Timeout=" + os.environ.get(
        "EQUIBLES_COMMAND_TIMEOUT", "300"
    )
    CONNECT_TIMEOUT_SUFFIX = ";Timeout=" + os.environ.get("EQUIBLES_CONNECT_TIMEOUT", "60")
    for svc in services.values():
        env = svc.get("environment") if isinstance(svc, dict) else None
        if not isinstance(env, dict):
            continue
        for key, value in list(env.items()):
            if not (key.startswith("ConnectionStrings__") and isinstance(value, str)):
                continue
            if "command timeout" not in value.lower():
                value = value.rstrip(";") + COMMAND_TIMEOUT_SUFFIX
            # "timeout=" alone also matches "command timeout=", so compare with
            # that phrase removed before deciding the connect timeout is missing.
            if "timeout=" not in value.lower().replace("command timeout=", ""):
                value = value.rstrip(";") + CONNECT_TIMEOUT_SUFFIX
            env[key] = value

    with open(OUT, "w") as fh:
        yaml.safe_dump(merged, fh, sort_keys=False, default_flow_style=False, width=1000)

    svcs = list(merged["services"].keys())
    print(f"wrote {OUT} with services: {', '.join(svcs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
