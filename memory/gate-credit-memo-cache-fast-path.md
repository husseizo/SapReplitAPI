---
name: gate-credit-memo-cache-fast-path
description: Credit Memo Event-Driven Cache Fast Path — COMPLETE 2026-09-13; ORIN 81 verified in SQLite + Neon; master at b246a64
metadata:
  type: project
---

Credit Memo event-driven SQLite + Neon cache fast path is COMPLETE and merged to master at commit `b246a64`.

**Why:** Cache freshness for credit memos must not depend on NeonSyncJob. When SAP commits an ORIN, the event pipeline must write CreditMemoHeaders/CreditMemoLines to both stores within seconds.

**How to apply:** CreditMemoEventHandler step 9 calls `GetCreditMemoSnapshotByDocEntry` → `RefreshSingleAsync` → `UpsertCreditMemoAsync`. The SQLite DB the service writes to is `F:\AutohubCaches\productcache.db` (from `C:\SAPAPI\appsettings.Production.json`), NOT the source-tree dev path.

**Production proof:** ORIN 81, DocNum=13, CUS001181, DocTotal=35000, U_AppRef=gate12-orin-28571-l0-q1-v1. Path B verified: RIN1.BaseType=234000031, BaseEntry=46, BaseLine=0. SQLite and Neon match identically.

**Key architecture note:** `EnsureCreatedAsync()` and `Migrate()` are no-ops for tables added after initial DB creation. All new tables must be created via explicit `CREATE TABLE IF NOT EXISTS` SQL in Program.cs startup block — same pattern as AccountStatements, DeliveryLines, etc.

**SQLite path disambiguation:** Service reads `C:\SAPAPI\appsettings.Production.json` → `F:\AutohubCaches\productcache.db`. Dev/test path in `C:\SAPAPI\publish\appsettings.Production.json` is unused in the running service. Always query `F:\AutohubCaches\productcache.db` to verify production SQLite state.

[[gate-picklist-event-refresh]]
