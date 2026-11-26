using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadAI.Core;
using CadAI.Core.Modal;
using CadAI.Mcp.AutoCAD.Core;
using McpExecutionResult = CadAI.Core.McpExecutionResult;

namespace CadAI.Host.AutoCAD.Hosting
{
    /// <summary>
    /// DWG 工具执行器：把 Agent 循环（后台线程）的工具调用安全地落到 AutoCAD 主线程上。
    ///
    /// 四层保护，缺一层都会变成"界面一直转圈、没有任何线索"：
    ///  1. **主线程派发**：AutoCAD 的 Database 只能在主 STA 线程上碰，后台线程经 Dispatcher 切过去。
    ///  2. **调用超时**：Dispatcher.Invoke 默认无限等待。AutoCAD 一旦弹出一个 ModalAgent
    ///     白名单外的模态框，消息泵就被堵住，Invoke 永远不返回——用户只看到"执行中"。
    ///     这里用带超时的 Wait，超时后返回一条明确诊断，而不是挂死。
    ///  3. **弹窗前置检测**：用 ChatCore.Modal 的探测（与 AutoCAD 宿主同一套判据）识别模态框，
    ///     检出即拒，避免在模态循环里重入数据库。
    ///  4. **卡死看门狗 + 事务判据**：见各自方法注释。
    /// </summary>
    internal sealed class CadToolExecutor : IToolExecutor
    {
        /// <summary>
        /// 单次工具调用的等待上限。默认 300s（与 ChatCore 的默认请求超时同量级），
        /// 可由宿主选项 ai-settings.json 的 ToolCallTimeoutSeconds 调整：
        /// 大装配/整图处理可以调大，嫌"转圈太久"可以调小。
        /// </summary>
        private static TimeSpan _toolTimeout = TimeSpan.FromSeconds(AiSettings.DefaultToolCallTimeoutSeconds);

        /// <summary>后台工具的等待上限。默认 20 分钟：整图翻译是逐条串行调模型的。</summary>
        private static TimeSpan _backgroundToolTimeout = TimeSpan.FromMinutes(AiSettings.DefaultBackgroundToolTimeoutMinutes);

        /// <summary>设置窗口保存后调用，让新的超时值立即生效（无需重启 AutoCAD）。</summary>
        public static void RefreshTimeouts()
        {
            try
            {
                AiSettings settings = AiSettings.Load();
                _toolTimeout = TimeSpan.FromSeconds(
                    AiSettings.NormalizeToolCallTimeoutSeconds(settings.ToolCallTimeoutSeconds));
                _backgroundToolTimeout = TimeSpan.FromMinutes(
                    AiSettings.NormalizeBackgroundToolTimeoutMinutes(settings.BackgroundToolTimeoutMinutes));
            }
            catch
            {
                // 读设置失败就保持上一次的值（首次即默认值）。
            }
        }

        static CadToolExecutor()
        {
            RefreshTimeouts();
        }

        [ThreadStatic]
        private static CancellationToken _currentCancellationToken;

        /// <summary>当前正在执行的工具调用关联的取消令牌。</summary>
        public static CancellationToken ActiveCancellationToken => _currentCancellationToken;

        private readonly CadToolRegistry _registry;
        private readonly Dispatcher _dispatcher;
        private volatile string _recentLog = string.Empty;

        public CadToolExecutor(CadToolRegistry registry, Dispatcher dispatcher)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public IReadOnlyList<object> ToolSchemas() => _registry.GetSchemas();

        /// <summary>
        /// 全部工具都在本进程内直连执行（没有 stdio MCP 子进程），因此一律按"routed"处理：
        /// 变更标记直接取自工具结果携带的 MutatesDrawing，不再走 AutoCAD 那套外部工具推断
        /// （那套会去调 cad_state_compare / cad_document_info，对 CAD 全是无效往返）。
        /// </summary>
        public bool IsRoutedTool(string toolName) => true;

        public McpExecutionResult Execute(string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            // 后台工具（整图翻译等）：慢在 HTTP，绝不能压在 AutoCAD 主线程上。
            // 这类工具自己按阶段调度线程与事务，执行器只负责"放到后台 + 加超时"。
            if (_registry.IsBackground(toolName))
                return ExecuteBackgroundTool(toolName, argumentsJson, cancellationToken);

            if (_dispatcher.CheckAccess())
                return ExecuteInMainThread(toolName, argumentsJson);

            // Dispatcher 可能已关闭（AutoCAD 正在退出）。
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                return Failure(toolName, "AutoCAD 正在关闭，无法执行工具调用。");

            var result = new McpExecutionResult[1];
            Exception failure = null;
            DispatcherOperation operation;

            // 用 BeginInvoke + Wait 而不是 Invoke：Invoke 没有超时，主线程一旦被模态框
            // 或长命令堵住就是永久挂起，用户只看到"执行中"转圈，拿不到任何诊断。
            try
            {
                operation = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    try { result[0] = ExecuteInMainThread(toolName, argumentsJson); }
                    catch (Exception ex) { failure = ex; }
                }));
            }
            catch (Exception ex)
            {
                return Failure(toolName, "无法派发到 AutoCAD 主线程：" + ex.Message);
            }

            // 「停止」在这里能真正生效：排队中的派发被 Abort 掉，Wait 立刻返回；
            // 已经在主线程上跑起来的那一段 COM 无法中断（与超时路径同一语义——Abort 无效，
            // 只能如实报告），但框架不会再为它一直等下去。
            DispatcherOperationStatus status;
            using (cancellationToken.Register(() => { try { operation.Abort(); } catch { } }))
            {
                status = operation.Wait(_toolTimeout);
            }
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (status != DispatcherOperationStatus.Completed)
            {
                // 超时/中止：尽量取消排队中的操作（已在执行时 Abort 无效，只能如实报告）。
                try { operation.Abort(); } catch { }
                return Failure(toolName, BuildTimeoutDiagnosis(toolName));
            }

            if (failure != null)
                return Failure(toolName, "执行错误: " + failure.Message);

            return result[0] ?? Failure(toolName, "工具没有返回结果。");
        }

        /// <summary>
        /// 执行后台工具。当前就在 UI 线程时直接就地跑（此时若再开 Task 等它，
        /// 而工具又要回主线程读写数据库，就会死锁）；否则放到线程池并加超时。
        /// </summary>
        private McpExecutionResult ExecuteBackgroundTool(string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            // 后台工具是逐条串行调模型的，动辄几分钟：这里是「停止」最该生效的一类。
            // 已经跑起来的那一次调用无法从外部打断（CadToolRegistry.ExecuteBackground 自己
            // 按阶段调度线程与事务），但框架不必再等它，也不会把它当成"执行成功"。
            cancellationToken.ThrowIfCancellationRequested();
            using (StallWatchdog.Begin(toolName))
            {
                ModalDecisionService.PushTool(toolName);
                ModalDecisionService.MarkToolActivity();
                try
                {
                    if (_dispatcher.CheckAccess())
                    {
                        try { return ToChatResult(_registry.ExecuteBackground(toolName, argumentsJson)); }
                        catch (Exception ex) { return Failure(toolName, "执行错误: " + ex.Message); }
                    }

                    McpExecutionResult result = null;
                    Exception failure = null;

                    var taskToken = cancellationToken;
                    // 取消令牌挂在等待上：用户按「停止」时立即返回，不再等满 20 分钟。
                    var task = System.Threading.Tasks.Task.Run(() =>
                    {
                        _currentCancellationToken = taskToken;
                        try { result = ToChatResult(_registry.ExecuteBackground(toolName, argumentsJson)); }
                        catch (Exception ex) { failure = ex; }
                        finally { _currentCancellationToken = CancellationToken.None; }
                    }, cancellationToken);

                    if (!task.Wait((int)_backgroundToolTimeout.TotalMilliseconds, cancellationToken))
                        return Failure(toolName, BuildTimeoutDiagnosis(toolName));

                    if (failure != null)
                        return Failure(toolName, "执行错误: " + failure.Message);

                    return result ?? Failure(toolName, "工具没有返回结果。");
                }
                finally
                {
                    ModalDecisionService.PopTool();
                }
            }
        }

        private McpExecutionResult ExecuteInMainThread(string toolName, string argumentsJson)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                _recentLog = "No active AutoCAD document.";
                return Failure(toolName, "当前没有打开的 AutoCAD 图纸。");
            }

            // 弹窗前置检测。为什么必须"检出就拒"而不是等一会儿：
            // Win32 模态对话框会**继续泵消息**，所以我们这段代码本身就可能是在模态循环里被派发进来的
            // （BeginInvoke 的委托在模态期间照样会被执行）。此时再去碰 Database 属于重入，
            // 而在这条线程上 Thread.Sleep 又会把对话框自己的消息泵也堵死——连 ModalAgent 的点按钮
            // 都送不进来。正确做法是立刻返回一条可执行的诊断，让主线程回到模态循环，
            // 由 ModalAgent 去处理弹窗；模型下一轮再重试就能过。
            //
            // 探测用 ChatCore.Modal.ModalDialogProbe（与 AutoCAD 宿主同一套）：
            // 它有窗口类过滤、子孙窗口数上限、按钮枚举等判据，比自己数 Owner 链可靠得多。
            string modal = DescribeBlockingModal();
            if (modal != null)
            {
                // 看门狗自愈：若看门狗因空闲自杀或异常退出，在此处重新拉起，避免弹窗永久卡死后续工具。
                if (!ModalAgentLauncher.IsRunning())
                {
                    try { ModalAgentLauncher.Start("acad"); }
                    catch (Exception ex) { AiExceptionGuard.Log("CadToolExecutor.ModalAgentRestart", ex); }
                }

                string message = "AutoCAD 上有未处理的模态对话框，已跳过本次工具调用以免重入数据库："
                    + modal + "。弹窗看门狗（ModalAgent）已确认在运行或尝试拉起，通常会在数秒内处理它；"
                    + "请稍等片刻后重试；若长时间不消失，请在 AutoCAD 里手工处理。";
                _recentLog = "[CadToolExecutor] " + message;
                return Failure(toolName, message);
            }

            // 卡死看门狗：记录"这次工具调用从何时开始"，让弹窗决策服务能判断
            // 这个弹窗是不是正卡着一次自动化调用（决定要不要交给视觉模型）。
            using (StallWatchdog.Begin(toolName))
            {
                ModalDecisionService.PushTool(toolName);
                ModalDecisionService.MarkToolActivity();
                try
                {
                    using (doc.LockDocument())
                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        McpExecutionResult result;
                        try
                        {
                            result = ToChatResult(_registry.Execute(toolName, argumentsJson, doc, tr));
                        }
                        catch
                        {
                            // 工具抛异常：显式 Abort，把半途改动整体回滚。
                            // （不 Abort 而靠 Dispose 也会回滚，但显式写出来意图清楚，
                            //   也避免将来有人把 using 改成手工 Dispose 时语义漂移。）
                            try { tr.Abort(); } catch { }
                            throw;
                        }

                        // 提交判据：**只有工具明确声明自己改了图纸、且没有报错**才提交。
                        // 反例（旧实现只看 MutatesDrawing）：工具返回 ok:false 但已经改了实体时照样 Commit，
                        // 用户看到"失败"却发现图被改了；或者工具返回 ok:true 却漏标 MutatesDrawing 时反而不落盘。
                        if (ShouldCommit(result))
                        {
                            tr.Commit();
                        }
                        else
                        {
                            try { tr.Abort(); } catch { }
                        }

                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _recentLog = "[CadToolExecutor] " + toolName + " failed: " + ex;
                    return Failure(toolName, "执行错误: " + ex.Message);
                }
                finally
                {
                    ModalDecisionService.PopTool();
                }
            }
        }

        /// <summary>
        /// 是否提交这次事务：工具声明了变更 **且** 结果里没有 ok=false。
        /// 报错时不提交，保证"报错 = 图纸没被改"，用户不必去猜哪一半生效了。
        /// </summary>
        private static bool ShouldCommit(McpExecutionResult result)
        {
            if (result == null || !result.MutatesDrawing) return false;
            return !IsExplicitFailure(result.Json);
        }

        /// <summary>结果 JSON 里是否显式写了 ok=false / success=false。</summary>
        private static bool IsExplicitFailure(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            string text = json.ToLowerInvariant();
            return text.IndexOf("\"ok\":false", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"ok\": false", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"success\":false", StringComparison.Ordinal) >= 0
                || text.IndexOf("\"success\": false", StringComparison.Ordinal) >= 0;
        }

        // ================================================================== 模态弹窗

        /// <summary>
        /// 找当前阻塞 AutoCAD 主窗口的模态对话框，返回它的描述；没有则返回 null。
        ///
        /// 用 ChatCore.Modal 的 <see cref="ModalDialogProbe"/>（与 AutoCAD 宿主同一套判据）：
        /// 它按"主窗口被禁用 + 沿 Owner 链找本进程窗口"定位，并带窗口类过滤与
        /// 子孙窗口数上限（避免把主框架窗口本身误判成弹窗），还能枚举按钮标签。
        /// 这里只做**只读探测**，绝不点任何按钮——是否点击、点哪个始终由 ModalAgent 决定。
        /// </summary>
        private static string DescribeBlockingModal()
        {
            try
            {
                ModalDialogInfo dialog = ModalDialogProbe.Probe(ModalDialogProbe.HostProcessId());
                if (dialog == null) return null;

                string title = string.IsNullOrWhiteSpace(dialog.Title) ? dialog.ClassName : dialog.Title;
                string buttons = dialog.Labels() == null || dialog.Labels().Count == 0
                    ? "无按钮"
                    : string.Join(" / ", dialog.Labels().ToArray());

                return "\"" + title + "\"（按钮：" + buttons + "）";
            }
            catch
            {
                // 探测本身失败时按"没有弹窗"处理：宁可让调用继续（可能成功），
                // 也不要因为探测异常把整条工具链拒掉。
                return null;
            }
        }

        private static string BuildTimeoutDiagnosis(string toolName)
        {
            return "工具调用超时（" + (int)_toolTimeout.TotalSeconds + "s）未返回：" + toolName
                + "。AutoCAD 主线程可能被模态对话框或长命令阻塞。"
                + "请检查 AutoCAD 窗口上是否有待处理的对话框，处理后重试；"
                + "该上限可在「设置 → 工具调用超时（秒）」调整。";
        }

        private static McpExecutionResult ToChatResult(CadAI.Mcp.AutoCAD.Core.McpExecutionResult r)
        {
            if (r == null) return null;
            return new McpExecutionResult
            {
                ToolName = r.ToolName,
                Json = r.Json,
                MutatesDrawing = r.MutatesDrawing,
                ImagePaths = r.ImagePaths
            };
        }

        private static McpExecutionResult Failure(string toolName, string message)
        {
            return new McpExecutionResult
            {
                ToolName = toolName,
                Json = "{\"ok\":false,\"error\":\"" + (message ?? string.Empty).Replace("\\", "/").Replace("\"", "'") + "\"}",
                MutatesDrawing = false
            };
        }

        public string RecentServerLog() => _recentLog;

        private bool _hasActiveUndoMark;
        private Document _activeUndoDocument;

        /// <summary>
        /// 一轮对话开始前由 AgentSession 调用，在 AutoCAD 中开启全局 UndoMark 事务组。
        /// 单轮内的多个工具调用所提交的事务会被合并为同一个撤销步，用户按一次 Ctrl+Z 即可完全回退。
        /// </summary>
        public void BeginTurn()
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try
            {
                if (_dispatcher.CheckAccess())
                {
                    StartUndoGroup();
                }
                else
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(StartUndoGroup));
                }
            }
            catch { }
        }

        private void StartUndoGroup()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    object acadDoc = ResolveAcadDocument(doc);
                    if (acadDoc != null)
                    {
                        System.Reflection.MethodInfo start = acadDoc.GetType().GetMethod("StartUndoMark");
                        if (start != null)
                        {
                            start.Invoke(acadDoc, null);
                            // 只有真正调用成功才置位：GetMethod 返回 null 时
                            // 旧代码仍然置位，后续 EndTurn 会去闭合一个不存在的标记。
                            _hasActiveUndoMark = true;
                            _activeUndoDocument = doc;
                        }
                        else
                        {
                            _recentLog = "[CadToolExecutor] StartUndoMark not available on this AutoCAD version.";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _recentLog = "[CadToolExecutor] StartUndoMark failed: " + ex.Message;
            }
        }

        private static object ResolveAcadDocument(Document doc)
        {
            if (doc == null) return null;
            try
            {
                // AutoCAD .NET 中 AcadDocument 是 DocumentExtension 的扩展方法 (定义在 acmgd.dll)
                Type extType = Type.GetType("Autodesk.AutoCAD.ApplicationServices.DocumentExtension, acmgd")
                    ?? doc.GetType().Assembly.GetType("Autodesk.AutoCAD.ApplicationServices.DocumentExtension");
                if (extType != null)
                {
                    System.Reflection.MethodInfo getAcadDoc = extType.GetMethod("GetAcadDocument", new[] { typeof(Document) });
                    if (getAcadDoc != null)
                    {
                        object res = getAcadDoc.Invoke(null, new object[] { doc });
                        if (res != null) return res;
                    }
                }
            }
            catch { }

            try
            {
                return doc.GetType().GetProperty("AcadDocument")?.GetValue(doc, null);
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 一轮对话结束后由 AgentSession 统一调用，闭合当前 UndoMark 撤销标记组。
        /// </summary>
        public void EndTurn()
        {
            if (!_hasActiveUndoMark) return;
            _hasActiveUndoMark = false;
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try
            {
                if (_dispatcher.CheckAccess())
                {
                    EndUndoGroup();
                }
                else
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(EndUndoGroup));
                }
            }
            catch { }
        }

        private void EndUndoGroup()
        {
            // 锁定在同一个 Document 实例上闭合，杜绝对话中切图纸导致原图纸撤销栈悬空、新图纸误闭合的隐患。
            var doc = _activeUndoDocument;
            _activeUndoDocument = null;
            if (doc == null) return;

            try
            {
                if (!doc.IsDisposed)
                {
                    object acadDoc = ResolveAcadDocument(doc);
                    if (acadDoc != null)
                    {
                        System.Reflection.MethodInfo end = acadDoc.GetType().GetMethod("EndUndoMark");
                        end?.Invoke(acadDoc, null);
                    }
                }
            }
            catch (Exception ex)
            {
                _recentLog = "[CadToolExecutor] EndUndoMark failed: " + ex.Message;
            }
        }

        /// <summary>
        /// 提取当前 AutoCAD 活动文档的轻量上下文摘要（耗时 < 5ms）：
        /// 文件名、只读属性、当前空间（Model/Paper）、用户光标是否已预选实体。
        /// </summary>
        public string GetActiveContextSummary()
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return null;
            try
            {
                string summary = null;
                if (_dispatcher.CheckAccess())
                {
                    summary = BuildContextSummary();
                }
                else
                {
                    var op = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Func<string>(BuildContextSummary));
                    if (op.Wait(TimeSpan.FromMilliseconds(500)) == DispatcherOperationStatus.Completed)
                        summary = op.Result as string;
                }
                return summary;
            }
            catch { return null; }
        }

        private string BuildContextSummary()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return "No active AutoCAD document.";

            var sb = new System.Text.StringBuilder();
            sb.AppendFormat("Active Drawing: \"{0}\"", System.IO.Path.GetFileName(doc.Name));
            if (doc.IsReadOnly) sb.Append(" (ReadOnly)");

            string space = doc.Database.TileMode ? "ModelSpace" : "PaperSpace";
            sb.AppendFormat(", Space: {0}", space);

            try
            {
                var ed = doc.Editor;
                var sel = ed.SelectImplied();
                if (sel.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK && sel.Value != null && sel.Value.Count > 0)
                {
                    sb.AppendFormat(", User Pre-Selected: {0} entity/entities", sel.Value.Count);
                }
            }
            catch { }

            return sb.ToString();
        }

        public void Dispose()
        {
            EndTurn();
        }
    }
}
