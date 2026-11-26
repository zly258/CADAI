# Batch Execution, Multi-File DWG & Analysis

## 1. Single-Drawing Transactional Batch (`cad_batch_execute`)

- Prefer `cad_batch_execute` whenever performing 3+ drawing steps (layer setup + geometry + copies/arrays + fillet/chamfer + chain/angular dimensions) in one turn.
- Arguments: `operations: [{ "tool": "cad_...", "arguments": { ... } }, ...]`, `stop_on_error: true` (default).
- Runs sequentially inside ONE active AutoCAD transaction; returns per-step `{ index, tool, ok, result }`. Never call `cad_batch_execute` recursively inside `operations`.

## 2. Multi-File Offline Batch Tools (Side Database)

Operate on directories of closed `.dwg` files cleanly:
- `cad_batch_summary` — sheet statistics across files.
- `cad_batch_read_texts` / `cad_batch_replace_texts` — read or replace text strings (`dry_run` supported).
- `cad_batch_set_layer_states` — turn layers on/off/frozen/locked across files.
- `cad_batch_export_dxf` / `cad_batch_export_pdf` — batch conversion.
- `cad_compare_drawings` — diff entities and text changes between two DWG files.

## 3. Measurement, Analysis & Office Data

- Geometry measurements: `cad_measure_area`, `cad_measure_length`, `cad_layer_isolate`, `cad_export_dxf`.
- External data: `cad_excel_list_sheets`, `cad_excel_read_sheet`, `cad_excel_write_data`, `cad_excel_export_table`, `cad_pdf_get_info`, `cad_pdf_read_text`, `cad_pdf_merge`, `cad_pdf_split`.
