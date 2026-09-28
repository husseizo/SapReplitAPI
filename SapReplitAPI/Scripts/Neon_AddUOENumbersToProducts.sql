-- ============================================================
-- Neon_AddUOENumbersToProducts.sql
-- Target:  Neon Postgres, public."Products" (confirmed via live read-only
--          information_schema.columns inspection — 8,871 rows at time of
--          writing; NOT the same table as the unrelated public.products
--          (lowercase) or public.neon_germax_products, both confirmed
--          untouched by this codebase, both 0 rows, both left alone).
-- Purpose: Mirror SAP OITM.U_OE_Numbers into Neon, matching the same
--          verbatim, nullable, additive semantics already applied to the
--          SQLite Products table (see Migrations/20260927210618_
--          AddUOENumbersToProducts.cs).
--
-- Explicitly NOT reused: public."Products".cross_ref_oems already exists,
-- is unpopulated by any code in this repository, and is semantically
-- similar (cross-reference OEM numbers) — the user was asked and chose to
-- add a distinct U_OE_Numbers column instead, matching the SAP field name
-- exactly for traceability, rather than repurpose an existing column of
-- unconfirmed original intent.
--
-- Safety:
--   - Purely additive — ADD COLUMN IF NOT EXISTS, nullable, no default.
--   - Does not touch any existing column, row, or the other two unrelated
--     tables.
--   - Not run against production by this task — written for review/
--     approval per the explicit stop-gate. Apply only after that approval.
-- ============================================================

ALTER TABLE public."Products"
    ADD COLUMN IF NOT EXISTS "U_OE_Numbers" VARCHAR(200) NULL;

-- Verification query (read-only) — run after applying, to confirm:
--   SELECT column_name, data_type, character_maximum_length, is_nullable
--   FROM information_schema.columns
--   WHERE table_schema='public' AND table_name='Products' AND column_name='U_OE_Numbers';
