---
name: dwgai
description: Operate Autodesk AutoCAD DWG drawings through the in-process DWG MCP server — inspection, layer/layout control, text typography, geometric drafting, entity transforms/arrays/fillets, dimensioning, block attributes, SHX repair, batch execution, and PDF delivery.
---

# DwgAI

Use this Skill for Autodesk AutoCAD automation through built-in `cad_*` tools. AutoCAD's database and deterministic query outputs are authoritative for drawing geometry, layers, styles, attributes, and text—never invent CAD state.

## Reference routing

Load only the specific reference needed via `cad_skill_read` (at most TWO per turn):

- Drawing inspection, health checks, entity & spatial queries (`min_x/min_y/max_x/max_y`): `references/drawing-inspection.md`
- Geometric drafting, entity modification/arrays/fillets, dimensioning & blocks: `references/geometry-annotation.md`
- Multi-step transactional batch (`cad_batch_execute`), multi-file DWG batch, area/length takeoffs: `references/batch-and-analysis.md`
- Text editing, DBText->MText merge, alignment, distribution: `references/text-typography.md`
- Layouts, plot setup, PDF export, audit/purge: `references/layout-and-output.md`
- Text styles, SHX/TrueType font audit & repair: `references/styles-and-fonts.md`
- Transactions & native command fallback: `references/safety-and-commands.md`
- Model endpoint & modal watchdog notes: `references/ai-configuration.md`

## Core workflow

1. Inspect state first (`cad_doc_active_info`, `cad_drawing_statistics`, `cad_drawing_health_check`, `cad_query_entities` with optional `min_x/min_y/max_x/max_y` bounds).
2. Prefer `cad_batch_execute` when performing 3+ create/modify/dimension steps in one turn so everything runs inside one transaction and one round-trip.
3. Verify edits with deterministic read-back (`cad_get_entity_info`, `cad_drawing_statistics`); only call `cad_view_capture_screen` when visual inspection is explicitly requested or resolving complex layout issues.
4. Persist only when requested (`cad_doc_save`, `cad_export_pdf`).

## Hard rules

- Full DWG drawing translation is handled by standalone AutoCAD Translate commands (`WISTRANSS`, `WISBATCH`, `WISTRANST`), not chat MCP tools.
- Never extrapolate missing coordinates or unverified entity handles.
- Use `cad_selection_get` / `cad_selection_filter` / `cad_selection_window` when acting on user-picked objects, and `cad_layer_list` / `cad_layer_create` before assigning unfamiliar layers.
- Call `cad_audit_purge` carefully only when database cleanup is requested; use `cad_execute_command` strictly as a last resort when no synchronous `cad_*` tool covers the task.
