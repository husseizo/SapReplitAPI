# Claude Code Instructions

## Session Start — ALWAYS do this first
Read all files in `/memory/` before doing any work:
- `memory/project.md` — architecture, stack, key files
- `memory/decisions.md` — past technical decisions and their rationale
- `memory/preferences.md` — owner's coding style and workflow preferences
- `memory/bugs.md` — known bugs, recurring issues, root causes already found
- `memory/api.md` — API contracts, field mappings, external integrations

## Session End / After Each Activity
After completing any task, update the relevant memory file(s):
- Add new decisions to `memory/decisions.md`
- Add any bugs found/fixed to `memory/bugs.md`
- Update API notes if contracts changed
- Update preferences if new patterns were established

## Ground Rules
- Never push to `master` directly — always use a `claude/` branch
- Always run on branch `claude/explore-project-w2myQ` unless told otherwise
- Migrations must have a matching `.Designer.cs` file
- Raw SQL UPSERTs must list every NOT NULL column explicitly
- SQLite does not support `ALTER TABLE ALTER COLUMN` — always rebuild table
- Read a file before editing it
