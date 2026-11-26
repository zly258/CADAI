# Text Styles, Fonts & SHX Repair Playbook

## Overview
The single most common reason an AutoCAD drawing from another site refuses to open cleanly is a **missing SHX font**. AutoCAD raises a modal "missing SHX font" dialog and blocks until a human answers it. In an automated flow that dialog is a hard deadlock — the drawing never finishes loading and every later tool call hangs behind it.

## 1. How AutoCAD references fonts
- A text style (`TextStyleTableRecord`) stores two file references:
  - `FileName` — the main font. Either an SHX shape file (`simplex.shx`, `txt.shx`) or a TrueType family name (`Arial`, `SimSun`).
  - `BigFontFileName` — the CJK "big font" used for Chinese/Japanese/Korean glyphs (`hztxt.shx`, `gbcbig.shx`, `chineset.shx`). This is the one that is almost always missing on a foreign machine.
- Plus `XScale` (width factor) and `TextSize` (fixed height; 0 = not fixed).

## 2. Audit before you touch anything
- `cad_text_font_audit` checks every SHX reference against the AutoCAD support search path (`ACADPREFIX`) and reports:
  - `problems[]` — one entry per style, with `style`, `handle`, `entityCount`, `missingMainFont`, `missingBigFont`
  - `trueTypeStyles[]` — TrueType styles are listed separately and explicitly **not** verified: a TrueType style stores a family name, and the family name often does not match the font file name (`SimSun` → `simsun.ttc`), so a file-name check would produce false positives. Say so honestly rather than guessing.
  - `supportPaths` — where the search actually happened
  - `suggestedFallbackFont` / `suggestedFallbackBigFont`
- `entityCount` tells you how much of the drawing is affected. A missing font on a style used by 0 entities is cosmetic; one used by 800 entities is the reason the drawing looks wrong.

## 3. Repair
- `cad_text_font_repair(fallback_font, fallback_big_font, dry_run)`:
  - `dry_run: true` (default) reports exactly what would change and touches nothing.
  - `dry_run: false` rewrites only the **missing** references. Fonts that already resolve are left alone.
  - If neither fallback resolves on this machine, the tool refuses and changes nothing — it will not trade one missing font for another.
- Recommended flow: audit → show the user the affected styles and entity counts → repair with `dry_run: false` → re-run the audit to prove the problems are gone.
- Defaults: `simplex.shx` for the main font, `hztxt.shx` for the big font.

## 4. Changing a style deliberately
- `cad_text_style_set(name, font_file, big_font_file, width_factor, text_size)`.
- Use it to standardise styles across a drawing set (e.g. force width factor 0.8, or switch a style to a company standard SHX).
- Passing an empty `big_font_file` clears the big font. Do that only when the style genuinely has no CJK content — clearing the big font on a Chinese drawing makes every Chinese character render as `?`.

## 5. Inspecting styles
- `cad_text_style_list` returns every style with `fontFile`, `bigFontFile`, `isShx`, `widthFactor`, `textSize`, `entityCount`, `isCurrent`, and for SHX styles the resolved `fontPath` / `bigFontPath`.
- Use `entityCount` to decide which styles are worth caring about, and `isCurrent` to know what new text will use.

## 6. Interaction with the modal watchdog
- A modal watchdog process is started with the chat window and auto-answers the common AutoCAD dialogs, including font-substitution prompts, so a batch run is not blocked.
- The watchdog is a safety net, not a licence to ignore fonts. Still audit and repair deliberately: the watchdog's answer for an unknown font is a substitution the user did not choose.
