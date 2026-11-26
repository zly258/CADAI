# Drawing Inspection & Spatial Queries

## 1. Initial Discovery & Statistics

- Active drawing & layout overview: `cad_doc_active_info`, `cad_doc_list`, `cad_layout_list`.
- Entity counts & layer breakdown: `cad_drawing_statistics`, `cad_count_entities_by_type`, `cad_layer_list`.
- Pre-edit health audit (empty/off-layer/broken texts, overlapping items): `cad_drawing_health_check`.

## 2. Targeted Spatial Queries (Keep Context Compact)

- On large drawings, avoid unbounded scans! Use `cad_query_entities` or `cad_text_search` with **2D bounding box filters** (`min_x`, `min_y`, `max_x`, `max_y`) plus `type`/`layer` and a small `limit`.
- Read full entity geometry/linetype/text/attributes for specific handles using `cad_get_entity_info`.
- Use `cad_export_drawing_json` when full structured sheet geometry export is needed (e.g., before feeding into AutoCAD `cad_model_execute_recipe` / `cad_plan_from_drawing_facts`).

## 3. Selection, Tables & Visual Check

- Inspect or filter user selection via `cad_selection_get`, `cad_selection_filter`, `cad_selection_select_by_filter`, `cad_selection_window`, `cad_selection_set`.
- Read native tables (`cad_table_list`, `cad_table_read`, `cad_table_write`) or exploded line+text tables (`cad_virtual_table_detect`).
- Zoom/navigate (`cad_view_zoom_extents`, `cad_view_zoom_window`, `cad_view_zoom_entity`); render PNG via `cad_view_capture_screen` ONLY on visual review requests.
