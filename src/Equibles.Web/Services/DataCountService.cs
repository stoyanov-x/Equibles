using System.Data;
using Equibles.Cboe.Repositories;
using Equibles.Cftc.Repositories;
using Equibles.CommonStocks.Repositories;
using Equibles.Congress.Repositories;
using Equibles.Core.AutoWiring;
using Equibles.Data;
using Equibles.Fred.Repositories;
using Equibles.Holdings.Repositories;
using Equibles.InsiderTrading.Repositories;
using Equibles.Sec.Repositories;
using Equibles.Yahoo.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Equibles.Web.Services;

[Service]
public class DataCountService
{
    private readonly EquityIssuerRepository _commonStockRepository;
    private readonly DocumentRepository _documentRepository;
    private readonly InsiderTransactionRepository _insiderTransactionRepository;
    private readonly CongressionalTradeRepository _congressionalTradeRepository;
    private readonly InstitutionalHoldingRepository _institutionalHoldingRepository;
    private readonly FailToDeliverRepository _failToDeliverRepository;
    private readonly FredObservationRepository _fredObservationRepository;
    private readonly EquityDailyStockPriceRepository _dailyStockPriceRepository;
    private readonly CftcPositionReportRepository _cftcPositionReportRepository;
    private readonly CboePutCallRatioRepository _cboePutCallRatioRepository;
    private readonly CboeVixDailyRepository _cboeVixDailyRepository;

    public DataCountService(
        EquityIssuerRepository commonStockRepository,
        DocumentRepository documentRepository,
        InsiderTransactionRepository insiderTransactionRepository,
        CongressionalTradeRepository congressionalTradeRepository,
        InstitutionalHoldingRepository institutionalHoldingRepository,
        FailToDeliverRepository failToDeliverRepository,
        FredObservationRepository fredObservationRepository,
        EquityDailyStockPriceRepository dailyStockPriceRepository,
        CftcPositionReportRepository cftcPositionReportRepository,
        CboePutCallRatioRepository cboePutCallRatioRepository,
        CboeVixDailyRepository cboeVixDailyRepository
    )
    {
        _commonStockRepository = commonStockRepository;
        _documentRepository = documentRepository;
        _insiderTransactionRepository = insiderTransactionRepository;
        _congressionalTradeRepository = congressionalTradeRepository;
        _institutionalHoldingRepository = institutionalHoldingRepository;
        _failToDeliverRepository = failToDeliverRepository;
        _fredObservationRepository = fredObservationRepository;
        _dailyStockPriceRepository = dailyStockPriceRepository;
        _cftcPositionReportRepository = cftcPositionReportRepository;
        _cboePutCallRatioRepository = cboePutCallRatioRepository;
        _cboeVixDailyRepository = cboeVixDailyRepository;
    }

    public Task<int> GetStockCount() => CountAll(_commonStockRepository);

    public Task<int> GetDocumentCount() => CountAll(_documentRepository);

    public Task<int> GetInsiderTransactionCount() => CountAll(_insiderTransactionRepository);

    public Task<int> GetCongressionalTradeCount() => CountAll(_congressionalTradeRepository);

    public Task<int> GetInstitutionalHoldingCount() => CountAll(_institutionalHoldingRepository);

    public Task<int> GetFailToDeliverCount() => CountAll(_failToDeliverRepository);

    public Task<int> GetFredObservationCount() => CountAll(_fredObservationRepository);

    public Task<int> GetDailyStockPriceCount() => CountAll(_dailyStockPriceRepository);

    public Task<int> GetCftcPositionReportCount() => CountAll(_cftcPositionReportRepository);

    public Task<int> GetCboePutCallRatioCount() => CountAll(_cboePutCallRatioRepository);

    public Task<int> GetCboeVixDailyCount() => CountAll(_cboeVixDailyRepository);

    // ==== MERIDIAN PATCH #6 -- if this file conflicts during an upstream sync,
    // ==== keep THIS body. Rationale below; upstream issue daniel3303/Equibles#4779.
    //
    // Upstream counts rows with COUNT(*) once per table, thirteen times, on every
    // single /status request. On a real dataset two of those tables reach 20M and
    // 44M rows, and measured here on 2026-10-20 a COUNT(*) on each did not finish
    // within 240 s - so the page could never render, whatever client timeout was
    // configured. Even the cheap tables took 14 s each while the disk was busy.
    //
    // These numbers are status-page decoration; nobody acts on the last digit.
    // Reading the planner's estimate from pg_class.reltuples turns thirteen table
    // scans into one catalogue lookup that costs microseconds and is immune to
    // load - which matters most precisely when the box is unhealthy, i.e. when
    // someone opens the status page.
    private async Task<int> CountAll<T>(BaseRepository<T> repository)
        where T : class
    {
        var entityType = repository.Context.Model.FindEntityType(typeof(T));
        var table = entityType?.GetTableName();
        if (table is null)
            return await repository.GetAll().CountAsync();

        var schema = entityType!.GetSchema() ?? "public";
        var connection = repository.Context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await connection.OpenAsync();
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT reltuples::bigint FROM pg_class c "
                + "JOIN pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE c.relname = @table AND n.nspname = @schema";
            var pTable = cmd.CreateParameter();
            pTable.ParameterName = "@table";
            pTable.Value = table;
            cmd.Parameters.Add(pTable);
            var pSchema = cmd.CreateParameter();
            pSchema.ParameterName = "@schema";
            pSchema.Value = schema;
            cmd.Parameters.Add(pSchema);

            if (await cmd.ExecuteScalarAsync() is long estimate && estimate >= 0)
                return (int)Math.Min(estimate, int.MaxValue);
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }

        // reltuples < 0 means never analyzed. Report the real number then, rather
        // than a wrong zero - this is rare (autovacuum analyzes after the first
        // insert batch) so it does not reintroduce the slow path in practice.
        return await repository.GetAll().CountAsync();
    }
}
