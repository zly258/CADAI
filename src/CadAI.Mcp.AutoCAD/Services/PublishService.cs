using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 发布 / 对比 / 表格 / 外部参照服务：
    ///   · cad_layout_set_plot_config —— 活动图批量改布局页面设置（设备/纸张/样式/打印范围）
    ///   · cad_batch_export_pdf —— 多文件批量出 PDF（侧数据库，不打开文档）
    ///   · cad_compare_drawings —— 两版 DWG 实体级 diff（新增/删除/改动的文字与实体清单）
    ///   · cad_table_read / cad_table_write —— AutoCAD Table 结构化读写（材料表明细自动填）
    ///   · cad_xref_list / cad_xref_update —— 外部参照清单 / 重路径 / 重新加载 / 卸载
    /// 批量 PDF 与 diff 复用侧数据库骨架（见 BatchService 的设计注释）；
    /// 表格与 xref 需要命名空间级上下文（活动文档），走普通前台工具。
    /// </summary>
    internal static class PublishService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_layout_set_plot_config", HandleSetPlotConfig,
                CadSchema.Function("cad_layout_set_plot_config",
                    "Set the page setup of one or ALL layouts of the ACTIVE drawing: plot device, canonical media (paper size), plot style table, drawing orientation and plot area.",
                    CadSchema.Obj(
                        CadSchema.Str("layout", "Layout name to update. Empty = ALL layouts."),
                        CadSchema.Str("device", "Plot device PC3, e.g. 'DWG To PDF.pc3'. Empty = keep."),
                        CadSchema.Str("media", "Canonical media name, e.g. 'ISO_full_bleed_A3_(420_x_297_MM)'. Empty = keep."),
                        CadSchema.Str("plot_style", "Plot style table (CTB), e.g. 'monochrome.ctb'. Empty = keep."),
                        CadSchema.Str("orientation", "portrait / landscape. Empty = keep."),
                        CadSchema.Str("units", "mm / inches for the paper size. Empty = keep."),
                        CadSchema.Str("plot_area", "Plot area: layout / extents / window / display. Empty = keep."),
                        CadSchema.Num("min_x", "Window plot lower-left X (plot_area='window')."),
                        CadSchema.Num("min_y", "Window plot lower-left Y."),
                        CadSchema.Num("max_x", "Window plot upper-right X."),
                        CadSchema.Num("max_y", "Window plot upper-right Y."),
                        CadSchema.Str("plot_scale", "Plot scale: 'fit' or '1:100'. Empty = keep."),
                        CadSchema.Num("offset_x", "Plot origin X offset. Empty = keep."),
                        CadSchema.Num("offset_y", "Plot origin Y offset. Empty = keep."),
                        CadSchema.Bool("centered", "Center the plot on the paper."))));

            registry.RegisterBackground("cad_batch_export_pdf", HandleBatchExportPdf,
                CadSchema.Function("cad_batch_export_pdf",
                    "FAST MODE: plot MULTIPLE DWG files to PDF WITHOUT opening them (side databases). Each DWG is plotted per its saved layout page setups.",
                    CadSchema.Obj(
                        CadSchema.Arr("file_paths", "Absolute paths of DWG files to plot.", "string", true),
                        CadSchema.Str("output_dir", "Optional output directory. Default: same folder as each DWG."),
                        CadSchema.Str("device", "Optional plot device PC3. Default 'DWG To PDF.pc3'."),
                        CadSchema.Str("media", "Optional canonical media name override. Empty = per-layout saved media."))));

            registry.RegisterBackground("cad_compare_drawings", HandleCompareDrawings,
                CadSchema.Function("cad_compare_drawings",
                    "FAST MODE: entity-level diff of two DWG files WITHOUT opening them. Compares ModelSpace entities keyed by Handle; reports added / removed entities and changed texts (old vs new). Read-only.",
                    CadSchema.Obj(
                        CadSchema.Str("old_file", "Absolute path of the baseline DWG.", true),
                        CadSchema.Str("new_file", "Absolute path of the revised DWG.", true),
                        CadSchema.Bool("text_only", "true (default false) = only compare text entities (DBText/MText), which is the common 'what changed in the notes' case."),
                        CadSchema.Int("limit", "Max entries per category, default 100, cap 1000."))));

            registry.Register("cad_table_list", HandleTableList,
                CadSchema.Function("cad_table_list",
                    "List AutoCAD Table entities (e.g. material lists / BOM tables) in the active drawing with their position and dimensions.",
                    CadSchema.Obj()));

            registry.Register("cad_table_read", HandleTableRead,
                CadSchema.Function("cad_table_read",
                    "Read an AutoCAD Table as structured rows and columns of cell strings.",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Hex handle of the Table entity (from cad_table_list).", true))));

            registry.Register("cad_table_write", HandleTableWrite,
                CadSchema.Function("cad_table_write",
                    "Write cell strings into an AutoCAD Table (fill material-list rows etc). Only existing cells are written; row/col indexes are 0-based and (0,0) is usually the header.",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Hex handle of the Table entity.", true),
                        CadSchema.Arr("cells", "Array of {row, col, text} cells to write.", "object", true))));

            registry.Register("cad_xref_list", HandleXrefList,
                CadSchema.Function("cad_xref_list",
                    "List external references (xrefs) of the active drawing: name, path, attach method and load status.",
                    CadSchema.Obj()));

            registry.Register("cad_xref_update", HandleXrefUpdate,
                CadSchema.Function("cad_xref_update",
                    "Update an xref: repoint its found path (repairs broken references), or unload/reload/reattach it.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Xref block name (from cad_xref_list).", true),
                        CadSchema.Str("new_path", "Optional new absolute DWG path (repoints and reloads)."),
                        CadSchema.Str("action", "Optional action: reload / unload / reload_all. Default reload."))));
        }

        // ==================================================================== 页面设置

        private static McpExecutionResult HandleSetPlotConfig(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string layoutName = CadJson.GetString(args, "layout");
            string device = CadJson.GetString(args, "device");
            string media = CadJson.GetString(args, "media");
            string plotStyle = CadJson.GetString(args, "plot_style");
            string orientation = CadJson.GetString(args, "orientation");
            string units = CadJson.GetString(args, "units");
            string plotArea = CadJson.GetString(args, "plot_area");
            string plotScale = CadJson.GetString(args, "plot_scale");
            bool hasOffset = args.ContainsKey("offset_x") || args.ContainsKey("offset_y");
            bool hasCentered = args.ContainsKey("centered");

            if (device.Length == 0 && media.Length == 0 && plotStyle.Length == 0 && orientation.Length == 0
                && units.Length == 0 && plotArea.Length == 0 && plotScale.Length == 0 && !hasOffset && !hasCentered)
                return Fail("cad_layout_set_plot_config",
                    "Pass at least one of device / media / plot_style / orientation / units / plot_area / plot_scale / offset / centered.");

            var validator = PlotSettingsValidator.Current;
            var db = doc.Database;
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);

            var updated = new List<string>();
            var errors = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                var layout = (Layout)tr.GetObject((ObjectId)entry.Value, OpenMode.ForWrite);
                if (layoutName.Length > 0 && !string.Equals(layout.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (device.Length > 0)
                    {
                        ApplyPlotDevice(validator, layout, device);
                        if (media.Length > 0)
                            ApplyCanonicalMedia(validator, layout, media, device);
                    }
                    else if (media.Length > 0)
                    {
                        ApplyCanonicalMedia(validator, layout, media, null);
                    }
                    if (plotStyle.Length > 0)
                    {
                        try { validator.SetCurrentStyleSheet(layout, plotStyle); } catch { }
                    }
                    if (orientation.Length > 0)
                    {
                        try { SetOrientation(validator, layout, orientation); } catch { }
                    }
                    if (units.Length > 0)
                    {
                        try
                        {
                            validator.SetPlotPaperUnits(layout, string.Equals(units, "inches", StringComparison.OrdinalIgnoreCase)
                                ? PlotPaperUnit.Inches : PlotPaperUnit.Millimeters);
                        }
                        catch { }
                    }

                    if (plotArea.Length > 0)
                    {
                        string area = plotArea.Trim().ToLowerInvariant();
                        if (area == "extents")
                        {
                            validator.SetPlotType(layout, PlotType.Extents);
                        }
                        else if (area == "window")
                        {
                            double minX = CadJson.GetDouble(args, "min_x", double.NaN);
                            double minY = CadJson.GetDouble(args, "min_y", double.NaN);
                            double maxX = CadJson.GetDouble(args, "max_x", double.NaN);
                            double maxY = CadJson.GetDouble(args, "max_y", double.NaN);
                            if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY))
                                throw new InvalidOperationException("plot_area='window' 需要 min_x, min_y, max_x, max_y。");
                            if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
                            if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }
                            try
                            {
                                validator.SetPlotWindowArea(layout, new Extents2d(minX, minY, maxX, maxY));
                                validator.SetPlotType(layout, PlotType.Window);
                            }
                            catch
                            {
                                validator.SetPlotType(layout, PlotType.Window);
                                validator.SetPlotWindowArea(layout, new Extents2d(minX, minY, maxX, maxY));
                            }
                        }
                        else if (area == "display")
                        {
                            validator.SetPlotType(layout, PlotType.Display);
                        }
                        else
                        {
                            validator.SetPlotType(layout, PlotType.Layout);
                        }
                    }

                    if (plotScale.Length > 0)
                    {
                        if (string.Equals(plotScale.Trim(), "fit", StringComparison.OrdinalIgnoreCase))
                        {
                            validator.SetUseStandardScale(layout, true);
                            validator.SetStdScaleType(layout, StdScaleType.ScaleToFit);
                        }
                        else
                        {
                            string[] parts = plotScale.Split(new[] { ':', '=' }, 2);
                            double num, den;
                            if (parts.Length != 2
                                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out num)
                                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out den)
                                || num <= 0 || den <= 0)
                            {
                                throw new InvalidOperationException("plot_scale 需要 'fit' 或 '1:100' 形式。");
                            }
                            validator.SetUseStandardScale(layout, false);
                            validator.SetCustomPrintScale(layout, new CustomScale(num, den));
                        }
                    }

                    if (hasOffset)
                    {
                        try
                        {
                            validator.SetPlotOrigin(layout, new Point2d(
                                CadJson.GetDouble(args, "offset_x", 0.0),
                                CadJson.GetDouble(args, "offset_y", 0.0)));
                        }
                        catch { }
                    }
                    if (hasCentered)
                    {
                        try
                        {
                            if (layout.PlotType != PlotType.Layout)
                                validator.SetPlotCentered(layout, CadJson.GetBool(args, "centered", false));
                        }
                        catch { }
                    }

                    updated.Add(layout.LayoutName);
                }
                catch (Exception ex)
                {
                    errors.Add(layout.LayoutName + ": " + ex.Message);
                }

                // 指定了具体布局就只改它
                if (layoutName.Length > 0) break;
            }

            if (layoutName.Length > 0 && updated.Count == 0 && errors.Count == 0)
                return Fail("cad_layout_set_plot_config", "布局不存在: " + layoutName);

            var data = new Dictionary<string, object>
            {
                ["ok"] = errors.Count == 0,
                ["updated"] = updated,
                ["errors"] = errors
            };
            return new McpExecutionResult { ToolName = "cad_layout_set_plot_config", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static void SetOrientation(PlotSettingsValidator validator, Layout layout, string orientation)
        {
            bool landscape = string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase);
            validator.SetPlotRotation(layout, landscape ? PlotRotation.Degrees000 : PlotRotation.Degrees090);
        }

        /// <summary>
        /// 挂打印设备。设备名写错时先给出"可用设备清单"，而不是让
        /// SetPlotConfigurationName 抛一个只有 eInvalidInput 的裸错误。
        /// </summary>
        private static void ApplyPlotDevice(PlotSettingsValidator validator, Layout layout, string device)
        {
            StringCollection devices = validator.GetPlotDeviceList();
            string matched = MatchPlotDevice(devices, device);
            if (matched == null)
                throw new InvalidOperationException(
                    $"打印设备 '{device}' 不存在。可用设备（部分）：{DescribeList(devices, 12)}");

            validator.SetPlotConfigurationName(layout, matched, null);
            validator.RefreshLists(layout);
        }

        /// <summary>
        /// 纸张名对齐到设备的 canonical 名称。
        ///
        /// 为什么不能直接用用户/模型给的字符串：PC3 的 canonical 名对大小写、空格、下划线、
        /// 小数位都敏感（例如 '420_x_297_MM' 与 '420.00_x_297.00_MM' 是两个名字）。
        /// 旧实现直接 SetPlotConfigurationName(device, media)，名字对不上就抛 eInvalidInput，
        /// 而工具只回一句"未指定的错误"——2026-10-23 自测里 media 参数就是这么失败的。
        /// 现在的顺序：精确匹配 → 归一化匹配 → 按名称里的尺寸挑最接近纸张 → 报错并列出可用纸张。
        /// </summary>
        private static void ApplyCanonicalMedia(PlotSettingsValidator validator, Layout layout, string media, string device)
        {
            StringCollection names = validator.GetCanonicalMediaNameList(layout);
            string canonical = MatchMediaName(names, media);
            if (canonical != null)
            {
                validator.SetCanonicalMediaName(layout, canonical);
                return;
            }

            double width, height;
            PlotPaperUnit unit;
            if (TryParseMediaSize(media, out width, out height, out unit))
            {
                // allowNonStandardSizes 必须为 false：栅格设备（如 PublishToWeb PNG.pc3）
                // 要求它按任意像素另开纸张时会抛 eInvalidInput。
                validator.SetClosestMediaName(layout, width, height, unit, false);
                return;
            }

            throw new InvalidOperationException(
                $"纸张 '{media}' 不在设备 '{(string.IsNullOrEmpty(device) ? layout.PlotConfigurationName : device)}' 的可用列表里。"
                + $"可用纸张（部分）：{DescribeList(names, 12)}");
        }

        private static string MatchPlotDevice(StringCollection devices, string device)
        {
            if (devices == null || string.IsNullOrWhiteSpace(device)) return null;
            string wanted = device.Trim();
            foreach (string candidate in devices)
            {
                if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            string wantedFile = Path.GetFileName(wanted);
            foreach (string candidate in devices)
            {
                if (string.Equals(Path.GetFileName(candidate), wantedFile, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            return null;
        }

        internal static string MatchMediaName(StringCollection names, string media)
        {
            if (names == null || string.IsNullOrWhiteSpace(media)) return null;
            string wanted = media.Trim();
            foreach (string candidate in names)
            {
                if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase)) return candidate;
            }

            string wantedKey = MediaKey(wanted);
            foreach (string candidate in names)
            {
                if (MediaKey(candidate) == wantedKey) return candidate;
            }
            return null;
        }

        /// <summary>去掉大小写与所有非字母数字字符，'A3_(420_x_297_MM)' 与 'A3 (420.00 x 297.00 mm)' 等价。</summary>
        private static string MediaKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            }
            return builder.ToString();
        }

        /// <summary>从纸张名里取尺寸，如 'A3_(420_x_297_MM)' → 420×297 mm。</summary>
        private static bool TryParseMediaSize(string media, out double width, out double height, out PlotPaperUnit unit)
        {
            width = 0;
            height = 0;
            unit = PlotPaperUnit.Millimeters;
            if (string.IsNullOrWhiteSpace(media)) return false;

            System.Text.RegularExpressions.MatchCollection numbers =
                System.Text.RegularExpressions.Regex.Matches(media, @"\d+(?:\.\d+)?");
            if (numbers.Count < 2) return false;

            double first, second;
            if (!double.TryParse(numbers[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out first)) return false;
            if (!double.TryParse(numbers[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out second)) return false;
            if (first <= 0 || second <= 0) return false;

            if (media.IndexOf("inch", StringComparison.OrdinalIgnoreCase) >= 0
                || media.IndexOf("\"", StringComparison.Ordinal) >= 0)
                unit = PlotPaperUnit.Inches;

            width = Math.Max(first, second);
            height = Math.Min(first, second);
            return true;
        }

        private static string DescribeList(StringCollection values, int limit)
        {
            if (values == null || values.Count == 0) return "（空）";
            var list = new List<string>();
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                list.Add(value);
                if (list.Count >= limit) break;
            }
            return list.Count == 0 ? "（空）" : string.Join(", ", list);
        }

        // ==================================================================== 批量 PDF

        /// <summary>多文件出 PDF：侧数据库逐个出，不打开文档。单文件失败不中断。</summary>
        private static McpExecutionResult HandleBatchExportPdf(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            var files = GetStringList(args, "file_paths");
            if (files.Count == 0)
                return Fail("cad_batch_export_pdf", "file_paths array is required.");
            string outputDir = CadJson.GetString(args, "output_dir");
            string device = CadJson.GetString(args, "device", "DWG To PDF.pc3");

            var results = new List<object>();
            foreach (string raw in files)
            {
                string filePath = Environment.ExpandEnvironmentVariables(raw ?? string.Empty).Trim();
                var item = new Dictionary<string, object> { ["file"] = filePath };
                try
                {
                    if (!File.Exists(filePath)) { item["ok"] = false; item["error"] = "文件不存在"; results.Add(item); continue; }

                    string targetDir = outputDir.Length > 0 ? outputDir : Path.GetDirectoryName(filePath);
                    Directory.CreateDirectory(targetDir);
                    string pdfPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(filePath) + ".pdf");

                    int pages = PlotSideDatabase(filePath, pdfPath, device);
                    var info = new FileInfo(pdfPath);
                    item["ok"] = info.Exists;
                    item["pdf"] = pdfPath;
                    item["pages"] = pages;
                    if (!info.Exists) item["error"] = "出图流程结束但 PDF 未生成（布局可能没有可打印内容）";
                    else item["bytes"] = info.Length;
                }
                catch (Exception ex) { item["ok"] = false; item["error"] = ex.Message; }
                results.Add(item);
            }

            var data = new Dictionary<string, object> { ["ok"] = true, ["fileCount"] = results.Count, ["results"] = results };
            return new McpExecutionResult { ToolName = "cad_batch_export_pdf", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        /// <summary>
        /// 侧数据库出图：逐布局用 PlotEngine 打印。
        /// 侧数据库没有 Document/MediaBrowser，页面设置直接从每个 Layout 的保存值取
        /// （PlotSettingsValidator 接受独立的 PlotSettings 对象，不要求活动文档）。
        /// 返回成功打印的页数。
        /// </summary>
        private static int PlotSideDatabase(string dwgPath, string pdfPath, string device)
        {
            int pages = 0;
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(dwgPath, FileShare.Read, true, null);
                db.CloseInput(true);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    var validator = PlotSettingsValidator.Current;

                    using (var engine = PlotFactory.CreatePublishEngine())
                    {
                        // 多页合并进一份 PDF：第一页 BeginDocument(establish)，其后 EndDocument(false) 保持打开
                        bool docStarted = false;
                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            var layout = (Layout)tr.GetObject((ObjectId)entry.Value, OpenMode.ForRead);

                            using (var settings = new PlotSettings(layout.ModelType))
                            using (var plotInfo = new PlotInfo())
                            using (var pageInfo = new PlotPageInfo())
                            using (var infoValidator = new PlotInfoValidator())
                            {
                                settings.CopyFrom(layout);
                                if (device.Length > 0)
                                {
                                    string media = layout.CanonicalMediaName;
                                    if (media.Length > 0 && CommandService.Contains(validator.GetCanonicalMediaNameList(settings), media))
                                        validator.SetPlotConfigurationName(settings, device, media);
                                    else
                                    {
                                        validator.SetPlotConfigurationName(settings, device, null);
                                        validator.RefreshLists(settings);
                                        if (!layout.ModelType)
                                        {
                                            var paper = layout.PlotPaperSize;
                                            validator.SetClosestMediaName(settings, paper.X, paper.Y, layout.PlotPaperUnits, true);
                                        }
                                    }
                                }

                                validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                                validator.SetUseStandardScale(settings, true);
                                validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                                validator.SetPlotCentered(settings, true);

                                plotInfo.Layout = layout.ObjectId;
                                plotInfo.OverrideSettings = settings;
                                infoValidator.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                                infoValidator.Validate(plotInfo);

                                if (!docStarted)
                                {
                                    engine.BeginPlot(null, null);
                                    engine.BeginDocument(plotInfo, Path.GetFileName(dwgPath), null, 1, false, pdfPath);
                                    docStarted = true;
                                }
                                engine.BeginPage(pageInfo, plotInfo, true, null);
                                engine.BeginGenerateGraphics(null);
                                engine.EndGenerateGraphics(null);
                                engine.EndPage(null);
                                pages++;
                            }
                        }
                        if (docStarted)
                        {
                            engine.EndDocument(null);
                            engine.EndPlot(null);
                        }
                    }
                    tr.Commit();
                }
            }
            return pages;
        }

        // ==================================================================== 图纸对比

        /// <summary>两版 DWG 实体级 diff：按 Handle 对齐，报告新增/删除/文字改动。</summary>
        private static McpExecutionResult HandleCompareDrawings(string argsJson)
        {
            var args = CadJson.Deserialize(argsJson);
            string oldFile = Environment.ExpandEnvironmentVariables(CadJson.GetString(args, "old_file")).Trim();
            string newFile = Environment.ExpandEnvironmentVariables(CadJson.GetString(args, "new_file")).Trim();
            bool textOnly = CadJson.GetBool(args, "text_only", false);
            int limit = Math.Max(1, Math.Min(1000, CadJson.GetInt(args, "limit", 100)));

            if (!File.Exists(oldFile)) return Fail("cad_compare_drawings", "old_file 不存在: " + oldFile);
            if (!File.Exists(newFile)) return Fail("cad_compare_drawings", "new_file 不存在: " + newFile);
            if (string.Equals(Path.GetFullPath(oldFile), Path.GetFullPath(newFile), StringComparison.OrdinalIgnoreCase))
                return Fail("cad_compare_drawings", "old_file 与 new_file 是同一个文件。");

            var oldEntities = Snapshot(oldFile, textOnly);
            var newEntities = Snapshot(newFile, textOnly);

            var added = new List<object>();
            var removed = new List<object>();
            var changed = new List<object>();

            foreach (var pair in oldEntities)
            {
                EntitySnapshot oldE = pair.Value;
                if (!newEntities.TryGetValue(pair.Key, out EntitySnapshot newE))
                {
                    if (removed.Count < limit)
                        removed.Add(oldE.ToSummary("removed"));
                    continue;
                }
                // 同 Handle：比内容（文字变了、图层变了、几何变了）
                string diff = oldE.DiffAgainst(newE);
                if (diff != null && changed.Count < limit)
                {
                    var item = oldE.ToSummary("changed");
                    item["changes"] = diff;
                    item["oldText"] = oldE.Text;
                    item["newText"] = newE.Text;
                    changed.Add(item);
                }
            }
            foreach (var pair in newEntities)
            {
                if (!oldEntities.ContainsKey(pair.Key) && added.Count < limit)
                    added.Add(pair.Value.ToSummary("added"));
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["oldFile"] = oldFile,
                ["newFile"] = newFile,
                ["oldEntityCount"] = oldEntities.Count,
                ["newEntityCount"] = newEntities.Count,
                ["addedCount"] = added.Count,
                ["removedCount"] = removed.Count,
                ["changedCount"] = changed.Count,
                ["added"] = added,
                ["removed"] = removed,
                ["changed"] = changed
            };
            return new McpExecutionResult { ToolName = "cad_compare_drawings", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        /// <summary>侧数据库快照：模型空间实体的 Handle → 内容摘要。</summary>
        private static Dictionary<string, EntitySnapshot> Snapshot(string dwgPath, bool textOnly)
        {
            var map = new Dictionary<string, EntitySnapshot>();
            using (var db = new Database(false, true))
            {
                db.ReadDwgFile(dwgPath, FileShare.Read, true, null);
                db.CloseInput(true);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity ent)) continue;
                        string type = ent.GetType().Name;
                        if (textOnly && !(ent is DBText) && !(ent is MText)) continue;
                        map[ent.Handle.ToString()] = EntitySnapshot.Capture(ent, type);
                    }
                    tr.Commit();
                }
            }
            return map;
        }

        /// <summary>对比用的实体内容摘要（可序列化，两个库各留一份再比）。</summary>
        private sealed class EntitySnapshot
        {
            public string Handle;
            public string Type;
            public string Layer;
            public string Text;
            public double[] Bounds;

            public static EntitySnapshot Capture(Entity ent, string type)
            {
                var s = new EntitySnapshot
                {
                    Handle = ent.Handle.ToString(),
                    Type = type,
                    Layer = ent.Layer,
                    Text = ent is DBText dt ? dt.TextString : ent is MText mt ? mt.Text : null
                };
                try
                {
                    var e = ent.GeometricExtents;
                    s.Bounds = new[] { Math.Round(e.MinPoint.X, 2), Math.Round(e.MinPoint.Y, 2), Math.Round(e.MaxPoint.X, 2), Math.Round(e.MaxPoint.Y, 2) };
                }
                catch { }
                return s;
            }

            /// <summary>与新版对比；无实质变化返回 null，有变化返回人读的描述。</summary>
            public string DiffAgainst(EntitySnapshot other)
            {
                var parts = new List<string>();
                if (!string.Equals(Layer, other.Layer, StringComparison.OrdinalIgnoreCase))
                    parts.Add("图层 " + Layer + "→" + other.Layer);
                if (!string.Equals(Text, other.Text, StringComparison.Ordinal))
                    parts.Add("文字变更");
                if (Bounds != null && other.Bounds != null && Moved(other.Bounds))
                    parts.Add("位置移动");
                return parts.Count == 0 ? null : string.Join("；", parts);
            }

            private bool Moved(double[] other)
            {
                for (int i = 0; i < 4; i++)
                    if (Math.Abs(Bounds[i] - other[i]) > 0.01) return true;
                return false;
            }

            public Dictionary<string, object> ToSummary(string category)
            {
                var d = new Dictionary<string, object> { ["action"] = category, ["handle"] = Handle, ["type"] = Type, ["layer"] = Layer };
                if (Text != null) d["text"] = Text;
                if (Bounds != null) d["bounds"] = Bounds;
                return d;
            }
        }

        // ==================================================================== 表格

        private static McpExecutionResult HandleTableList(string argsJson, Document doc, Transaction tr)
        {
            var list = new List<object>();
            var ms = OpenModelSpace(tr, doc.Database);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Table table)) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["handle"] = table.Handle.ToString(),
                    ["rows"] = table.Rows.Count,
                    ["columns"] = table.Columns.Count,
                    ["layer"] = table.Layer,
                    ["position"] = new[] { Math.Round(table.Position.X, 1), Math.Round(table.Position.Y, 1) },
                    ["preview"] = CellPreview(table, 0, 0) + " | " + CellPreview(table, 0, 1)
                });
            }

            var data = new Dictionary<string, object> { ["ok"] = true, ["count"] = list.Count, ["tables"] = list };
            return new McpExecutionResult { ToolName = "cad_table_list", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleTableRead(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!TryGetTable(args, doc, tr, "cad_table_read", out Table table, out McpExecutionResult error))
                return error;

            var rows = new List<object>();
            for (int r = 0; r < table.Rows.Count; r++)
            {
                var cells = new List<string>();
                for (int c = 0; c < table.Columns.Count; c++)
                    cells.Add(table.Cells[r, c].TextString ?? string.Empty);
                rows.Add(cells);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["handle"] = table.Handle.ToString(),
                ["rows"] = table.Rows.Count,
                ["columns"] = table.Columns.Count,
                ["rowsData"] = rows
            };
            return new McpExecutionResult { ToolName = "cad_table_read", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleTableWrite(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!TryGetTable(args, doc, tr, "cad_table_write", out Table table, out McpExecutionResult error))
                return error;

            if (args == null || !(args.TryGetValue("cells", out var rawCells) && rawCells is System.Collections.IEnumerable seq))
                return Fail("cad_table_write", "cells array of {row,col,text} is required.");

            int written = 0;
            var errors = new List<string>();
            table.UpgradeOpen();
            foreach (var item in seq)
            {
                var cell = CadJson.Deserialize(CadJson.Serialize(item));
                int row = CadJson.GetInt(cell, "row", -1);
                int col = CadJson.GetInt(cell, "col", -1);
                string text = CadJson.GetString(cell, "text");
                if (row < 0 || col < 0) { errors.Add("行/列不能为负: row=" + row + " col=" + col); continue; }
                if (row >= table.Rows.Count || col >= table.Columns.Count) { errors.Add("越界: row=" + row + " col=" + col + "（表为 " + table.Rows.Count + "×" + table.Columns.Count + "）"); continue; }
                table.Cells[row, col].TextString = text;
                written++;
            }

            var data = new Dictionary<string, object> { ["ok"] = errors.Count == 0, ["written"] = written, ["errors"] = errors };
            return new McpExecutionResult { ToolName = "cad_table_write", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        private static bool TryGetTable(Dictionary<string, object> args, Document doc, Transaction tr,
                                        string tool, out Table table, out McpExecutionResult error)
        {
            table = null;
            error = null;
            string hex = CadJson.GetString(args, "handle");
            if (hex.Length == 0)
            { error = Fail(tool, "handle is required."); return false; }
            if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out long val) ||
                !doc.Database.TryGetObjectId(new Handle(val), out ObjectId id) || id.IsNull ||
                !(tr.GetObject(id, OpenMode.ForRead) is Table t))
            { error = Fail(tool, "Handle 不是 Table 实体: " + hex); return false; }
            table = t;
            return true;
        }

        private static string CellPreview(Table table, int r, int c)
        {
            if (r >= table.Rows.Count || c >= table.Columns.Count) return string.Empty;
            string s = table.Cells[r, c].TextString ?? string.Empty;
            return s.Length <= 12 ? s : s.Substring(0, 12) + "…";
        }

        // ==================================================================== 外部参照

        private static McpExecutionResult HandleXrefList(string argsJson, Document doc, Transaction tr)
        {
            var list = new List<object>();
            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = btr.Name,
                    ["path"] = btr.PathName,
                    ["isUnloaded"] = btr.IsUnloaded,
                    ["isResolved"] = !btr.IsUnloaded && File.Exists(btr.PathName)
                });
            }

            var data = new Dictionary<string, object> { ["ok"] = true, ["count"] = list.Count, ["xrefs"] = list };
            return new McpExecutionResult { ToolName = "cad_xref_list", Json = CadJson.Serialize(data), MutatesDrawing = false };
        }

        private static McpExecutionResult HandleXrefUpdate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            string newPath = Environment.ExpandEnvironmentVariables(CadJson.GetString(args, "new_path")).Trim();
            string action = CadJson.GetString(args, "action", "reload").ToLowerInvariant();

            if (name.Length == 0)
                return Fail("cad_xref_update", "name is required (from cad_xref_list).");

            var db = doc.Database;
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name))
                return Fail("cad_xref_update", "找不到块: " + name);

            var btr = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForWrite);
            if (!btr.IsFromExternalReference)
                return Fail("cad_xref_update", "块 " + name + " 不是外部参照。");

            var data = new Dictionary<string, object> { ["ok"] = true, ["name"] = name };

            if (newPath.Length > 0)
            {
                if (!File.Exists(newPath))
                    return Fail("cad_xref_update", "新路径文件不存在: " + newPath);
                btr.UpgradeOpen();
                btr.PathName = newPath;
                data["repointedTo"] = newPath;
            }

            switch (action)
            {
                case "unload":
                    btr.IsUnloaded = true;
                    data["action"] = "unloaded";
                    break;
                case "reload_all":
                    var allXrefs = new ObjectIdCollection();
                    foreach (ObjectId id in bt)
                    {
                        var r = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (r.IsFromExternalReference) allXrefs.Add(id);
                    }
                    if (allXrefs.Count > 0) db.ReloadXrefs(allXrefs);
                    data["action"] = "reloaded_all";
                    data["reloadedCount"] = allXrefs.Count;
                    break;
                default: // reload
                    btr.IsUnloaded = false;
                    db.ReloadXrefs(new ObjectIdCollection(new[] { btr.ObjectId }));
                    data["action"] = "reloaded";
                    break;
            }

            data["path"] = btr.PathName;
            return new McpExecutionResult { ToolName = "cad_xref_update", Json = CadJson.Serialize(data), MutatesDrawing = true };
        }

        // ==================================================================== 公共

        private static BlockTableRecord OpenModelSpace(Transaction tr, Database db)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var list = new List<string>();
            if (args != null && args.TryGetValue(key, out var raw) && raw is System.Collections.IEnumerable seq && !(raw is string))
            {
                foreach (var item in seq)
                {
                    string s = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }

        private static McpExecutionResult Fail(string tool, string error)
            => new McpExecutionResult { ToolName = tool, Json = "{\"ok\":false,\"error\":\"" + error.Replace("\"", "'") + "\"}", MutatesDrawing = false };
    }
}
