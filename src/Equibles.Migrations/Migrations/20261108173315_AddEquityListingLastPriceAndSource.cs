using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Equibles.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddEquityListingLastPriceAndSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both statements need ACCESS EXCLUSIVE / SHARE ROW EXCLUSIVE on tables the scraper fleet
            // reads and writes continuously, and an unbounded wait QUEUES: every reader and writer of
            // that table then sits behind the DDL. Measured on this database on 2026-11-08, a 5 s
            // lock_timeout on EquityListing expired immediately, so a plain ADD COLUMN would have
            // stalled the fleet for as long as the queue took to drain. Instead each statement gets a
            // short lock_timeout and retries a bounded number of times, which either finds a gap
            // between statements or fails the deploy loudly with nothing half-applied.
            migrationBuilder.Sql(
                """
                DO $mig$
                DECLARE
                    attempt int := 0;
                BEGIN
                    LOOP
                        BEGIN
                            SET LOCAL lock_timeout = '5s';
                            ALTER TABLE "EquityListing"
                                ADD COLUMN IF NOT EXISTS "LastPriceDate" date,
                                ADD COLUMN IF NOT EXISTS "LastPriceSource" text;
                            EXIT;
                        EXCEPTION WHEN lock_not_available THEN
                            attempt := attempt + 1;
                            IF attempt >= 24 THEN
                                RAISE EXCEPTION
                                    'could not acquire ACCESS EXCLUSIVE on EquityListing after % attempts',
                                    attempt;
                            END IF;
                            PERFORM pg_sleep(5);
                        END;
                    END LOOP;
                END
                $mig$;
                """
            );

            // Kept current by the database rather than by each writer: bars are inserted by the Yahoo
            // import, the delayed-trade writer and the backfills, and a writer that forgot to stamp
            // this would silently freeze a listing's apparent freshness. Statement-level, so a
            // 200-bar batch costs one grouped update rather than 200, via the transition table.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION equibles_sync_listing_last_price()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $fn$
                BEGIN
                    UPDATE "EquityListing" l
                       SET "LastPriceDate" = g."Date",
                           "LastPriceSource" = g."SourceTicker"
                      FROM (
                            SELECT DISTINCT ON ("EquityListingId")
                                   "EquityListingId", "Date", "SourceTicker"
                              FROM new_prices
                             ORDER BY "EquityListingId", "Date" DESC
                           ) g
                     WHERE l."Id" = g."EquityListingId"
                       AND (l."LastPriceDate" IS NULL OR l."LastPriceDate" < g."Date");
                    RETURN NULL;
                END;
                $fn$;
                """
            );

            // Same retry discipline: CREATE TRIGGER takes SHARE ROW EXCLUSIVE, which conflicts with
            // the ROW EXCLUSIVE lock every price insert holds.
            migrationBuilder.Sql(
                """
                DO $mig$
                DECLARE
                    attempt int := 0;
                BEGIN
                    LOOP
                        BEGIN
                            SET LOCAL lock_timeout = '5s';
                            DROP TRIGGER IF EXISTS "TR_EquityDailyStockPrice_ListingLastPrice"
                                ON "EquityDailyStockPrice";
                            CREATE TRIGGER "TR_EquityDailyStockPrice_ListingLastPrice"
                            AFTER INSERT ON "EquityDailyStockPrice"
                            REFERENCING NEW TABLE AS new_prices
                            FOR EACH STATEMENT
                            EXECUTE FUNCTION equibles_sync_listing_last_price();
                            EXIT;
                        EXCEPTION WHEN lock_not_available THEN
                            attempt := attempt + 1;
                            IF attempt >= 24 THEN
                                RAISE EXCEPTION
                                    'could not acquire SHARE ROW EXCLUSIVE on EquityDailyStockPrice after % attempts',
                                    attempt;
                            END IF;
                            PERFORM pg_sleep(5);
                        END;
                    END LOOP;
                END
                $mig$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_EquityDailyStockPrice_ListingLastPrice"
                    ON "EquityDailyStockPrice";
                DROP FUNCTION IF EXISTS equibles_sync_listing_last_price();
                """
            );

            migrationBuilder.DropColumn(
                name: "LastPriceDate",
                table: "EquityListing");

            migrationBuilder.DropColumn(
                name: "LastPriceSource",
                table: "EquityListing");
        }
    }
}
