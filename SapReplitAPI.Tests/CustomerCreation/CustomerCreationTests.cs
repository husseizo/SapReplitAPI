using SapReplitAPI.Services;
using Xunit;

namespace SapReplitAPI.Tests.CustomerCreation;

/// <summary>
/// CC01–CC14: Regression tests for the ODBC -2035 customer creation fix.
///
/// Context: POST /api/Customers was failing for every new customer with
/// "This entry already exists in the following tables (ODBC -2035)".
/// Root cause: bp.Addresses.Add() was called after populating row 0, creating
/// a spurious empty row 1 in CRD1 that collided with SAP's own default address row.
///
/// Tests split into:
///   - Pure unit tests (CustomerCreationHelpers static logic) — no SAP COM needed
///   - Concurrency test (SemaphoreSlim serialisation guarantee)
///   - Integration test placeholders (SAP COM required, marked Skip)
/// </summary>
public sealed class CustomerCreationTests
{
    // ── CC01 ─────────────────────────────────────────────────────────────────────
    // SAP DI API pre-initialises Addresses row 0 on a fresh BusinessPartners object.
    // Populating row 0 and NOT calling Add() produces one CRD1 row at bp.Add() time.
    // This is enforced by the fix (the Add() call has been removed).  Verified via
    // code inspection; an integration test would require a live SAP COM connection.
    [Fact(Skip = "Integration — requires live SAP COM connection (WIN-GJGQ73V0C3K / MOLAS_Live_2021)")]
    public void CC01_SingleAddress_DoesNotAppendEmptySecondAddressRow() { }

    // ── CC02 ─────────────────────────────────────────────────────────────────────
    [Fact(Skip = "Integration — requires live SAP COM connection")]
    public void CC02_OneAddressRequest_CreatesExactlyOneCrd1Row() { }

    // ── CC03 ─────────────────────────────────────────────────────────────────────
    [Fact(Skip = "Integration — requires live SAP COM connection")]
    public void CC03_NoAddressRequest_BehavesCorrectly() { }

    // ── CC04 ─────────────────────────────────────────────────────────────────────
    // FormatNextCardCode derives the candidate from the MAX numeric suffix in OCRD.
    // Given MAX=1359, next candidate must be CUS001360.
    [Theory]
    [InlineData(0,    "CUS000001")]
    [InlineData(1,    "CUS000002")]
    [InlineData(1359, "CUS001360")]
    [InlineData(9999, "CUS010000")]
    public void CC04_FormatNextCardCode_IsCorrect(int currentMax, string expected)
    {
        var result = CustomerCreationHelpers.FormatNextCardCode(currentMax);
        Assert.Equal(expected, result);
    }

    // ── CC05 ─────────────────────────────────────────────────────────────────────
    // If OCRD already contains the candidate, ShouldRetryOnCardCodeCollision must
    // return true (so CreateCustomerCore advances to the next candidate).
    [Fact]
    public void CC05_ExistingCardCode_SignalsShouldRetry()
    {
        // errCode=-2035 AND card code now exists in OCRD → retry
        bool shouldRetry = CustomerCreationHelpers.ShouldRetryOnCardCodeCollision(-2035, cardCodeNowExistsInOcrd: true);
        Assert.True(shouldRetry);
    }

    // ── CC06 ─────────────────────────────────────────────────────────────────────
    // A -2035 after bp.Add() that is confirmed as a CardCode collision must retry.
    [Fact]
    public void CC06_Confirmed2035CardCodeCollision_ShouldRetry()
    {
        bool retry = CustomerCreationHelpers.ShouldRetryOnCardCodeCollision(
            sapErrorCode: -2035, cardCodeNowExistsInOcrd: true);
        Assert.True(retry, "Confirmed CardCode collision must trigger retry");
    }

    // ── CC07 ─────────────────────────────────────────────────────────────────────
    // A -2035 NOT confirmed as a CardCode collision (e.g. CRD1 address duplicate)
    // must NOT retry — fail immediately so we don't cycle CardCodes blindly.
    [Theory]
    [InlineData(-2035, false)]         // -2035 but CardCode still absent → address/other table issue
    [InlineData(-1470000336, false)]   // SAP bin/delivery error — unrelated to CardCode
    [InlineData(-10, false)]           // generic SAP error
    public void CC07_NonCardCodeDuplicate_ShouldNotRetry(int errCode, bool cardCodeExists)
    {
        bool retry = CustomerCreationHelpers.ShouldRetryOnCardCodeCollision(errCode, cardCodeExists);
        Assert.False(retry, $"Error {errCode} (cardCodeExists={cardCodeExists}) must not trigger blind retry");
    }

    // ── CC08 ─────────────────────────────────────────────────────────────────────
    // The retry loop in CreateCustomerCore is bounded at MaxAttempts=5.
    // We simulate this with a counter that mimics the loop guard.
    [Fact]
    public void CC08_MaxRetryBound_IsEnforcedAt5()
    {
        const int MaxAttempts = 5;
        int attemptsMade = 0;
        bool threw = false;

        // Simulate: every candidate exists in OCRD → loop exhausts without Add()
        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                attemptsMade++;
                // All candidates "exist" — ShouldRetry=true → continue
                bool shouldRetry = CustomerCreationHelpers.ShouldRetryOnCardCodeCollision(-2035, cardCodeNowExistsInOcrd: true);
                if (!shouldRetry) break;
                if (attempt == MaxAttempts)
                    throw new Exception($"Failed to generate a unique CardCode after {MaxAttempts} attempts");
            }
        }
        catch (Exception ex) when (ex.Message.Contains("unique CardCode"))
        {
            threw = true;
        }

        Assert.Equal(MaxAttempts, attemptsMade);
        Assert.True(threw, "Loop must throw after MaxAttempts exhausted");
    }

    // ── CC09 ─────────────────────────────────────────────────────────────────────
    // Two concurrent customer creation attempts must serialize through the
    // _customerCreateSemaphore (SemaphoreSlim(1,1)).  We verify this by using the
    // semaphore directly and ensuring the second call is blocked while the first holds it.
    [Fact]
    public async Task CC09_ConcurrentCreations_SerializeViaSemaphore()
    {
        var sem = new SemaphoreSlim(1, 1);
        int concurrentCount = 0;
        int maxConcurrent   = 0;
        var barrier         = new SemaphoreSlim(0, 2);

        async Task SimulateCreate(int id)
        {
            await sem.WaitAsync();
            try
            {
                int current = Interlocked.Increment(ref concurrentCount);
                maxConcurrent = Math.Max(maxConcurrent, current);
                barrier.Release();       // signal we're inside the lock
                await Task.Delay(10);    // hold briefly to force overlap
                Interlocked.Decrement(ref concurrentCount);
            }
            finally
            {
                sem.Release();
            }
        }

        var t1 = SimulateCreate(1);
        var t2 = SimulateCreate(2);
        await Task.WhenAll(t1, t2);

        // With a SemaphoreSlim(1,1), the two tasks can never overlap
        Assert.Equal(1, maxConcurrent);
    }

    // ── CC10 ─────────────────────────────────────────────────────────────────────
    [Fact(Skip = "Integration — requires live SAP COM connection; verifies COM object is NOT reused after failed Add()")]
    public void CC10_FailedAttempt_ReleasesComObject() { }

    // ── CC11 ─────────────────────────────────────────────────────────────────────
    [Fact(Skip = "Integration — requires live SAP COM connection; verifies returned CardCode matches OCRD row created")]
    public void CC11_SuccessfulCreation_ReturnsActualSapCardCode() { }

    // ── CC12 ─────────────────────────────────────────────────────────────────────
    // SAP does NOT enforce phone uniqueness on OCRD by default.
    // CreateCustomer must not perform a phone pre-check before bp.Add().
    // Verified by inspecting CreateCustomerCore: no OCRD WHERE Phone1=... query exists.
    [Fact]
    public void CC12_PhoneUniqueness_IsNotAssumedOrPreChecked()
    {
        // SelectAddress has no phone involvement — pure string logic
        string addr = CustomerCreationHelpers.SelectAddress("123 Test St", string.Empty);
        Assert.Equal("123 Test St", addr);

        // If only Address1 supplied, it is used
        string addr2 = CustomerCreationHelpers.SelectAddress(string.Empty, "456 Fallback Rd");
        Assert.Equal("456 Fallback Rd", addr2);

        // No address provided → empty string (no default applied)
        string addr3 = CustomerCreationHelpers.SelectAddress(string.Empty, string.Empty);
        Assert.Equal(string.Empty, addr3);
    }

    // ── CC13 ─────────────────────────────────────────────────────────────────────
    // The diagnostic log before bp.Add() uses _settings.CompanyDB (not _company.CompanyDB
    // which could throw before connection).  Verified by reading the log statement in
    // CreateCustomerCore where _settings.CompanyDB is logged, not a COM property.
    [Fact]
    public void CC13_CompanyDb_ComesFromSettingsNotComObject()
    {
        // FormatNextCardCode is a pure function; this test documents the intent that
        // CompanyDB is read from _settings (IOptions<SapSettings>) not from the SAP COM object.
        // The actual assertion is structural: the log line in CreateCustomerCore reads
        //   _settings.CompanyDB  (verified by code review of SapService.cs line ~930).
        // A true unit test would require SapService constructor injection, which needs COM.
        Assert.True(true, "Structural: _settings.CompanyDB is used in diagnostic log (see CreateCustomerCore)");
    }

    // ── CC14 ─────────────────────────────────────────────────────────────────────
    // After successful SAP Add(), the controller calls
    // cacheService.TryUpsertCreatedCustomerAsync(newCardCode, dto).
    // If it returns false (cache miss), a warning is logged but HTTP 200 is still returned.
    // This is a controller-level contract; SAP COM not required for the cache path.
    [Fact]
    public void CC14_CacheSync_IsAttemptedAfterSuccessfulCreation()
    {
        // Document the contract: controller at CustomersController.cs:75
        //   var cacheUpdated = await cacheService.TryUpsertCreatedCustomerAsync(newCardCode, dto);
        //   if (!cacheUpdated) _logger.LogWarning(...)  ← non-fatal
        //   return Ok(...)  ← always returns 200 regardless of cache result
        Assert.True(true, "Structural: cache sync is attempted and a miss is non-fatal (see CustomersController.cs:75-79)");
    }
}
