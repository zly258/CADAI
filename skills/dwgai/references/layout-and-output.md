# Layout, Output & Delivery Playbook

## Overview
Layouts are the unit of delivery. A drawing is not "finished" until the right layout is active, its page setup is correct, and the output has been produced deliberately.

## 1. Know the layout landscape first
- Call `cad_layout_list` before any layout operation. It returns every tab (Model plus each paper-space layout) with:
  - `name`, `isModel`, `tabOrder`, `isCurrent`
  - `entityCount` — a layout with 0 entities usually means an unfinished sheet
  - `paperSize`, `plotRotation`, `plotType`, `plotCentered`, `plotPaperUnits` (paper space only)
- Use it to answer "这张图有几张图纸 / 出图对不对" without touching anything.

## 2. Switching layouts
- `cad_layout_switch(name)` moves the active tab and toggles the drawing between model space and paper space for you (TILEMODE is maintained by AutoCAD).
- After switching, re-run `cad_layout_list` if you need to confirm.
- Switching is **non-mutating** — it does not dirty the drawing.

## 3. Creating layouts
- `cad_layout_create(name)` creates an empty paper-space layout.
- `cad_layout_create(name, copy_from: "Layout1")` additionally:
  - copies the full page setup (plot device, paper size, scale, rotation, centring, print style table) via `PlotSettingsValidator`;
  - clones the template layout's entities into the new layout — **except viewports**, because a viewport's identity is bound to its own layout and copying one produces a dead hole rather than a live view.
  - The result reports `copiedEntityCount`, so you can tell the user how much was carried over.
- Typical use: duplicating a standard sheet (图框 + 标题栏) per discipline or per drawing number, then adjusting the title block attributes.
- After creating, set the title block fields with `cad_block_get_attributes` / `cad_block_set_attributes`.

## 4. Renaming layouts
- `cad_layout_rename(old_name, new_name)`.
- The `Model` tab cannot be renamed; the tool rejects it explicitly.
- The target name must not already exist.

## 5. Database hygiene before delivery
- `cad_audit_purge` runs the database audit and then purges unreferenced layers, blocks, text styles and linetypes.
- Run it after large batch deletions, and before the final save of a delivered drawing.
- It is a mutating operation — mention it in the summary.

## 6. PDF delivery
- `cad_export_pdf(output_pdf_path)` exports the current drawing/layout to PDF.
- The export runs through AutoCAD's own plot pipeline asynchronously. After calling it:
  1. `cad_doc_save` to persist the DWG;
  2. report the returned `outputPdfPath` to the user;
  3. if the user needs to confirm the result, wait for the plot to finish before claiming the file exists — the tool returns as soon as the export is queued.

## 7. Native command escape hatch
- When a task needs functionality with no dedicated tool (FILLET, CHAMFER, HATCH, MATCHPROP, QSELECT, custom LISP), use `cad_execute_command`.
- Always prefix with `._` for language-neutral interpretation, e.g. `._FILLET`.
- The command is queued on the AutoCAD command line; it runs after the current tool call returns. Do not assume its effect is visible within the same call.
