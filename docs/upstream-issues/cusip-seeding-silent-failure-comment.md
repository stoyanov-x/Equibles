Two follow-up findings from the same instance, both about **recoverability** rather
than the initial failure.

**1. The realtime sweep records success even when it imported nothing.**
`RealtimeSweepState` is advanced to `today` regardless of how many filings were
imported — `ComputeNextWatermark` only holds the watermark back for a *failed*
day, never for an empty result. On our instance it reads
`SweptThrough = 2026-09-15` while `ProcessedFiling = 0`. So the watermark claims
a completed sweep that ingested nothing, which is the same blind spot as the rest
of this report: an empty harvest and a healthy one are stored identically.

**2. Once a watermark exists, the sweep can no longer reach the season it missed.**
`ComputeWindowStart` clamps to `TrailingReSweepDays = 14` whenever a watermark is
present, so the live log reads:

```
13F real-time ingestion sweeping 15 days of EDGAR daily index (from 2026-09-01)
```

`ComputeLookbackDays` — the one that measures from the latest *processed* data
set (here `01mar2026-31may2026` → 2026-05-31 → ~107 days) — is only consulted on
a cold start (`state == null`). Consequence: even after CUSIPs finally seed, the
Q2 2026 season stays invisible to this path. 1,835 13F-HRs were filed on
2026-08-14 alone (verified against `master.20260814.idx`); all are outside the
15-day window and — because `NoTrackedStocks` is treated as non-terminal — none
were recorded in `ProcessedFiling`, so nothing marks them as done either. They
are simply unreachable.

The other route is the quarterly data set, and
`01jun2026-31aug2026_form13f.zip` is still 404, so a cold install has no working
path back to the season it dropped.

**Recovery note, in case it is useful to document.** Deleting the
`Holdings13FRealtime` row from `RealtimeSweepState` makes the next cycle take the
cold-start branch and re-sweep the season. It is safe from a data standpoint in
this state — `ProcessedFiling` is empty, so no accession is skipped as
already-processed, and the import upserts — but it is a heavy sweep (~107 daily
index files plus every accession in the season), so it is something to do
deliberately rather than as a side effect. I could not find a supported entry
point for the intended alternative: `Holdings13FReconciliationService` has no
caller in the OSS repo (only `AddHoldingsReconciliation`, whose doc comment says
a Web/Backoffice host drives it from a button), so on a standalone deployment
there appears to be no way to ask it to re-feed the filings EDGAR lists but our
holdings lack. Documenting how to invoke that path would help — it looks like the
tool designed for exactly this situation.

Environment notes for both points: `MinimumLogLevel=Warning` (so all of the above
is invisible in production logs), `Worker__MinSyncDate` unset, worker memory
limited to 1 GiB.
