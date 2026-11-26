using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 原生命令兜底、数据库清理与出图交付。
    ///
    /// 这一域的原则是**不许把"排队"说成"完成"**：
    ///  - PURGE / AUDIT 走 .NET API 同步做完（旧实现发命令行，工具却立刻返回 ok:true，
    ///    模型据此宣称"已清理"，而命令可能还没执行、甚至因为上下文不对而失败）；
    ///  - PDF 出图走 PlotEngine 同步出图（旧实现发 `-EXPORT _PDF` 命令就返回"导出已启动"，
    ///    用户拿到的是一个可能根本不存在的路径）；
    ///  - 只有真正无法同步的任意原生命令（cad_execute_command）才排队，
    ///    并在结果里显式标 queued=true / completed=false。
    /// </summary>
    internal static class CommandService
    {
        /// <summary>AutoCAD 自带的 PDF 打印设备。没有它就只能退回命令行列印。</summary>
        internal const string PdfPlotDevice = "DWG To PDF.pc3";

        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_execute_command", HandleExecuteCommand,
                CadSchema.Function("cad_execute_command",
                    "Execute a native AutoCAD command or LISP expression as a fallback when no specific MCP tool is available. "
                    + "The command is QUEUED on the command line and runs asynchronously AFTER this call returns — "
                    + "verify its effect with a fresh read-only query before reporting success.",
                    CadSchema.Obj(
                        CadSchema.Str("command", "AutoCAD command or LISP expression string.", true))));

            registry.Register("cad_audit_purge", HandleAuditPurge,
                CadSchema.Function("cad_audit_purge",
                    "Audit the database for errors and purge all unreferenced layers, blocks, text styles, dimension styles and linetypes. Runs synchronously.",
                    CadSchema.Obj()));

            registry.Register("cad_export_pdf", HandleExportPdf,
                CadSchema.Function("cad_export_pdf",
                    "Export the active layout to a PDF document using AutoCAD's plot engine. Runs synchronously and reports the real output path.",
                    CadSchema.Obj(
                        CadSchema.Str("output_pdf_path", "Target PDF file path. If omitted, creates a PDF next to the DWG file."),
                        CadSchema.Str("layout", "Optional layout name to plot. Defaults to the active layout."))));

            registry.Register("cad_batch_execute",
                (argsJson, doc, tr) => HandleBatchExecute(registry, argsJson, doc, tr),
                CadSchema.Function("cad_batch_execute",
                    "Execute multiple cad_* tools sequentially inside ONE AutoCAD transaction (e.g., create layers + draw geometry + dimension + set properties in one round-trip).",
                    CadSchema.Obj(
                        CadSchema.Arr("operations", "Array of { tool: string, arguments: object } objects to run in sequence.", "object", true),
                        CadSchema.Bool("stop_on_error", "Abort remaining operations if any step fails (default true)."))));
        }

        // ================================================================== 原生命令

        private static McpExecutionResult HandleExecuteCommand(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string cmd = CadJson.GetString(args, "command");

            if (string.IsNullOrWhiteSpace(cmd))
                return Failure("cad_execute_command", "command 不能为空。");

            try
            {
                doc.SendStringToExecute(cmd + " \n", true, false, false);

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["queued"] = true,
                    ["completed"] = false,
                    ["command"] = cmd,
                    ["message"] = "命令已排队到 AutoCAD 命令行，将在本次调用返回后异步执行。",
                    ["note"] = "排队不等于完成：请用只读查询（cad_query_entities / cad_get_entity_info / "
                        + "cad_drawing_statistics）确认效果后再向用户报告成功。"
                };

                return Mutating("cad_execute_command", CadJson.Serialize(data));
            }
            catch (Exception ex)
            {
                return Failure("cad_execute_command", "命令排队失败: " + ex.Message);
            }
        }

        // ================================================================== 清理

        private static McpExecutionResult HandleAuditPurge(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;

            // 1) 审查修复：同步执行。注意 Database.Audit 返回 void——它不告诉你有没有改东西，
            //    所以结果里只报"已执行"，不编造"修复了 N 处"。
            bool auditRan = false;
            string auditError = null;
            try
            {
                // Audit(auditXdata, fixErrors)：第二个参数为真时直接修复。
                db.Audit(true, true);
                auditRan = true;
            }
            catch (Exception ex)
            {
                auditError = ex.Message;
            }

            // 2) 清理：把所有符号表记录作为候选交给 Database.Purge，由它自己做依赖分析，
            //    只删真正没人引用的。逐表统计前后条数，得到**真实**清理数量。
            var purged = new List<object>();
            int purgedTotal = 0;

            try
            {
                var tables = new List<KeyValuePair<string, ObjectId>>
                {
                    new KeyValuePair<string, ObjectId>("Layer", db.LayerTableId),
                    new KeyValuePair<string, ObjectId>("TextStyle", db.TextStyleTableId),
                    new KeyValuePair<string, ObjectId>("DimStyle", db.DimStyleTableId),
                    new KeyValuePair<string, ObjectId>("Linetype", db.LinetypeTableId),
                    new KeyValuePair<string, ObjectId>("Block", db.BlockTableId),
                    new KeyValuePair<string, ObjectId>("RegApp", db.RegAppTableId),
                    new KeyValuePair<string, ObjectId>("Ucs", db.UcsTableId),
                    new KeyValuePair<string, ObjectId>("View", db.ViewTableId),
                    new KeyValuePair<string, ObjectId>("Viewport", db.ViewportTableId)
                };

                var candidates = new ObjectIdCollection();
                var before = new Dictionary<string, int>(StringComparer.Ordinal);

                foreach (var entry in tables)
                {
                    var table = (SymbolTable)tr.GetObject(entry.Value, OpenMode.ForRead);
                    int count = 0;
                    foreach (ObjectId id in table)
                    {
                        candidates.Add(id);
                        count++;
                    }
                    before[entry.Key] = count;
                }

                db.Purge(candidates);

                foreach (var entry in tables)
                {
                    var table = (SymbolTable)tr.GetObject(entry.Value, OpenMode.ForRead);
                    int after = 0;
                    foreach (ObjectId _ in table) after++;

                    int removed = before[entry.Key] - after;
                    if (removed <= 0) continue;

                    purgedTotal += removed;
                    purged.Add(new Dictionary<string, object>
                    {
                        ["table"] = entry.Key,
                        ["removed"] = removed,
                        ["remaining"] = after
                    });
                }
            }
            catch (Exception ex)
            {
                return Failure("cad_audit_purge", "清理失败: " + ex.Message);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["auditRan"] = auditRan,
                ["purgedTotal"] = purgedTotal,
                ["purgedByTable"] = purged,
                ["synchronous"] = true,
                ["note"] = "已同步完成审查与清理，改动计入本轮撤销组。"
            };
            if (auditError != null) data["auditWarning"] = auditError;

            return Mutating("cad_audit_purge", CadJson.Serialize(data));
        }

        // ================================================================== 出图

        private static McpExecutionResult HandleExportPdf(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string pdfPath = CadJson.GetString(args, "output_pdf_path");
            string layoutName = CadJson.GetString(args, "layout");

            if (string.IsNullOrWhiteSpace(pdfPath))
            {
                string dwgDir = Path.GetDirectoryName(doc.Name) ?? Path.GetTempPath();
                string dwgName = Path.GetFileNameWithoutExtension(doc.Name);
                if (string.IsNullOrWhiteSpace(dwgName)) dwgName = "drawing";
                pdfPath = Path.Combine(dwgDir, dwgName + ".pdf");
            }

            // 目标目录不存在就建出来，否则 PlotEngine 会在最后一步才失败，白跑一遍。
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(pdfPath));
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                return Failure("cad_export_pdf", "输出目录不可用: " + ex.Message);
            }

            // 预先清理目标文件，避免驱动层或命令行触发"文件已存在，是否覆盖"模态确认框。
            TryDeleteFile(pdfPath);

            // 定位要出图的布局。
            string targetLayout = layoutName;
            if (string.IsNullOrWhiteSpace(targetLayout))
            {
                try { targetLayout = LayoutManager.Current.CurrentLayout; }
                catch { targetLayout = "Model"; }
            }

            var space = CadSpaces.Find(doc.Database, tr, targetLayout);
            if (space == null)
                return Failure("cad_export_pdf", "布局不存在: " + targetLayout);

            try
            {
                var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
                try
                {
                    PlotToPdf(doc.Database, layout, pdfPath);
                }
                catch (Exception plotError) when (ForegroundPlotScope.IsBackgroundPlotBusy(plotError))
                {
                    // 残留的后台打印作业还没退出：等队列清空后再给一次机会，
                    // 而不是立刻退回命令行（那一条路径同样是异步的，PDF 未必生成）。
                    ForegroundPlotScope.WaitForBackgroundPlot(3000);
                    PlotToPdf(doc.Database, layout, pdfPath);
                }

                if (!File.Exists(pdfPath))
                    return Failure("cad_export_pdf",
                        "出图流程已结束但目标 PDF 未生成：" + pdfPath + "。请检查该布局是否有可打印内容。");

                var info = new FileInfo(pdfPath);
                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["outputPdfPath"] = pdfPath,
                    ["layout"] = space.Name,
                    ["bytes"] = info.Length,
                    ["synchronous"] = true,
                    ["message"] = "PDF 已生成（" + Math.Round(info.Length / 1024.0, 1) + " KB）。"
                };

                return new McpExecutionResult
                {
                    ToolName = "cad_export_pdf",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false
                };
            }
            catch (Exception ex)
            {
                // 同步出图失败时退回命令行列印，但**如实标注它是异步的**，
                // 不再像旧实现那样把"已启动"说成"已完成"。
                return FallbackCommandExport(doc, pdfPath, space.Name, ex.Message);
            }
        }

        /// <summary>
        /// 用 PlotEngine 同步把一张布局打成 PDF。
        ///
        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// 用 PlotEngine 同步把一张布局打成 PDF。
        ///
        /// 关键点：<see cref="PlotSettings"/> 上的打印设备/图纸/比例在托管 API 里都是**只读**的，
        /// 必须经 <see cref="PlotSettingsValidator"/> 修改；图纸名要先跟设备的可用列表核对，
        /// 对不上时按布局自身的纸张尺寸挑最接近的一张，否则 Validate 会直接抛。
        /// </summary>
        internal static void PlotToPdf(Database db, Layout layout, string pdfPath)
        {
            var validator = PlotSettingsValidator.Current;

            // 设备可用性预检：没装 "DWG To PDF.pc3" 就不要往下走，否则异常信息很难懂。
            var devices = validator.GetPlotDeviceList();
            if (!Contains(devices, PdfPlotDevice))
                throw new InvalidOperationException("本机未安装 PDF 打印设备：" + PdfPlotDevice);

            // 强制前台出图：后台出图会让 EndPlot 提前返回（PDF 还没落盘），
            // 且下一次出图会撞 eBackgroundPlotInProgress 并卡死整条链路。
            using (ForegroundPlotScope.Enter())
            using (var settings = new PlotSettings(layout.ModelType))
            using (var plotInfo = new PlotInfo())
            using (var pageInfo = new PlotPageInfo())
            using (var infoValidator = new PlotInfoValidator())
            using (PlotEngine engine = PlotFactory.CreatePublishEngine())
            {
                settings.CopyFrom(layout);

                // 先切换到 PDF 设备并刷新列表，再匹配可用纸张。
                validator.SetPlotConfigurationName(settings, PdfPlotDevice, null);
                validator.RefreshLists(settings);

                string mediaName = layout.CanonicalMediaName;
                StringCollection availableMedia = validator.GetCanonicalMediaNameList(settings);
                string matchedMedia = PublishService.MatchMediaName(availableMedia, mediaName);

                if (matchedMedia != null)
                {
                    validator.SetCanonicalMediaName(settings, matchedMedia);
                }
                else
                {
                    var paper = layout.PlotPaperSize;
                    validator.SetClosestMediaName(settings, paper.X, paper.Y, layout.PlotPaperUnits, false);
                }

                if (layout.ModelType)
                {
                    validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                    validator.SetUseStandardScale(settings, true);
                    validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                    try { validator.SetPlotCentered(settings, true); } catch { }
                }
                else
                {
                    if (layout.PlotType == Autodesk.AutoCAD.DatabaseServices.PlotType.Extents)
                    {
                        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Extents);
                        validator.SetUseStandardScale(settings, true);
                        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
                        try { validator.SetPlotCentered(settings, true); } catch { }
                    }
                    else
                    {
                        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
                    }
                }

                plotInfo.Layout = layout.ObjectId;
                plotInfo.OverrideSettings = settings;

                infoValidator.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                infoValidator.Validate(plotInfo);

                engine.BeginPlot(null, null);
                engine.BeginDocument(plotInfo, System.IO.Path.GetFileName(db.Filename), null, 1, true, pdfPath);
                engine.BeginPage(pageInfo, plotInfo, true, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                engine.EndDocument(null);
                engine.EndPlot(null);
            }
        }

        internal static bool Contains(StringCollection collection, string value)
        {
            if (collection == null || string.IsNullOrWhiteSpace(value)) return false;
            foreach (string item in collection)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static McpExecutionResult FallbackCommandExport(Document doc, string pdfPath, string layoutName, string reason)
        {
            try
            {
                TryDeleteFile(pdfPath);
                try { Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("FILEDIA", (short)0); } catch { }
                try { Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDDIA", (short)0); } catch { }

                string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "._-EXPORT _PDF _CURRENT \"{0}\" \n", pdfPath.Replace("\\", "/"));
                doc.SendStringToExecute(cmd, true, false, false);

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["outputPdfPath"] = pdfPath,
                    ["layout"] = layoutName,
                    ["queued"] = true,
                    ["completed"] = false,
                    ["synchronous"] = false,
                    ["message"] = "同步出图失败，已退回命令行导出（异步）。",
                    ["note"] = "PDF 尚未生成：请在数秒后用只读方式确认文件存在，再向用户报告成功。",
                    ["fallbackReason"] = reason
                };
                return new McpExecutionResult
                {
                    ToolName = "cad_export_pdf",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false
                };
            }
            catch (Exception ex)
            {
                return Failure("cad_export_pdf", "PDF 导出失败: " + reason + "；命令行兜底也失败: " + ex.Message);
            }
        }

        // ================================================================== 批量执行

        private static McpExecutionResult HandleBatchExecute(
            CadToolRegistry registry, string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            if (!args.TryGetValue("operations", out object rawOps) || !(rawOps is System.Collections.IEnumerable seq))
                return Failure("cad_batch_execute", "operations array is required.");

            bool stopOnError = CadJson.GetBool(args, "stop_on_error", true);
            var stepResults = new List<Dictionary<string, object>>();
            int total = 0, succeeded = 0, failed = 0;
            bool anyMutated = false;

            foreach (object item in seq)
            {
                total++;
                var opDict = item as Dictionary<string, object>;
                string toolName = opDict != null ? CadJson.GetString(opDict, "tool") : string.Empty;
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    failed++;
                    stepResults.Add(new Dictionary<string, object>
                    {
                        ["index"] = total,
                        ["ok"] = false,
                        ["error"] = "Missing tool name in operation."
                    });
                    if (stopOnError) break;
                    continue;
                }

                if (string.Equals(toolName, "cad_batch_execute", StringComparison.OrdinalIgnoreCase))
                {
                    failed++;
                    stepResults.Add(new Dictionary<string, object>
                    {
                        ["index"] = total,
                        ["tool"] = toolName,
                        ["ok"] = false,
                        ["error"] = "Recursive cad_batch_execute is not allowed."
                    });
                    if (stopOnError) break;
                    continue;
                }

                string opArgsJson = "{}";
                if (opDict != null && opDict.TryGetValue("arguments", out object argObj) && argObj != null)
                {
                    opArgsJson = argObj is string str ? str : CadJson.Serialize(argObj);
                }

                try
                {
                    McpExecutionResult subResult = registry.Execute(toolName, opArgsJson, doc, tr);
                    Dictionary<string, object> parsed = null;
                    try { parsed = CadJson.Deserialize(subResult.Json); } catch { }
                    bool stepOk = parsed == null || CadJson.GetBool(parsed, "ok", true);
                    anyMutated |= subResult.MutatesDrawing;

                    if (stepOk) succeeded++;
                    else failed++;

                    stepResults.Add(new Dictionary<string, object>
                    {
                        ["index"] = total,
                        ["tool"] = toolName,
                        ["ok"] = stepOk,
                        ["result"] = (object)parsed ?? subResult.Json
                    });

                    if (!stepOk && stopOnError) break;
                }
                catch (Exception ex)
                {
                    failed++;
                    stepResults.Add(new Dictionary<string, object>
                    {
                        ["index"] = total,
                        ["tool"] = toolName,
                        ["ok"] = false,
                        ["error"] = ex.Message
                    });
                    if (stopOnError) break;
                }
            }

            bool batchOk = failed == 0 && total > 0;
            var payload = new Dictionary<string, object>
            {
                ["ok"] = batchOk,
                ["total"] = total,
                ["succeeded"] = succeeded,
                ["failed"] = failed,
                ["results"] = stepResults
            };

            return new McpExecutionResult
            {
                ToolName = "cad_batch_execute",
                Json = CadJson.Serialize(payload),
                MutatesDrawing = anyMutated
            };
        }

        // ================================================================== 结果

        private static McpExecutionResult Mutating(string tool, string json)
            => new McpExecutionResult { ToolName = tool, Json = json, MutatesDrawing = true };

        private static McpExecutionResult Failure(string tool, string message)
            => new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
    }
}
