**Live reproduction of the silent no-op, from the same instance.**

Ran the FTD cycle at `Information` and captured the whole replay leg. It scanned
120,776 records and returned zero, logging nothing at all about it:

```
[20:40:38 INF] FTD scraper running at: 09/15/2026 20:40:38 +00:00
[20:41:05 INF] Downloading 2 FTD identity replay files and 4 new-data files from 08/15/2026
[20:41:39 INF] FTD cnsfails202608a.zip: scanned 59585 records for identity
[20:41:40 INF] FTD cnsfails202608b.zip: scanned 61191 records for identity
        ← nothing else from the FTD cycle
```

Both replay files existed (HTTP 200, verified independently) and both parsed —
59,585 and 61,191 records. The caller logs `"Seeded or updated {Count} CUSIPs from
FTD data"` **only when `cusipsSeeded > 0`**, and no such line was emitted, so
`ReplayLiveIdentity` returned 0 for this cycle. Which of the five guards produced
that zero is, as ever, unknowable from the output — that's the whole point of the
report, and here it is happening on a healthy worker with both files present and
the tracked universe populated (7,842 `CommonStock` rows).

Two things that make this more interesting than a simple no-op:

* **CUSIPs appeared anyway.** `EquitySecurity.Cusip` climbed 36 → 58 over the same
  few minutes, so some *other* path is seeding identities — the replay is
  returning 0 while coverage grows independently. That means "seeded 0" from the
  replay is not necessarily "nothing happened", which makes the absence of the
  log line genuinely ambiguous rather than merely quiet.
* **`FailToDeliver` never moved.** The same cycle announced
  `4 new-data files from 08/15/2026`, yet `max(SettlementDate)` stayed at
  2026-08-14 and the row count stayed at 1,138,983 — no new FTD rows were
  written and no `Failed to download FTD file …` warning was logged either. So
  the new-data import also appears to be completing without importing, silently.
  That may be a second instance of the same pattern, or it may be intended;
  I can't tell from the output, which is again the point.

Environment: `MinimumLogLevel=Information` for this run (normally `Warning`, where
all of the above is invisible).
