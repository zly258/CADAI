# AI Assistant Configuration & Operation

- LLM endpoint, API key, temperature, and context window settings live in the AutoCAD Ribbon's **设置** window.
- Modal dialog protection (`ModalAgent`) runs externally when enabled in settings to clear blocking prompts safely.
- **Standalone AutoCAD Translate** (`WISTRANSS`, `WISBATCH`, `WISTRANST`) is a dedicated Ribbon feature for drawing translation; do not try to run drawing-wide translation via chat MCP tools.
- For troubleshooting slow or oversized context on large DWGs: scope queries using `min_x/min_y/max_x/max_y` on `cad_query_entities` / `cad_text_search`, group multi-step edits with `cad_batch_execute`, and run `cad_drawing_health_check` first if annotations appear missing.
