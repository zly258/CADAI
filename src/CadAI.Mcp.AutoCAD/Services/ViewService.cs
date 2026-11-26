using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Mcp.AutoCAD.Services
{
    /// <summary>
    /// 视图与图纸转 PNG。
    ///
    /// 两个关键设计：
    ///  1. **缩放必须是同步的**。旧实现用 SendStringToExecute("._ZOOM")，命令只是排队，
    ///     工具却立刻返回 ok:true。于是"先 zoom_entity 再取图"这种用法存在竞态——
    ///     取图时视图可能还没动。这里改用 Editor.SetCurrentView 直接改当前视图（同步生效），
    ///     并 UpdateScreen 强制重绘；原生命令只作为失败时的兜底。
    ///  2. **取图走打印引擎，不做屏幕截图**。屏幕截图会拍到窗口遮挡、面板、光标，
    ///     而且依赖窗口可见/硬件加速。这里用 PlotEngine 把当前布局按 Extents/Window
    ///     出成 PNG：矢量渲染、不受遮挡影响，范围由 scope/min_x..max_y/handle 决定。
    ///     返回结构仍保持 ChatCore 视觉链路要求的 {"images":[{"ok":true,"path":"..."}]}。
    /// </summary>
    internal static class ViewService
    {
        public static void Register(CadToolRegistry registry)
        {
            registry.Register("cad_view_zoom_extents", HandleZoomExtents,
                CadSchema.Function("cad_view_zoom_extents",
                    "Zoom the viewport to show the whole drawing extents. Takes effect synchronously.",
                    CadSchema.Obj()));

            registry.Register("cad_view_zoom_window", HandleZoomWindow,
                CadSchema.Function("cad_view_zoom_window",
                    "Zoom the viewport to focus on a specific rectangular coordinate window. Takes effect synchronously.",
                    CadSchema.Obj(
                        CadSchema.Num("min_x", "Lower-left X coordinate.", true),
                        CadSchema.Num("min_y", "Lower-left Y coordinate.", true),
                        CadSchema.Num("max_x", "Upper-right X coordinate.", true),
                        CadSchema.Num("max_y", "Upper-right Y coordinate.", true))));

            registry.Register("cad_view_zoom_entity", HandleZoomEntity,
                CadSchema.Function("cad_view_zoom_entity",
                    "Zoom the viewport to focus on a specific entity by its Handle, and highlight it. Takes effect synchronously.",
                    CadSchema.Obj(
                        CadSchema.Str("handle", "Entity hex Handle.", true))));

            registry.Register("cad_view_capture_screen", HandleCaptureScreen,
                CadSchema.Function("cad_view_capture_screen",
                    "Render the current AutoCAD drawing to a PNG image with the plot engine (vector render, not a screen capture; "
                    + "window chrome and panels can never occlude the result). "
                    + "By default plots the whole drawing extents; supports a specified rectangular window (min_x, min_y, max_x, max_y), "
                    + "an entity handle, the current view (scope='current'), or the whole layout (scope='layout'). "
                    + "Returns the saved image path(s) under the session workspace Captures folder.",
                    CadSchema.Obj(
                        CadSchema.Str("scope", "Plot scope: 'extents' (default), 'window', 'current', or 'layout'.", false),
                        CadSchema.Num("min_x", "Lower-left X for scope='window'.", false),
                        CadSchema.Num("min_y", "Lower-left Y for scope='window'.", false),
                        CadSchema.Num("max_x", "Upper-right X for scope='window'.", false),
                        CadSchema.Num("max_y", "Upper-right Y for scope='window'.", false),
                        CadSchema.Str("handle", "Entity hex Handle to plot around (implies a window plot).", false),
                        CadSchema.Int("width", "Preferred output width in pixels; 0 = derive from sheet size and dpi. The raster plot device may snap to its own media size; read 'width' in the result for the actual size.", false),
                        CadSchema.Int("height", "Preferred output height in pixels; 0 = derive from sheet size and dpi. Best effort, see width.", false),
                        CadSchema.Int("dpi", "Raster resolution in DPI when width/height are 0 (default 150).", false))));
        }

        // ================================================================== 缩放

        public static McpExecutionResult HandleZoomExtents(string argsJson, Document doc, Transaction tr)
        {
            var db = doc.Database;

            Extents3d extents;
            try
            {
                // 布局空间不能用 db.Extmin/Extmax（那是模型空间范围）；按当前布局实体算。
                if (!db.TileMode && TryGetCurrentSpaceExtents(doc, tr, out Extents3d spaceExtents))
                {
                    extents = spaceExtents;
                }
                else
                {
                    var min = db.Extmin;
                    var max = db.Extmax;
                    if (min.X >= max.X && min.Y >= max.Y)
                        throw new InvalidOperationException("图纸范围为空。");
                    extents = new Extents3d(min, max);
                }
            }
            catch (Exception ex)
            {
                return FallbackZoom(doc, "._ZOOM _E ", "cad_view_zoom_extents", "全图缩放", ex.Message);
            }

            return ApplyView(doc, extents, 0.05, "cad_view_zoom_extents", "已执行全图缩放。");
        }

        /// <summary>当前布局（模型/图纸空间）所有实体的并集范围；没有实体时返回 false。</summary>
        private static bool TryGetCurrentSpaceExtents(Document doc, Transaction tr, out Extents3d extents)
        {
            extents = default(Extents3d);
            try
            {
                var space = CadSpaces.Find(doc.Database, tr, CadSpaces.CurrentLayoutName(doc));
                if (space == null) return false;

                bool has = false;
                foreach (ObjectId id in space.Record)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    try
                    {
                        Extents3d e = ent.GeometricExtents;
                        if (!has)
                        {
                            extents = e;
                            has = true;
                        }
                        else
                        {
                            extents = new Extents3d(
                                new Point3d(Math.Min(extents.MinPoint.X, e.MinPoint.X),
                                            Math.Min(extents.MinPoint.Y, e.MinPoint.Y),
                                            Math.Min(extents.MinPoint.Z, e.MinPoint.Z)),
                                new Point3d(Math.Max(extents.MaxPoint.X, e.MaxPoint.X),
                                            Math.Max(extents.MaxPoint.Y, e.MaxPoint.Y),
                                            Math.Max(extents.MaxPoint.Z, e.MaxPoint.Z)));
                        }
                    }
                    catch { }
                }
                return has;
            }
            catch { return false; }
        }

        public static McpExecutionResult HandleZoomWindow(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            double minX = CadJson.GetDouble(args, "min_x", double.NaN);
            double minY = CadJson.GetDouble(args, "min_y", double.NaN);
            double maxX = CadJson.GetDouble(args, "max_x", double.NaN);
            double maxY = CadJson.GetDouble(args, "max_y", double.NaN);

            if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY))
                return Failure("cad_view_zoom_window", "必须提供有效的 min_x, min_y, max_x, max_y 坐标。");

            if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
            if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }

            var extents = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
            return ApplyView(doc, extents, 0.05, "cad_view_zoom_window",
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "已将视口聚焦到区域: ({0:F2}, {1:F2}) -> ({2:F2}, {3:F2})", minX, minY, maxX, maxY));
        }

        private static McpExecutionResult HandleZoomEntity(string argsJson, Document doc, Transaction tr)
        {
            var args = CadJson.Deserialize(argsJson);
            string hexHandle = CadJson.GetString(args, "handle");

            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                return Failure("cad_view_zoom_entity", "无效的十六进制句柄。");

            var db = doc.Database;
            if (!db.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                return Failure("cad_view_zoom_entity", "未找到图元。");

            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null)
                return Failure("cad_view_zoom_entity", "该句柄不是图元。");

            Extents3d extents;
            try
            {
                extents = ent.GeometricExtents;
                // 退化情形（单点文字、零长线）：给一个最小可视窗口，否则 SetCurrentView 会除零。
                if (extents.MaxPoint.X - extents.MinPoint.X < 1e-6)
                    extents = new Extents3d(
                        extents.MinPoint + new Vector3d(-5, 0, 0),
                        extents.MaxPoint + new Vector3d(5, 0, 0));
                if (extents.MaxPoint.Y - extents.MinPoint.Y < 1e-6)
                    extents = new Extents3d(
                        extents.MinPoint + new Vector3d(0, -5, 0),
                        extents.MaxPoint + new Vector3d(0, 5, 0));
            }
            catch (Exception ex)
            {
                return FallbackZoom(doc,
                    string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "._ZOOM _O (handent \"{0}\") ", hexHandle),
                    "cad_view_zoom_entity", "聚焦图元 " + hexHandle, ex.Message);
            }

            // 顺手高亮，让用户一眼看到 AI 在说哪个图元。
            try { doc.Editor.SetImpliedSelection(new[] { id }); } catch { }

            return ApplyView(doc, extents, 0.2, "cad_view_zoom_entity", "已将视口聚焦到图元: " + hexHandle);
        }

        /// <summary>
        /// 同步改当前视图：算出中心与视口尺寸后交给 Editor.SetCurrentView，并强制重绘。
        /// 这样紧跟其后的截图一定拍到新视图，不再依赖"命令排到队里什么时候执行"。
        /// </summary>
        private static McpExecutionResult ApplyView(Document doc, Extents3d extents, double paddingRatio,
                                                    string tool, string message)
        {
            try
            {
                double minX = extents.MinPoint.X, minY = extents.MinPoint.Y;
                double maxX = extents.MaxPoint.X, maxY = extents.MaxPoint.Y;
                double width = Math.Max(maxX - minX, 1e-6);
                double height = Math.Max(maxY - minY, 1e-6);

                double padX = width * paddingRatio;
                double padY = height * paddingRatio;

                using (ViewTableRecord view = doc.Editor.GetCurrentView())
                {
                    // 视口宽高比要跟着图纸走，否则横向拉伸后图元会跑出屏幕。
                    double aspect = view.Height > 0 && view.Width > 0 ? view.Height / view.Width : 0.75;
                    double targetWidth = width + padX * 2;
                    double targetHeight = height + padY * 2;

                    if (targetHeight / targetWidth < aspect) targetHeight = targetWidth * aspect;
                    else targetWidth = targetHeight / aspect;

                    view.CenterPoint = new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);
                    view.Width = targetWidth;
                    view.Height = targetHeight;
                    doc.Editor.SetCurrentView(view);
                }

                try { doc.Editor.UpdateScreen(); } catch { }

                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["message"] = message,
                    ["synchronous"] = true
                };
                return Ok(tool, CadJson.Serialize(data));
            }
            catch (Exception ex)
            {
                return Failure(tool, "改视图失败: " + ex.Message);
            }
        }

        /// <summary>改视图失败时的兜底：退回原生 ZOOM 命令（异步，会在结果里标注）。</summary>
        private static McpExecutionResult FallbackZoom(Document doc, string command, string tool,
                                                       string what, string reason)
        {
            try
            {
                doc.SendStringToExecute(command, true, false, false);
                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["message"] = what + "已交给原生命令执行（异步）。",
                    ["synchronous"] = false,
                    ["note"] = "同步改视图失败（" + reason + "），已退回原生命令；"
                        + "该命令是排队执行的，紧接其后的截图可能拍不到新视图。"
                };
                return Ok(tool, CadJson.Serialize(data));
            }
            catch (Exception ex)
            {
                return Failure(tool, "缩放失败: " + ex.Message);
            }
        }

        // ================================================================== 图纸转 PNG

        private static McpExecutionResult HandleCaptureScreen(string argsJson, Document doc, Transaction tr)
        {
            try
            {
                string viewLabel = "全图范围";
                string scope = "extents";
                string handle = null;
                double minX = double.NaN, minY = double.NaN, maxX = double.NaN, maxY = double.NaN;
                int width = 0, height = 0, dpi = 150;

                if (!string.IsNullOrWhiteSpace(argsJson))
                {
                    var args = CadJson.Deserialize(argsJson);
                    scope = CadJson.GetString(args, "scope");
                    handle = CadJson.GetString(args, "handle");
                    minX = CadJson.GetDouble(args, "min_x", double.NaN);
                    minY = CadJson.GetDouble(args, "min_y", double.NaN);
                    maxX = CadJson.GetDouble(args, "max_x", double.NaN);
                    maxY = CadJson.GetDouble(args, "max_y", double.NaN);
                    width = CadJson.GetInt(args, "width", 0);
                    height = CadJson.GetInt(args, "height", 0);
                    dpi = CadJson.GetInt(args, "dpi", 150);
                }

                if (dpi < 50) dpi = 50;
                if (dpi > 600) dpi = 600;

                bool hasWindow = !double.IsNaN(minX) && !double.IsNaN(minY)
                                 && !double.IsNaN(maxX) && !double.IsNaN(maxY);

                if (!string.IsNullOrWhiteSpace(handle))
                {
                    // 图元范围：先把屏幕同步聚焦过去，再按该范围出图。
                    HandleZoomEntity(argsJson, doc, tr);
                    Extents3d entExtents = ResolveEntityExtents(doc, tr, handle);
                    minX = entExtents.MinPoint.X;
                    minY = entExtents.MinPoint.Y;
                    maxX = entExtents.MaxPoint.X;
                    maxY = entExtents.MaxPoint.Y;
                    scope = "window";
                    viewLabel = "图元范围: " + handle;
                }
                else if (hasWindow)
                {
                    if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
                    if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }
                    HandleZoomWindow(argsJson, doc, tr);
                    scope = "window";
                    viewLabel = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "指定范围 ({0:F1},{1:F1})-({2:F1},{3:F1})", minX, minY, maxX, maxY);
                }
                else if (string.Equals(scope, "window", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure("cad_view_capture_screen",
                        "scope='window' 需要提供 min_x, min_y, max_x, max_y 坐标。");
                }
                else if (string.Equals(scope, "current", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(scope, "display", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "current";
                    viewLabel = "当前视图";
                }
                else if (string.Equals(scope, "layout", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "layout";
                    viewLabel = "当前布局";
                }
                else
                {
                    // 默认：全图范围。先把屏幕同步 fit 到全图，用户看到的和出图范围一致。
                    HandleZoomExtents(argsJson, doc, tr);
                    scope = "extents";
                    viewLabel = "全图范围";
                }

                string outPath = RenderToPng(doc, tr, scope, minX, minY, maxX, maxY, width, height, dpi);

                int actualWidth, actualHeight;
                ReadPngSize(outPath, out actualWidth, out actualHeight);
                if (actualWidth <= 0) actualWidth = width;
                if (actualHeight <= 0) actualHeight = height;

                // 契约：ChatCore 的自动视觉校验只认 images[].path，
                // 结构与字段名都不能改（AgentSession.Evidence.ExtractCapturedImagePaths）。
                // view 字段是给对话界面做标签用的（ChatCore 的图片提取器优先读 view/angle，
                // 读不到才退回文件名），顺手带上，界面上的缩略图就有可读标题。
                var data = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["images"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["view"] = viewLabel,
                            ["path"] = outPath
                        }
                    },
                    ["imagePath"] = outPath,      // 兼容旧调用方
                    ["width"] = actualWidth,
                    ["height"] = actualHeight,
                    ["scope"] = viewLabel,
                    ["render"] = "plot",
                    ["note"] = "已按范围用打印引擎出图为 PNG（矢量渲染，不含窗口遮挡）。"
                };

                return new McpExecutionResult
                {
                    ToolName = "cad_view_capture_screen",
                    Json = CadJson.Serialize(data),
                    MutatesDrawing = false,
                    // 显式声明图片：ChatCore 会把它们交给视觉模型并把视觉事实注入上下文。
                    // 只把路径写进 JSON 是不够的——模型读到的只是一串字符串，等于没看见图。
                    ImagePaths = new[] { outPath }
                };
            }
            catch (Exception ex)
            {
                return Failure("cad_view_capture_screen", "图纸转 PNG 失败: " + ex.Message);
            }
        }

        /// <summary>解析图元范围；退化图元给一个最小窗口，避免打印时除零。</summary>
        private static Extents3d ResolveEntityExtents(Document doc, Transaction tr, string hexHandle)
        {
            if (!long.TryParse(hexHandle, System.Globalization.NumberStyles.HexNumber, null, out long hVal))
                throw new InvalidOperationException("无效的十六进制句柄: " + hexHandle);

            if (!doc.Database.TryGetObjectId(new Handle(hVal), out ObjectId id) || id.IsNull)
                throw new InvalidOperationException("未找到图元: " + hexHandle);

            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null)
                throw new InvalidOperationException("该句柄不是图元: " + hexHandle);

            Extents3d extents = ent.GeometricExtents;
            double width = extents.MaxPoint.X - extents.MinPoint.X;
            double height = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (width < 1e-6 || height < 1e-6)
            {
                // 单点文字/零长线：给一个最小可视窗口。
                double padX = width < 1e-6 ? 5.0 : 0.0;
                double padY = height < 1e-6 ? 5.0 : 0.0;
                extents = new Extents3d(
                    extents.MinPoint + new Vector3d(-padX, -padY, 0),
                    extents.MaxPoint + new Vector3d(padX, padY, 0));
            }
            return extents;
        }

        /// <summary>
        /// 用 PlotEngine 把当前布局按指定范围出成 PNG。
        /// 与屏幕截图不同：不依赖窗口可见、不受面板/对话框遮挡，范围由 PlotType 决定。
        ///
        /// 出图设置分两档：先按"目标像素 + 打印样式"精细出图；某些 AutoCAD 版本/打印设备组合下
        /// 挑栅格纸张（SetClosestMediaName 传 Pixels）或挂打印样式会直接抛 eInvalidInput
        /// （ErrorStatus.InvalidInput），这时退回与 cad_export_pdf 相同的极简设置——
        /// 设备默认纸张 + Extents/Window + 布满图纸，保证取图链路不会因为"挑纸张"整体不可用。
        /// </summary>
        private static string RenderToPng(Document doc, Transaction tr, string scope,
                                          double minX, double minY, double maxX, double maxY,
                                          int widthPx, int heightPx, int dpi)
        {
            var db = doc.Database;
            string layoutName = CadSpaces.CurrentLayoutName(doc);
            var space = CadSpaces.Find(db, tr, layoutName);
            if (space == null)
                throw new InvalidOperationException("当前布局不存在: " + layoutName);

            var layout = (Layout)tr.GetObject(space.LayoutId, OpenMode.ForRead);

            // 目标像素：未指定时按图纸尺寸 × DPI 推算。模型页常常没有页面设置（纸张 0×0），
            // 推算出来会是 200×200 这种退化尺寸——那种图视觉模型什么也读不出来，退回 1600×1200。
            bool sizeDerived = widthPx <= 0 || heightPx <= 0;
            if (sizeDerived)
            {
                var paper = layout.PlotPaperSize;
                double w = paper.X, h = paper.Y;
                if (layout.PlotPaperUnits == PlotPaperUnit.Inches)
                {
                    w *= 25.4;
                    h *= 25.4;
                }
                else if (layout.PlotPaperUnits == PlotPaperUnit.Pixels)
                {
                    widthPx = (int)Math.Round(w);
                    heightPx = (int)Math.Round(h);
                }
                if (widthPx <= 0) widthPx = (int)Math.Round(w / 25.4 * dpi);
                if (heightPx <= 0) heightPx = (int)Math.Round(h / 25.4 * dpi);

                if (widthPx < 200 || heightPx < 200)
                {
                    widthPx = 1600;
                    heightPx = 1200;
                }
            }
            if (widthPx < 200) widthPx = 200;
            if (heightPx < 200) heightPx = 200;
            if (widthPx > 8000) widthPx = 8000;
            if (heightPx > 8000) heightPx = 8000;

            var validator = PlotSettingsValidator.Current;
            string pngDevice = FindPngPlotDevice(validator);

            string outDir = CaptureDirectory();
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir,
                "cad_view_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".png");

            bool plotted = false;
            string plotError = null;

            if (pngDevice != null)
            {
                // 1. 优先尝试精细矢量打印
                try
                {
                    PlotWithBackgroundRetry(() => PlotToPng(db, layout, scope, minX, minY, maxX, maxY, widthPx, heightPx, pngDevice, outPath, fine: true));
                    plotted = FileExistsAndNonEmpty(outPath);
                }
                catch (Exception ex)
                {
                    plotError = ex.Message;
                    TryDeleteFile(outPath);
                }

                // 2. 失败后尝试极简矢量打印（默认纸张与默认样式）
                if (!plotted)
                {
                    try
                    {
                        PlotWithBackgroundRetry(() => PlotToPng(db, layout, scope, minX, minY, maxX, maxY, widthPx, heightPx, pngDevice, outPath, fine: false));
                        plotted = FileExistsAndNonEmpty(outPath);
                    }
                    catch (Exception ex)
                    {
                        plotError = ex.Message;
                        TryDeleteFile(outPath);
                    }
                }

                // 3. 若按 WindowArea 出图因驱动/坐标校验抛错，由于前序步骤已同步将视口精准缩放到该区域，
                //    直接以当前视口 Display 模式出图，100% 避开任何窗口坐标校验缺陷，依然保持高质量矢量输出。
                if (!plotted && string.Equals(scope, "window", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        PlotWithBackgroundRetry(() => PlotToPng(db, layout, "current", minX, minY, maxX, maxY, widthPx, heightPx, pngDevice, outPath, fine: false));
                        plotted = FileExistsAndNonEmpty(outPath);
                    }
                    catch (Exception ex)
                    {
                        plotError = ex.Message;
                        TryDeleteFile(outPath);
                    }
                }
            }

            // 4. 终极兜底：若本机无 PNG 打印驱动或所有矢量打印流程均失败，直接由活动视口/文档窗口捕获位图保存为 PNG。
            if (!plotted)
            {
                try
                {
                    if (TryCaptureViewportToPng(doc, outPath))
                        plotted = FileExistsAndNonEmpty(outPath);
                }
                catch { }
            }

            if (!plotted)
                throw new InvalidOperationException("出图流程已结束但未能生成有效的 PNG 图像。"
                    + (plotError != null ? " 原因：" + plotError : " 请检查当前图纸是否有可渲染内容。"));

            return outPath;
        }

        private static bool FileExistsAndNonEmpty(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                return new FileInfo(path).Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 出图包装：撞上后台打印队列占用时，等队列收尾后重试一次。
        /// 只对 eBackgroundPlotInProgress 重试，其他错误（纸张/设备/范围）直接上抛。
        /// </summary>
        private static void PlotWithBackgroundRetry(Action plot)
        {
            try
            {
                plot();
            }
            catch (Exception ex) when (ForegroundPlotScope.IsBackgroundPlotBusy(ex))
            {
                ForegroundPlotScope.WaitForBackgroundPlot(3000);
                plot();
            }
        }

        /// <summary>单次出图。fine=false 时只用设备默认纸张，与 cad_export_pdf 的设置保持一致。</summary>
        private static void PlotToPng(Database db, Layout layout, string scope,
                                      double minX, double minY, double maxX, double maxY,
                                      int widthPx, int heightPx, string pngDevice, string outPath, bool fine)
        {
            var validator = PlotSettingsValidator.Current;

            using (ForegroundPlotScope.Enter())
            using (var settings = new PlotSettings(layout.ModelType))
            using (var plotInfo = new PlotInfo())
            using (var pageInfo = new PlotPageInfo())
            using (var infoValidator = new PlotInfoValidator())
            using (PlotEngine engine = PlotFactory.CreatePublishEngine())
            {
                settings.CopyFrom(layout);

                // 设备/纸张：先挂 PNG 设备，再按目标像素挑最接近的栅格纸张。
                validator.SetPlotConfigurationName(settings, pngDevice, null);
                validator.RefreshLists(settings);

                if (fine)
                {
                    // 挑纸张/挂样式都是"尽力而为"：失败就保持设备默认纸张与默认样式，
                    // 由 ScaleToFit 把图纸铺满。让这两个可选优化把整个出图搞挂不值得。
                    //
                    // allowNonStandardSizes 必须是 false：PublishToWeb PNG.pc3（raster7.hdi）
                    // 只定义了一张固定纸张（1600×1280 px，dimensional=FALSE），要求它按任意
                    // 像素尺寸另开一张自定义纸会抛 eInvalidInput——历史取图失败最可能就断在这一步。
                    // 传 false 时会吸附到设备唯一的标准纸张，出图照常进行（尺寸以实际 PNG 为准）。
                    try
                    {
                        validator.SetPlotPaperUnits(settings, PlotPaperUnit.Pixels);
                        validator.SetClosestMediaName(settings, widthPx, heightPx, PlotPaperUnit.Pixels, false);
                    }
                    catch { }

                    // 切换打印设备可能重置样式表；把布局自己的 CTB/STB 再挂回去，
                    // 否则 PNG 的线宽/颜色会和图纸的打印样式不一致。
                    try
                    {
                        string styleSheet = layout.CurrentStyleSheet;
                        if (!string.IsNullOrWhiteSpace(styleSheet))
                            validator.SetCurrentStyleSheet(settings, styleSheet);
                    }
                    catch { }

                    try { validator.SetPlotRotation(settings, PlotRotation.Degrees000); } catch { }
                }

                // 范围类型与窗口区域先落定，再算 ScaleToFit（比例随当前范围重算）。
                if (string.Equals(scope, "window", StringComparison.OrdinalIgnoreCase))
                {
                    if (minX > maxX) { double tmp = minX; minX = maxX; maxX = tmp; }
                    if (minY > maxY) { double tmp = minY; minY = maxY; maxY = tmp; }
                    if (maxX - minX < 1.0) { minX -= 5.0; maxX += 5.0; }
                    if (maxY - minY < 1.0) { minY -= 5.0; maxY += 5.0; }

                    var windowArea = new Extents2d(minX, minY, maxX, maxY);
                    bool windowApplied = false;

                    // 顺序与容错：不同 AutoCAD 核心与驱动对先切 PlotType 还是先设 WindowArea 表现不一；
                    // 两条链路互为兜底，且若均失败则自动转当前视口 Display 出图（视口此前已同步对焦该区域）。
                    try
                    {
                        validator.SetPlotType(settings, PlotType.Window);
                        validator.SetPlotWindowArea(settings, windowArea);
                        windowApplied = true;
                    }
                    catch
                    {
                        try
                        {
                            validator.SetPlotWindowArea(settings, windowArea);
                            validator.SetPlotType(settings, PlotType.Window);
                            windowApplied = true;
                        }
                        catch { }
                    }

                    if (!windowApplied)
                    {
                        validator.SetPlotType(settings, layout.ModelType ? PlotType.Display : PlotType.Extents);
                    }

                    try { validator.SetPlotCentered(settings, true); } catch { }
                }
                else if (string.Equals(scope, "current", StringComparison.OrdinalIgnoreCase))
                {
                    validator.SetPlotType(settings,
                        layout.ModelType ? PlotType.Display : PlotType.Layout);
                    if (layout.ModelType)
                    {
                        try { validator.SetPlotCentered(settings, true); } catch { }
                    }
                }
                else if (string.Equals(scope, "layout", StringComparison.OrdinalIgnoreCase))
                {
                    // 模型空间没有"布局"可出，按全图范围处理。
                    validator.SetPlotType(settings,
                        layout.ModelType ? PlotType.Extents : PlotType.Layout);
                    if (layout.ModelType)
                    {
                        try { validator.SetPlotCentered(settings, true); } catch { }
                    }
                }
                else
                {
                    validator.SetPlotType(settings, PlotType.Extents);
                    try { validator.SetPlotCentered(settings, true); } catch { }
                }

                validator.SetUseStandardScale(settings, true);
                validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);

                plotInfo.Layout = layout.ObjectId;
                plotInfo.OverrideSettings = settings;

                infoValidator.MediaMatchingPolicy = MatchingPolicy.MatchEnabled;
                infoValidator.Validate(plotInfo);

                string documentName = Path.GetFileName(db.Filename);
                if (string.IsNullOrWhiteSpace(documentName)) documentName = "drawing";

                engine.BeginPlot(null, null);
                engine.BeginDocument(plotInfo, documentName, null, 1, true, outPath);
                engine.BeginPage(pageInfo, plotInfo, true, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                engine.EndDocument(null);
                engine.EndPlot(null);
            }
        }

        /// <summary>失败重试前清掉上一次可能留下的半成品，避免把旧文件当成新结果。</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        /// <summary>在打印设备列表里找 PNG 设备（标准安装为 PublishToWeb PNG.pc3）。</summary>
        private static string FindPngPlotDevice(PlotSettingsValidator validator)
        {
            var devices = validator.GetPlotDeviceList();
            if (devices == null) return null;
            foreach (string device in devices)
            {
                if (!string.IsNullOrWhiteSpace(device)
                    && device.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0)
                    return device;
            }
            return null;
        }

        /// <summary>读 PNG 的 IHDR 宽高；读不到时返回 0，调用方回退到请求值。</summary>
        private static void ReadPngSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var header = new byte[24];
                    if (fs.Read(header, 0, header.Length) == header.Length
                        && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                    {
                        width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                        height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 视口位图直接捕获（终极保底）：当系统未装 PNG 打印驱动或所有矢量打印流程均失败时，
        /// 直接由 AutoCAD 文档窗口客户区抓取渲染位图保存为 PNG。
        /// 前序步骤已将视口精确同步对齐到目标范围，因此该位图严格反映用户所求区域。
        /// </summary>
        private static bool TryCaptureViewportToPng(Document doc, string outPath)
        {
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    if (doc != null && doc.Window != null)
                        hwnd = doc.Window.Handle;
                }
                catch { }

                if (hwnd == IntPtr.Zero)
                {
                    try
                    {
                        using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                            hwnd = proc.MainWindowHandle;
                    }
                    catch { }
                }

                if (hwnd == IntPtr.Zero) return false;

                if (!GetClientRect(hwnd, out RECT rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                    return false;

                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w < 10 || h < 10) return false;

                using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool printed = false;
                        try
                        {
                            // 优先使用 PrintWindow 抓取完整内容（不受其它悬浮窗遮挡）
                            printed = PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                            if (!printed)
                                printed = PrintWindow(hwnd, hdc, 0);
                        }
                        catch { }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }

                        if (!printed)
                        {
                            POINT pt = new POINT { X = 0, Y = 0 };
                            if (ClientToScreen(hwnd, ref pt))
                            {
                                g.CopyFromScreen(pt.X, pt.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                                printed = true;
                            }
                        }
                    }

                    bmp.Save(outPath, ImageFormat.Png);
                }

                return FileExistsAndNonEmpty(outPath);
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        /// <summary>出图输出目录：当前对话的 Captures\（由 ChatCore 统一布局管理，受会话清理策略约束）。</summary>
        private static string CaptureDirectory()
        {
            try
            {
                string dir = CadEngineHost.CapturesDirectory;
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
            catch { }

            return Path.Combine(Path.GetTempPath(), "DwgAiCaptures");
        }

        // ================================================================== 结果

        private static McpExecutionResult Ok(string tool, string json)
            => new McpExecutionResult { ToolName = tool, Json = json, MutatesDrawing = false };

        private static McpExecutionResult Failure(string tool, string message)
            => new McpExecutionResult
            {
                ToolName = tool,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
    }
}
