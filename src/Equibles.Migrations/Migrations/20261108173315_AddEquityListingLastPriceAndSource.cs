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
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastPriceDate",
                table: "EquityListing",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastPriceSource",
                table: "EquityListing",
                type: "text",
                nullable: true);

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

            migrationBuilder.Sql(
                """
                CREATE TRIGGER "TR_EquityDailyStockPrice_ListingLastPrice"
                AFTER INSERT ON "EquityDailyStockPrice"
                REFERENCING NEW TABLE AS new_prices
                FOR EACH STATEMENT
                EXECUTE FUNCTION equibles_sync_listing_last_price();
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
