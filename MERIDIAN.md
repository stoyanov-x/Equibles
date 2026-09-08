# Meridian deploy notes (fork)

This is a fork of [`daniel3303/Equibles`](https://github.com/daniel3303/Equibles)
that Meridian self-hosts on its Hetzner + Coolify box (`coolify-hel1-36.stoyanov.sh`).
It is kept in sync with upstream by `.github/workflows/sync-upstream.yml`.

## Single deploy file

Coolify runs exactly ONE compose file. `docker-compose.deploy.yml` is the
self-contained stack we deploy: upstream's `docker-compose.yml` **+**
`docker-compose.embedding.yml` (Ollama embeddings ON) **+**
`docker-compose.stealth.yml` (CloakBrowser) **+** our env-settable db port, merged
into one standalone file. Point the app's **Docker Compose Location** at
`/docker-compose.deploy.yml`.

The upstream layered files (`docker-compose.yml`, `-embedding.yml`, `-stealth.yml`)
are kept as-is for clean upstream sync; they are NOT deployed directly.

## The only change vs upstream

Upstream's `docker-compose.yml` publishes the ParadeDB/Postgres service on host
port **5432**, which collides with the Coolify-managed Postgres already bound to
`127.0.0.1:5432` on the host. We changed **one line** so the host port is
env-settable (default unchanged at 5432):

```yaml
ports:
  - "${EQUIBLES_DB_EXPOSE_PORT:-5432}:5432"
```

`web`, `mcp` and `worker` connect to `db:5432` over the internal Compose network,
so nothing else needs to change. Docker Compose interpolates `${EQUIBLES_DB_EXPOSE_PORT}`
from the deploy environment at run time.

When upstream syncs, this line is preserved by the merge. If upstream ever edits
the same `db: ports:` block, the sync workflow will conflict and surface it.

## Env vars (set these in Coolify)

| Variable | Meaning | Example |
|---|---|---|
| `EQUIBLES_DB_EXPOSE_PORT` | **Required.** Host port for the Equibles DB. Must differ from the host's 5432. | `15432` |
| `SEC_CONTACT_EMAIL` | Required by SEC upstream for fair-use access. | your email |
| `MCP_API_KEY` | Optional. Key the MCP server enforces. | random |
| `AUTH_USERNAME` / `AUTH_PASSWORD` | Optional. Basic auth for the web UI. | |
| `Fred__ApiKey` | Free FRED key (macroeconomics). | |
| `Finra__ClientId` | Free FINRA key. | |
| `MINIMUM_LOG_LEVEL` | `Warning` by default. | |

## Deploy (Coolify)

1. App → **Public repository** → `stoyanov-x/Equibles`, branch `main`, build pack
   **Docker Compose**, and set **Docker Compose Location** to
   `/docker-compose.deploy.yml`.
2. Set the env vars above (at minimum `EQUIBLES_DB_EXPOSE_PORT=15432` and
   `SEC_CONTACT_EMAIL`).
3. Do **not** use a pre/post-deployment command — Coolify execs those into a single
   container, which does not work for this multi-service Compose stack.
4. Deploy. First run builds `web`/`mcp`/`worker` from source (a few minutes).

> The stack needs one free host port for the DB that isn't `5432`. `15432` is the
> suggested default. If the host Postgres ever moves, you can set `5433`, etc.

## Embeddings + stealth

`docker-compose.deploy.yml` includes the Ollama `embedding` + `embedding-pull`
services (embeddings ON, `qwen3-embedding:0.6b`) and the `cloakbrowser` sidecar
(worker stealth fetch for bot-challenged IR sites). Ollama pulls its model on first
start (a few GB + RAM). If you ever want embeddings OFF, revert to deploying plain
`docker-compose.yml` instead.

## License / AGPL

Equibles is AGPL-3.0. Self-hosting it privately for your own use does not trigger
the network-copyleft obligation; it only applies if you *offer* the service to
others over a network. See `LICENSE`.
