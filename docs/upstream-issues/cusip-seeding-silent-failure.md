# Upstream issue draft — silent zero-CUSIP seeding

Target: `daniel3303/Equibles` (we run a self-hosted fork).

## Title

**13F ingest can sit at zero holdings forever with an empty error log — every
zero-result path in FTD CUSIP seeding is silent**

## Body

### Summary

On a cold install (`InstitutionalHolding` empty, no `CommonStock.Cusip` seeded),
the FTD scraper is the only thing that can seed CUSIPs, and the 13F pipeline
maps filed CUSIPs through them. When that seeding produces nothing, **nothing is
logged and nothing is recorded in `Errors`** — the install simply reports zero
institutional holdings indefinitely.

Concretely: `FtdScraperWorker` runs on schedule, FTD data imports fine
(1.1M rows), the realtime 13F sweep discovers and parses filings correctly, and
every single one is dropped with an `INF`-level line at most. From the operator's
side the system looks healthy; only the data is missing.

### Impact

Hard to overstate for a fresh deployment: the 13F feature is completely dead and
completely silent. Our instance sat like this for a week — 30.4M `FinancialFact`
rows, 12.1M prices, 628k chunks, 0 holdings, and an `Errors` table with no
Holdings- or FTD-sourced rows at all.

### What we observed (live, Information level)

Per filing, for every filing in the sweep:

```
Found 7164 unique CUSIPs in INFOTABLE
Mapped 0 CUSIPs to tracked stocks (0 retired aliases, 0 secondary listings, out of 7164 in data set)
No tracked stocks mapped for this data set (CUSIPs may not be seeded yet) — will retry on a later cycle
```

Database state alongside it:

| Table | Rows |
| --- | --- |
| `InstitutionalHolding` / `InstitutionalFiling` / `ProcessedFiling` / `HoldingManagerEntry` | **0** |
| `CommonStock.Cusip` non-empty | **0 of 7842** |
| `EquitySecurity.Cusip` non-empty | **0 of 10370** |
| `CommonStockListedCusip` / `CommonStockCusipAlias` / `EquityIssuerCusipAlias` / `EquityListingCusipEvidence` | **0** |
| `UnmappedCusip` | **0** |
| `FailToDeliver` | 1,138,983 (2,609 distinct tickers, 0 blank tickers, all stock-mapped, through 2026-08-14) |
| `RealtimeSweepState` (`Holdings13FRealtime`) | `SweptThrough` = today |

So the FTD import itself is healthy and the sweep runs to completion — the
CUSIP seeding is the only thing producing nothing.

### Ruled out

* **SEC source data is fine.** `https://www.sec.gov/files/data/fails-deliver-data/cnsfails202608b.zip`
  returns 200 and its header is `SETTLEMENT DATE|CUSIP|SYMBOL|QUANTITY (FAILS)|DESCRIPTION|PRICE`
  — 61,552 rows, only 2 with a blank CUSIP.
* **`Sec:ContactEmail` is valid** (it gates `ValidateConfiguration()`).
* **A 404 on an unpublished quarterly data set is fine** — that path is
  deliberate and documented.

### The actual defect: zero-result paths return silently

In `FtdImportService`, a cycle that seeds nothing is indistinguishable from a
cycle that was never needed. Five places can return "seeded nothing":

| Location | Guard | Logged? |
| --- | --- | --- |
| `ReplayLiveIdentity` | `if (replayFiles.Count == 0) return 0;` | no |
| `ReplayLiveIdentity` | `if (!replayFiles.All(replayRecords.ContainsKey)) return 0;` | no |
| `ReplayLiveIdentity` | `if (liveIdentityEvidence.Count == 0) return 0;` | no |
| `SeedCusipsWithSecondaryMap` | `if (tickerToCusip.Count == 0) return 0;` | no |
| `Import` (caller) | `if (cusipsSeeded > 0) _logger.LogInformation("Seeded or updated {Count} CUSIPs from FTD data", …)` | **only on success** |

So the success path is logged and every failure path is not — the exact inverse
of what an operator needs. Note that seeding only replays **one month**
(`LiveRecheckMonths = 1`, `asOf - 1`), and `ReplayLiveIdentity` aborts entirely
unless *every* file in that month downloaded, so a single lagging half-file
silently zeroes a whole cycle.

The same shape exists on the holdings side. `Realtime13FIngestionService`:

* `ImportEntry` returns `EntryImportOutcome.Incomplete` when
  `!importResult.IsComplete` (the documented `NoTrackedStocks` case), and the
  caller does `continue` **without recording the accession**. The filing is
  retried forever and never surfaced.
* `HoldingsImportService` does log `Mapped {Count} CUSIPs to tracked stocks …`,
  but at `Information`, and the default `MinimumLogLevel` is `Warning`.

One more consequence worth noting: `UnmappedCusip` is **empty** while 100% of
CUSIPs are unmapped, so `RecordUnmappedCusip` appears to be unreachable in the
`NoTrackedStocks` case — the one table designed to make this visible stays
empty.

### Could the bulk path have recovered it?

No: `HoldingsScraperWorker.BackfillProcessedDataSets` seeds every data set
except the newest as already-processed on first run (26 rows with
`SubmissionCount = 0`), so the only file ever fetched is the newest one — and an
unpublished one 404s by design. That is a sensible cold-start optimisation, but
it means the *only* path that can populate a fresh install is the realtime
sweep, which is the silent one.

### Suggested fix

1. Make every zero-result path emit a reason (Warning) with its own counter —
   e.g. `replayFiles=0`, `replayIncomplete`, `noIdentityEvidence`,
   `tickerToCusipEmpty`.
2. Track consecutive cycles that seeded 0 CUSIPs and, after N cycles, surface a
   real diagnostic (an `Errors` row and/or a Status-page indicator) instead of
   Information chatter. "0 CUSIPs on a database with 0 CUSIPs" is a loud state,
   not a quiet one.
3. Expose "13F filings discovered but mapped 0 CUSIPs" as a first-class metric,
   so a 100%-unmapped state is visible without turning on `Information`.
4. Consider recording a durable unmapped marker in the `NoTrackedStocks` case so
   `UnmappedCusip` (or an equivalent) is populated even when nothing maps.
5. Either keep `MINIMUM_LOG_LEVEL` at `Warning` and make these events `Warning`,
   or document that a fresh install needs `Information` to diagnose 13F.

### What we could not determine — and why that is the point

With `MinimumLogLevel=Information` we confirmed the symptom precisely, but we
still cannot tell *which* of the five guards returned zero, because four of them
log nothing at all and the fifth logs only on success. That is exactly the
observability gap in this report: **the failure mode cannot be diagnosed from
the running system's own output**, which is why it is worth fixing independently
of whichever guard happened to fire here.

### Environment

* Self-hosted Docker Compose deployment (Coolify), Postgres via `paradedb/paradedb`.
* `SEC_CONTACT_EMAIL` set, `Worker__MinSyncDate` unset, embeddings disabled.
* Worker memory-limited to 1 GiB on a 4 GiB host; the instance had been running
  about a week.
