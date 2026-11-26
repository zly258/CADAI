# Safety, Failure Recovery & Native Commands Playbook

## Overview
Protocols for transaction boundaries, undo grouping, error recovery, and the native-command escape hatch. Layout, output and delivery steps live in [layout-and-output.md](layout-and-output.md).

## 1. Transaction safety and undo grouping
- Every DWG MCP mutation executes inside three nested guarantees:
  1. the AutoCAD main STA thread, via `Dispatcher.Invoke`;
  2. a document lock (`doc.LockDocument()`), so no `eLockViolation` can occur;
  3. a database transaction that commits on success and rolls back on exception.
- Everything you change within one user turn lands in **one** undo group. A single `Ctrl+Z` in AutoCAD reverts the whole turn.
- Tell the user this whenever you make bulk changes — it is the reason they can let you try.

## 2. Reading a tool result correctly
- Every tool returns JSON with an `ok` field.
- `"ok": false` means the operation did not happen; the `error` field carries the reason. Report it. Do not retry the same call unchanged, and never describe a failed call as done.
- Some tools return `ok: true` with a partial outcome (`failed`, `aborted`, `skipped` counts). Summarise the partial result honestly.

## 3. Failure recovery
- If a Handle does not resolve, re-query with `cad_query_entities` or `cad_selection_get` — the entity may have been erased by an earlier step in the same turn.
- If a layer/style/block name is not found, list the existing ones (`cad_layer_list`, `cad_text_style_list`, `cad_block_list`) instead of retrying the same spelling.
- If a write fails on a locked layer or a read-only document, say which one and stop; do not work around it silently.

## 4. The native-command escape hatch
- When a task needs AutoCAD functionality with no dedicated tool — `FILLET`, `CHAMFER`, `HATCH`, `MATCHPROP`, `QSELECT`, `OVERKILL`, or a custom LISP routine — use `cad_execute_command`.
- Always prefix with `._` (e.g. `._FILLET`) so the command is interpreted regardless of the AutoCAD UI language.
- **The command is QUEUED, not executed.** The tool returns `queued: true, completed: false` and the command runs on the command line *after* the call returns. Verify with a fresh read-only query before reporting success — never describe a queued command as done.
- Prefer a dedicated tool when one exists: dedicated tools are transactional and report a deterministic result, whereas a queued command reports only that it was queued.
- Note that `cad_audit_purge` and `cad_export_pdf` do **not** go through the command line — they run synchronously through the .NET API and report real counts and real file paths. If `cad_export_pdf` ever returns `queued: true`, its synchronous plot failed and it fell back to the command line: confirm the PDF exists before telling the user it was produced.

## 5. Modal dialogs block everything
- A modal AutoCAD dialog stops the message pump. The assistant detects this before each tool call and **skips** the call with a message naming the dialog, rather than risking re-entrant database access.
- The modal watchdog (`ModalAgent`) normally clears whitelisted dialogs within seconds. When a call is skipped for this reason, tell the user which dialog it was and that a retry is needed once it is gone.
- Tool calls also have a timeout (5 minutes foreground, 20 minutes for background translation work). A timeout is reported as a readable error — do not silently retry the same call in a loop.

## 6. Confirmation discipline
- For destructive operations (delete, batch replace, whole-drawing translate, purge), state what you are about to do and on how many entities **before** doing it, unless the user already asked for exactly that.
- After any destructive operation, report the count actually affected and the Handles where it matters.
