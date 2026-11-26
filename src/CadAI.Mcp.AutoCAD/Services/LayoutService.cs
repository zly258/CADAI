using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 布局（图纸空间 / 模型空间）管理：列出、切换、新建、重命名。
    /// 出图、换图框、按专业拆图这些任务都先要落到正确的布局上，故独立成域。
    /// </summary>
    internal static class LayoutService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_layout_list", HandleList,
                CadSchema.Function("cad_layout_list",
                    "List all layouts in the drawing (Model space plus every paper-space layout tab) with tab order, entity count, paper size and plot settings.",
                    CadSchema.Obj()));

            registry.Register("cad_layout_switch", HandleSwitch,
                CadSchema.Function("cad_layout_switch",
                    "Switch the active layout tab (e.g. to 'Model' or 'Layout1'). Also switches the drawing between model space and paper space.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "Target layout name exactly as shown on the layout tab.", true))));

            registry.Register("cad_layout_create", HandleCreate,
                CadSchema.Function("cad_layout_create",
                    "Create a new paper-space layout, optionally copying the plot settings and title block of an existing layout.",
                    CadSchema.Obj(
                        CadSchema.Str("name", "New layout name (must not already exist).", true),
                        CadSchema.Str("copy_from", "Optional existing layout name to use as a template."))));

            registry.Register("cad_layout_rename", HandleRename,
                CadSchema.Function("cad_layout_rename",
                    "Rename an existing layout tab.",
                    CadSchema.Obj(
                        CadSchema.Str("old_name", "Current layout name.", true),
                        CadSchema.Str("new_name", "New layout name.", true))));
        }

        private static McpExecutionResult HandleList(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;
            string current = CadSpaces.CurrentLayoutName(doc);
            var list = new List<object>();

            foreach (var space in CadSpaces.Enumerate(db, tr))
            {
                int entityCount = 0;
                foreach (ObjectId _ in space.Record) entityCount++;

                var item = new Dictionary<string, object>
                {
                    ["name"] = space.Name,
                    ["isModel"] = space.IsModel,
                    ["tabOrder"] = space.TabOrder,
                    ["isCurrent"] = string.Equals(space.Name, current, StringComparison.OrdinalIgnoreCase),
                    ["entityCount"] = entityCount,
                    ["handle"] = space.Record.Handle.ToString()
                };

                if (!space.IsModel)
                {
                    try
                    {
                        var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);
                        item["paperSize"] = layout.CanonicalMediaName;
                        item["plotRotation"] = layout.PlotRotation.ToString();
                        item["plotType"] = layout.PlotType.ToString();
                        item["plotCentered"] = layout.PlotCentered;
                        item["plotPaperUnits"] = layout.PlotPaperUnits.ToString();
                    }
                    catch { }
                }

                list.Add(item);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["currentLayout"] = current,
                ["tileMode"] = db.TileMode,
                ["total"] = list.Count,
                ["layouts"] = list
            };

            return new McpExecutionResult
            {
                ToolName = "cad_layout_list",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleSwitch(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");

            if (string.IsNullOrWhiteSpace(name))
                return Fail("cad_layout_switch", "name is required.");

            var space = CadSpaces.Find(doc.Database, tr, name);
            if (space == null)
                return Fail("cad_layout_switch", "Layout not found: " + name);

            try
            {
                // LayoutManager 会同步维护 TILEMODE：切到图纸空间布局自动置 0，切回 Model 置 1。
                LayoutManager.Current.CurrentLayout = space.Name;
            }
            catch (Exception ex)
            {
                return Fail("cad_layout_switch", "切换布局失败: " + ex.Message);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["currentLayout"] = space.Name,
                ["isModel"] = space.IsModel,
                ["tileMode"] = doc.Database.TileMode
            };

            return new McpExecutionResult
            {
                ToolName = "cad_layout_switch",
                Json = CadJson.Serialize(data),
                MutatesDrawing = false
            };
        }

        private static McpExecutionResult HandleCreate(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string name = CadJson.GetString(args, "name");
            string copyFrom = CadJson.GetString(args, "copy_from");

            if (string.IsNullOrWhiteSpace(name))
                return Fail("cad_layout_create", "name is required.");

            name = name.Trim();

            if (CadSpaces.Find(doc.Database, tr, name) != null)
                return Fail("cad_layout_create", "布局已存在: " + name);

            try
            {
                var manager = LayoutManager.Current;
                CadSpace template = null;

                if (!string.IsNullOrWhiteSpace(copyFrom))
                {
                    template = CadSpaces.Find(doc.Database, tr, copyFrom);
                    if (template == null || template.IsModel)
                        return Fail("cad_layout_create", "模板布局不存在或是模型空间: " + copyFrom);
                }

                ObjectId created = manager.CreateLayout(name);
                if (created.IsNull)
                    return Fail("cad_layout_create", "AutoCAD 未返回新布局句柄。");

                int copiedEntities = 0;
                if (template != null)
                {
                    var newLayout = (Layout)tr.GetObject(created, OpenMode.ForWrite);
                    CopyPlotSettings((Layout)tr.GetObject(template.LayoutId, OpenMode.ForRead), newLayout);
                    copiedEntities = CopyLayoutContents(tr, template.Record, newLayout);
                }

                var result = (Layout)tr.GetObject(created, OpenMode.ForRead);
                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["name"] = result.LayoutName,
                    ["handle"] = result.Handle.ToString(),
                    ["copiedFrom"] = template == null ? null : template.Name,
                    ["copiedEntityCount"] = copiedEntities
                };

                return new McpExecutionResult
                {
                    ToolName = "cad_layout_create",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = true
                };
            }
            catch (Exception ex)
            {
                return Fail("cad_layout_create", "新建布局失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 复制页面设置（打印设备 / 图纸尺寸 / 比例 / 方向 / 居中 / 打印样式表）。
        ///
        /// 注意：CanonicalMediaName、PlotRotation、StdScaleType 等在 AutoCAD 托管 API 里都是
        /// **只读**属性，必须经 PlotSettingsValidator 修改；只有少数开关（打印线宽、打印样式、
        /// 视口边框等）是 Layout 上的可写属性，直接搬即可。
        /// 整段都做了容错：某个属性在本机驱动上不被支持时跳过，不影响布局本身的创建。
        /// </summary>
        private static void CopyPlotSettings(Layout source, Layout target)
        {
            PlotSettingsValidator validator;
            try { validator = PlotSettingsValidator.Current; }
            catch { validator = null; }

            if (validator != null)
            {
                // 打印设备 + 图纸尺寸必须成对设置，否则会落在"无"设备上。
                string device = source.PlotConfigurationName;
                if (!string.IsNullOrWhiteSpace(device) && !string.IsNullOrWhiteSpace(source.CanonicalMediaName))
                {
                    try { validator.SetPlotConfigurationName(target, device, source.CanonicalMediaName); }
                    catch { }
                }

                try
                {
                    if (source.UseStandardScale)
                    {
                        validator.SetUseStandardScale(target, true);
                        validator.SetStdScaleType(target, source.StdScaleType);
                    }
                    else
                    {
                        validator.SetUseStandardScale(target, false);
                        validator.SetCustomPrintScale(target, source.CustomPrintScale);
                    }
                }
                catch { }

                try { validator.SetPlotRotation(target, source.PlotRotation); } catch { }
                try { validator.SetPlotPaperUnits(target, source.PlotPaperUnits); } catch { }
                try { validator.SetPlotCentered(target, source.PlotCentered); } catch { }
                try { validator.SetPlotOrigin(target, source.PlotOrigin); } catch { }
                try { validator.SetPlotType(target, source.PlotType); } catch { }
                if (!string.IsNullOrWhiteSpace(source.CurrentStyleSheet))
                {
                    try { validator.SetCurrentStyleSheet(target, source.CurrentStyleSheet); } catch { }
                }
            }

            // Layout 上的可写开关，直接搬。
            target.PrintLineweights = source.PrintLineweights;
            target.PlotPlotStyles = source.PlotPlotStyles;
            target.ShowPlotStyles = source.ShowPlotStyles;
            target.PlotViewportBorders = source.PlotViewportBorders;
            target.DrawViewportsFirst = source.DrawViewportsFirst;
            target.PlotHidden = source.PlotHidden;
            target.ScaleLineweights = source.ScaleLineweights;
            target.PlotTransparency = source.PlotTransparency;
            target.ShadePlot = source.ShadePlot;
            target.ShadePlotResLevel = source.ShadePlotResLevel;
            target.ShadePlotCustomDpi = source.ShadePlotCustomDpi;
        }

        /// <summary>
        /// 把模板布局里的图元复制到新布局（典型用途：图框 + 标题栏）。
        /// 视口（Viewport）刻意跳过：视口的编号与布局强绑定，复制过去会变成一堆指向错误区域的空洞。
        /// </summary>
        private static int CopyLayoutContents(Transaction tr, BlockTableRecord source, Layout targetLayout)
        {
            var target = (BlockTableRecord)tr.GetObject(targetLayout.BlockTableRecordId, OpenMode.ForWrite);
            int copied = 0;

            foreach (ObjectId id in source)
            {
                Entity entity;
                try { entity = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                catch { continue; }
                if (entity == null) continue;
                if (entity is Viewport) continue;

                try
                {
                    var clone = (Entity)entity.Clone();
                    target.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    copied++;
                }
                catch { }
            }

            return copied;
        }

        private static McpExecutionResult HandleRename(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string oldName = CadJson.GetString(args, "old_name");
            string newName = CadJson.GetString(args, "new_name");

            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return Fail("cad_layout_rename", "old_name 与 new_name 均为必填。");

            var space = CadSpaces.Find(doc.Database, tr, oldName);
            if (space == null)
                return Fail("cad_layout_rename", "布局不存在: " + oldName);
            if (space.IsModel)
                return Fail("cad_layout_rename", "模型空间标签不可重命名。");
            if (CadSpaces.Find(doc.Database, tr, newName) != null)
                return Fail("cad_layout_rename", "目标布局名已存在: " + newName);

            try
            {
                LayoutManager.Current.RenameLayout(space.Name, newName.Trim());
            }
            catch (Exception ex)
            {
                return Fail("cad_layout_rename", "重命名失败: " + ex.Message);
            }

            var data = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["oldName"] = space.Name,
                ["newName"] = newName.Trim()
            };

            return new McpExecutionResult
            {
                ToolName = "cad_layout_rename",
                Json = CadJson.Serialize(data),
                MutatesDrawing = true
            };
        }

        private static McpExecutionResult Fail(string tool, string message)
        {
            return new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + message.Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
        }
    }
}
