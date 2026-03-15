# Owner Preferences

## Owner
Hussein Abdurahmani — SAP developer, Tanzanian context (TZS currency, Tanzania regions).

## Workflow
- Works in Visual Studio on Windows (`C:\Users\hussein.abdurahmani\source\repos\...`)
- Uses Postman / API testing tools to verify endpoints
- Shares raw log output and screenshots to report bugs
- Expects fixes to be committed and pushed to the Claude branch immediately

## Code Style
- Emoji prefixes in log messages and comments are welcome (`✅`, `❌`, `📦`, `🆕`, etc.)
- XML doc comments (`/// <inheritdoc />`) on migration methods
- `#region` blocks to group related methods in service files
- Inline comments explaining the "why" not just the "what"
- `string.Empty` preferred over `""` in C# code
- `?? string.Empty` null-coalescing pattern for SAP field reads

## Migration Conventions
- Migration class names are PascalCase descriptive nouns: `AddCancellationStatusDefault`
- Migration timestamps: `yyyyMMddHHmmss` format; manual migrations use date-based IDs
  like `20260311000003`
- Raw SQL migrations (using `migrationBuilder.Sql()`) are acceptable and preferred
  over complex `AlterColumn` for SQLite

## Git
- Branch: always `claude/explore-project-w2myQ` (or as instructed)
- Never push to `master`
- Commit messages: imperative mood, multi-line body explaining root cause + changes
- Always append `https://claude.ai/code/session_01Rono8n5bxD15p5LDtpY5iX` to commit body

## What to Avoid
- Do NOT use `EnsureCreated()` — always `Migrate()`
- Do NOT suppress `PendingModelChangesWarning` — always fix the root cause
- Do NOT add `[Required]` annotations when `string?` solves the problem more cleanly
- Do NOT batch multiple schema changes into one migration if they can be separated
