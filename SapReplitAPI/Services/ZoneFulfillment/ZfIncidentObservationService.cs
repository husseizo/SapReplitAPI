using System.Text.Json;
using SapReplitAPI.Models.ZoneFulfillment;

namespace SapReplitAPI.Services.ZoneFulfillment;

/// <summary>
/// The authoritative backend classifier for technical incident lifecycle — not the browser,
/// not the dashboard. Runs the live detection query, diffs the result against durable
/// history, and persists exactly one of three outcomes per incident:
///
///   NEW INCIDENT                    → insert a new ACTIVE occurrence (OccurrenceNumber = prior max + 1)
///   EXISTING ACTIVE, still detected → update LastObservedAtUtc (+ EvidenceJson) on the same row
///   PREVIOUSLY ACTIVE, now absent   → mark RECOVERED, set ClearedAtUtc
///
/// Failure safety (this is the single most important contract in this class):
///   A failed or throwing detection query is NEVER interpreted as recovery. The diff
///   (step 3 above) only runs after DetectCurrentIncidentsAsync returns successfully.
///   Since that query is one atomic SELECT, there is no partial/incomplete result to
///   guard against separately — it either returns the full, consistent current set, or
///   it throws and this method aborts before touching any row. No SAP call is made here
///   or anywhere in this class — this is diagnostic history persistence only.
///
/// Concurrency: guarded by a static, non-blocking gate (mirrors the precedent in
/// ProductPriceListSyncService) so an overlapping Quartz tick or manual trigger is
/// skipped rather than run concurrently against the same rows.
/// </summary>
public class ZfIncidentObservationService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private readonly ZfLiveIncidentDetector _detector;
    private readonly ZfDiagnosticIncidentHistoryRepository _repo;
    private readonly ILogger<ZfIncidentObservationService> _log;

    public ZfIncidentObservationService(
        ZfLiveIncidentDetector detector,
        ZfDiagnosticIncidentHistoryRepository repo,
        ILogger<ZfIncidentObservationService> log)
    {
        _detector = detector;
        _repo     = repo;
        _log      = log;
    }

    public async Task<ZfIncidentObservationResult> ObserveAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            _log.LogInformation("[ZF-OBSERVE] Skipped — another observation cycle is already running.");
            return new ZfIncidentObservationResult { Success = true, Error = "SKIPPED_ALREADY_RUNNING" };
        }
        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ZfIncidentObservationResult> RunAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        // 1. Authoritative observation. Throws on failure — caught here, and on failure we
        //    return immediately WITHOUT touching any existing row. An exception here must
        //    never reach the diff/recovery logic below (ZDH_16/17).
        List<ZfLiveIncidentObservation> detected;
        try
        {
            detected = await _detector.DetectCurrentIncidentsAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[ZF-OBSERVE] Detection query failed — no lifecycle rows touched. " +
                               "Existing ACTIVE incidents remain ACTIVE.");
            return new ZfIncidentObservationResult
            {
                Success = false, Error = ex.Message, ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        // 2. Load current ACTIVE rows once (avoids N+1 SELECTs during the diff below).
        IReadOnlyDictionary<string, ZfDiagnosticIncidentHistoryRecord> activeRows;
        try
        {
            activeRows = await _repo.GetActiveOccurrencesAsync(ct);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex, "[ZF-OBSERVE] Failed to read existing ACTIVE occurrences — aborting cycle safely.");
            return new ZfIncidentObservationResult
            {
                Success = false, Error = ex.Message, ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        var detectedKeys = new HashSet<string>(detected.Select(d => d.IncidentKey), StringComparer.Ordinal);
        int created = 0, updated = 0, recovered = 0;

        // 3. New or still-active incidents.
        foreach (var obs in detected)
        {
            var evidence = BuildEvidenceJson(obs, now);

            if (activeRows.TryGetValue(obs.IncidentKey, out var existing))
            {
                await _repo.UpdateObservedAsync(existing.Id, now, evidence, now, ct);
                updated++;
            }
            else
            {
                var occurrenceNumber = await NextOccurrenceNumberAsync(obs.IncidentKey, ct);
                await _repo.InsertNewOccurrenceAsync(new ZfDiagnosticIncidentHistoryRecord
                {
                    IncidentKey        = obs.IncidentKey,
                    OccurrenceNumber   = occurrenceNumber,
                    SoDocNum           = obs.SoDocNum,
                    SoDocEntry         = obs.SoDocEntry,
                    OrchestrationId    = obs.OrchestrationId,
                    FragmentId         = obs.FragmentId,
                    IncidentCode       = obs.IncidentCode,
                    Severity           = obs.Severity,
                    ItemCode           = obs.ItemCode,
                    ExpectedLineNum    = obs.SoLineNum,
                    ExpectedWhsCode    = obs.WhsCode,
                    ExpectedQty        = obs.SoLineQty,
                    LifecycleStatus    = ZfLifecycleStatus.Active,
                    FirstDetectedAtUtc = now,
                    LastObservedAtUtc  = now,
                    ClearedAtUtc       = null,
                    RecoveryReason     = null,
                    DetectionSource    = ZfDetectionSource.ObservationJob,
                    EvidenceJson       = evidence,
                    CreatedAtUtc       = now,
                    UpdatedAtUtc       = now,
                }, ct);
                created++;
            }
        }

        // 4. Previously-active incidents no longer detected → RECOVERED.
        //    Only reachable after the detection query above succeeded in full.
        foreach (var (key, row) in activeRows)
        {
            if (detectedKeys.Contains(key)) continue;
            await _repo.MarkRecoveredAsync(row.Id, now, ZfRecoveryReason.NotDetectedInLiveScan, now, ct);
            recovered++;
        }

        sw.Stop();
        _log.LogInformation(
            "[ZF-OBSERVE] DONE Detected={Detected} Created={Created} Updated={Updated} Recovered={Recovered} ElapsedMs={Ms:F1}",
            detected.Count, created, updated, recovered, sw.Elapsed.TotalMilliseconds);

        return new ZfIncidentObservationResult
        {
            Success   = true,
            Detected  = detected.Count,
            Created   = created,
            Updated   = updated,
            Recovered = recovered,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>
    /// Next OccurrenceNumber for a key that has no current ACTIVE row — 1 for a brand-new
    /// IncidentKey, or (max existing occurrence + 1) if it's reappearing after a prior
    /// RECOVERED occurrence (recurrence — see ZDH_05).
    /// </summary>
    private async Task<int> NextOccurrenceNumberAsync(string incidentKey, CancellationToken ct)
    {
        var history = await _repo.GetOccurrencesAsync(incidentKey, ct);
        return history.Count == 0 ? 1 : history.Max(h => h.OccurrenceNumber) + 1;
    }

    private static string BuildEvidenceJson(ZfLiveIncidentObservation obs, DateTime observedAtUtc)
    {
        var evidence = new
        {
            observedAtUtc      = observedAtUtc,
            soDocNum           = obs.SoDocNum,
            soDocEntry         = obs.SoDocEntry,
            orchestrationId    = obs.OrchestrationId,
            fragmentId         = obs.FragmentId,
            itemCode           = obs.ItemCode,
            expectedLineNum    = obs.SoLineNum,
            expectedWhsCode    = obs.WhsCode,
            expectedQty        = obs.SoLineQty,
            orchestrationState = obs.OrchestrationState,
            summary            = $"SoLineFragment {obs.FragmentId} (item {obs.ItemCode}, line {obs.SoLineNum}) " +
                                  $"has no corresponding SAP RDR1 line as of this observation.",
        };
        return JsonSerializer.Serialize(evidence);
    }
}
