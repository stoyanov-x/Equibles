# Meridian deploy notes (fork)

This is a fork of [`daniel3303/Equibles`](https://github.com/daniel3303/Equibles)
that Meridian self-hosts on its Hetzner + Coolify box (`coolify-hel1-36.stoyanov.sh`).
It is kept in sync with upstream by `.github/workflows/sync-upstream.yml`.

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
   **Docker Compose**.
2. Set the env vars above (at minimum `EQUIBLES_DB_EXPOSE_PORT=15432` and
   `SEC_CONTACT_EMAIL`).
3. Do **not** use a pre/post-deployment command — Coolify execs those into a single
   container, which does not work for this multi-service Compose stack.
4. Deploy. First run builds `web`/`mcp`/`worker` from source (a few minutes).

> The stack needs one free host port for the DB that isn't `5432`. `15432` is the
> suggested default. If the host Postgres ever moves, you can set `5433`, etc.

## Embeddings (optional, off by default)

This compose runs without the Ollama embedding stack by default (upstream keeps it
behind `docker-compose.embedding.yml`, `Embedding__Enabled` defaults to `false`).
If/when you want semantic search over filings, run the separate embedding compose
or add those two services to the stack — note it pulls `qwen3-embedding:0.6b` on
first run.

## License / AGPL

Equibles is AGPL-3.0. Self-hosting it privately for your own use does not trigger
the network-copyleft obligation; it only applies if you *offer* the service to
others over a network. See `LICENSE`.
