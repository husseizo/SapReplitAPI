using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SapReplitAPI.Services.PickList;

/// <summary>
/// Reads CreatedAtUtc / PickedAtUtc from dbo.PickListRecord in MolasIntegration for a given OPKL
/// AbsEntry. Returns a per-(SoDocEntry, SoLineNum) dictionary for use during cache sync.
///
/// If the MolasIntegration connection string is absent or the query fails, returns an empty
/// dictionary — callers store NULL for both timestamps, preserving historical NULL semantics.
///
/// Join key: PickListAbsEntry + SoDocEntry + SoLineNum maps 1:1 to CachedPickListLine
/// (AbsEntry, OrderEntry, OrderLine).
/// </summary>
public sealed class PickListTimestampReader
{
    private readonly string? _cs;
    private readonly ILogger<PickListTimestampReader> _log;

    public PickListTimestampReader(IConfiguration cfg, ILogger<PickListTimestampReader> log)
    {
        _cs  = cfg.GetConnectionString("MolasIntegration");
        _log = log;
    }

    /// <summary>
    /// Returns timestamps keyed by (SoDocEntry, SoLineNum) for the given PickListAbsEntry.
    /// If multiple PLR rows exist for the same (SoDocEntry, SoLineNum) pair (repick), the most
    /// recent row (highest Id) wins — same policy as ZoneFulfillmentRepository.GetPickListRecordsAsync.
    /// </summary>
    public async Task<Dictionary<(int soDocEntry, int soLineNum), (DateTime createdAtUtc, DateTime? pickedAtUtc)>>
        GetTimestampsAsync(int pickListAbsEntry, CancellationToken ct = default)
    {
        var result = new Dictionary<(int, int), (DateTime, DateTime?)>();

        if (string.IsNullOrWhiteSpace(_cs))
            return result;

        const string sql = """
            SELECT SoDocEntry, SoLineNum, CreatedAtUtc, PickedAtUtc
            FROM   dbo.PickListRecord
            WHERE  PickListAbsEntry = @absEntry
            ORDER BY Id DESC;
            """;

        try
        {
            await using var conn = new SqlConnection(_cs);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@absEntry", pickListAbsEntry);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            while (await rdr.ReadAsync(ct))
            {
                var key = (soDocEntry: rdr.GetInt32(0), soLineNum: rdr.GetInt32(1));
                if (result.ContainsKey(key)) continue; // keep first (highest Id, ORDER BY Id DESC)
                var created  = rdr.GetDateTime(2);
                var picked   = rdr.IsDBNull(3) ? (DateTime?)null : rdr.GetDateTime(3);
                result[key]  = (created, picked);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[PickListTimestampReader] Failed to read PLR timestamps for AbsEntry={AbsEntry}. Timestamps will be NULL.",
                pickListAbsEntry);
        }

        return result;
    }
}
