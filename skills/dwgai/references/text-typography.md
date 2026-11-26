# Text Editing, Paragraph Merging & Typography Playbook

## Overview
AutoCAD drawings frequently suffer from disorganized text: broken single-line text lines, inconsistent line spacing, misalignment, and obsolete specifications.

## 1. Single-Line vs Multiline Text (DBText vs MText)
- `DBText`: Single-line text, one entity per line. Hard to format as a block, no native word wrap or line spacing factor.
- `MText`: Rich paragraph text supporting line spacing (`LineSpacingFactor`), format overrides (`\W`, `\Q`), and multiple lines (`\P`).

## 2. Paragraph Merging (`cad_text_merge_to_mtext`)
- When user asks to merge broken lines into a paragraph:
  1. Retrieve target handles (from `cad_selection_get` or `cad_text_search`).
  2. Call `cad_text_merge_to_mtext(handles: [...])`.
  3. The underlying engine guarantees:
     - Strict visual equivalence: calculates the projection along the text rotation angle.
     - Exact line spacing: sets `LineSpacingStyle = Exactly` and reverse-calculates `LineSpacingFactor`.
     - Zero wrap drift: sets `Width = 0` and joins lines strictly using `\P`.
     - Style inheritance: embeds font width factors (`\W...;`) and oblique angles (`\Q...;`).
     - Safe replacement: erases the source DBTexts and returns the new MText handle.

## 3. Alignment & Distribution (`cad_text_align` / `cad_text_distribute`)
- **Alignment (`cad_text_align`)**:
  - `mode: 'left'`: Align all entities along the left bounding edge of the first entity.
  - `mode: 'center'`: Align horizontally centered.
  - `mode: 'right'`: Align along the right edge.
  - `mode: 'baseline'`: Align along the bottom baseline (Y-axis).
- **Equal Spacing Distribution (`cad_text_distribute`)**:
  - `direction: 'vertical'`: Uniformly spaces technical notes, table rows, or leader notes.
  - Set `spacing: 0` for auto-interpolation between first and last entity; set `spacing: 7.0` (or target mm) for standard院标 fixed line spacing.

## 4. Batch Find & Replace (`cad_text_batch_replace`)
- For updating project dates, material codes, or equipment prefixes across the entire drawing, prefer `cad_text_batch_replace` over modifying entities one-by-one.
- Respect `match_case` for case-sensitive engineering codes.
