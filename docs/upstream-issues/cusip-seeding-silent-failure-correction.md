**Correction / update, in the interest of accuracy: FTD CUSIP seeding is not
permanently broken — on a later cycle it worked.**

Same instance, shortly after the worker was last restarted: `EquitySecurity.Cusip`
went 0 → 36, `StockCusipChanged` fired, the holdings rescan cleared
`ProcessedDataSet` (26 → 2), and `InstitutionalHolding` went 0 → 110 with
`InstitutionalFiling` 24 and climbing while the bulk path began re-importing the
historical data sets it had previously been skipping. So the designed self-heal
chain works end to end, including the FTD → `SetCusip` → rescan → backfill leg.

That makes the accurate shape of the bug narrower than my original report
implies — and in a way that makes the observability point *sharper*, not weaker:

* The seeding is not broken. It **silently no-ops on some cycles and succeeds on
  others.** We still cannot say which guard fired on the failed cycles, because
  none of them logs — the observable outcome is a week of missing holdings
  followed by a silent recovery, with no signal in either direction.
* The most likely mechanism is the monthly-window rule. `ReplayLiveIdentity`
  replays only `asOf - 1` month and aborts unless *every* file in that month
  downloaded (`if (!replayFiles.All(replayRecords.ContainsKey)) return 0;`), so a
  half-file that lags publication zeroes the whole cycle with no log. Once both
  halves were present, seeding happened immediately.
* Worth knowing for anyone checking this: the counts move on
  **`EquitySecurity.Cusip`**, not `CommonStock.Cusip`. The latter stays 0 under
  the new identity model, so the obvious column to check reports zero forever and
  makes a working system look broken (I spent a while misreading it that way
  myself).

What I would still ask for is unchanged, and is now the entirety of the report:
log the no-op paths, and surface "N consecutive cycles seeded 0 CUSIPs" as a real
diagnostic. A self-healing silent failure that costs a week of holdings is still
worth fixing — arguably more so, because it means the system can be wrong for days
and then quietly become right, leaving no trace of the interval either way.
