using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using CadAI.Core;
using CadAI.Mcp.AutoCAD.Core;

namespace CadAI.Host.AutoCAD.UI
{
    public static class DwgChatHostService
    {
        private static bool _installed;
        private static AiChatWindow _activeWindow;
        private static readonly CadToolRegistry _registry = new CadToolRegistry();

        internal static CadToolRegistry Registry => _registry;

        /// <summary>
        /// 宿主侧工具扩展点。主插件（AutoCADAI）曾把自己的翻译工具域挂进来（2026-10-23 拆分后翻译工具已退出对话工具面），
        /// 这样 AutoCADMcp 不需要反向依赖翻译引擎（那会形成循环引用）。
        /// 每个提供者在注册表上跑一次；CadToolRegistry.Register 对同名工具是幂等的。
        /// </summary>
        internal static readonly List<Action<CadToolRegistry>> ExtraToolProviders =
            new List<Action<CadToolRegistry>>();

        /// <summary>
        /// 注册宿主扩展工具（由主插件 AutoCADAI 经友元程序集调用）。
        /// 可在 EnsureInstalled 之前调用（先攒着，安装时统一执行），也可在之后调用（立即补登记）。
        /// </summary>
        internal static void AddToolProvider(Action<CadToolRegistry> provider)
        {
            if (provider == null) return;
            if (ExtraToolProviders.Contains(provider)) return;

            ExtraToolProviders.Add(provider);
            if (_installed) provider(_registry);
        }

        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;

            var env = new Hosting.CadHostEnvironment();
            var dispatcher = ResolveDispatcher();

            // 后台工具（整图翻译等）要靠它把"必须回主线程"的短阶段切回去。
            // Dispatcher.Invoke 在本来就是该 Dispatcher 线程时会就地同步执行，不会自锁。
            _registry.UiInvoke = action =>
            {
                if (action == null) return;
                if (dispatcher.CheckAccess()) action();
                else dispatcher.Invoke(action);
            };

            ChatCoreHost.Install(
                env,
                () => new Hosting.CadToolExecutor(_registry, dispatcher),
                runtimeConfigPathOverride: Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
                    "cadai.runtime.json"));

            // 装配 AutoCADMcp 宿主路径委托（使 AutoCADMcp 无需直接引用 ChatCore.dll）
            CadEngineHost.RootDirectoryProvider = () => WorkspaceLayout.RootDirectory;
            CadEngineHost.CurrentConversationDirectoryProvider = () => WorkspaceLayout.CurrentConversationDirectory;
            CadEngineHost.ExportsDirectoryProvider = () => WorkspaceLayout.ExportsDirectory;
            CadEngineHost.CapturesDirectoryProvider = () => WorkspaceLayout.CapturesDirectory;

            // 注入 AutoCAD 系统提示词及工具意图判定前缀
            ApplyAutoCadHostConfig();

            // 注册 AutoCADMcp 核心工具
            CadAI.Mcp.AutoCAD.Tools.CadToolBootstrap.RegisterAllTools(_registry);

            // 宿主扩展工具（翻译引擎、术语库等由主插件提供）
            foreach (var provider in ExtraToolProviders.ToArray())
            {
                try { provider(_registry); }
                catch { }
            }

            // 启动弹窗看门狗（针对 AutoCAD 模态弹窗）
            if (!Hosting.ModalAgentLauncher.Start("acad"))
            {
                AiExceptionGuard.Log("DwgChat.ModalAgentLauncher.Start",
                    new InvalidOperationException("ModalAgent failed to start; modal dialogs may block automation."));
            }

            // 启动弹窗桥接服务端：ModalAgent 白名单未命中的弹窗会上报到这里，
            // 由当前对话模型判断"点哪个按钮"（危险按钮一律拒绝，见 ModalDecisionSafety）。
            StartModalBridge();
        }

        private static bool _modalBridgeStarted;

        /// <summary>
        /// 起弹窗决策桥接。
        ///
        /// 这是 ModalAgent 的**第二级**能力：第一级是它的白名单规则（0 延迟、确定性），
        /// 未命中时把弹窗快照（标题+正文+按钮+截图）上报到这条命名管道，由宿主回一个决策。
        /// 在此之前我们没有服务端，未命中的弹窗只能等 800ms 短连接失败后放弃——
        /// 也就是说 ModalAgent 有一半能力是闲置的。
        ///
        /// 服务端跑在后台线程上，绝不碰 UI 线程：模态框弹出时 AutoCAD 主线程正在模态消息循环里，
        /// 决策链路一旦占用主线程就是死锁。
        /// </summary>
        private static void StartModalBridge()
        {
            if (_modalBridgeStarted) return;
            _modalBridgeStarted = true;
            try
            {
                Hosting.ModalBridgeHost.Start();
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("DwgChat.StartModalBridge", ex);
            }
        }

        /// <summary>
        /// 取 UI 线程的 Dispatcher。
        ///
        /// 为什么要小心：<see cref="Dispatcher.CurrentDispatcher"/> 在**没有 Dispatcher 的线程**上
        /// 会新建一个。如果 EnsureInstalled 哪天被后台线程先调到，捕获到的就是一个没人泵消息的
        /// Dispatcher，之后每一次工具调用都会永久挂起，而且毫无线索。
        /// 这里优先用 WPF Application 的 Dispatcher（一定属于 UI 线程）；
        /// 退回 CurrentDispatcher 时留一条日志，便于事后定位。
        /// 兜底由 CadToolExecutor 的调用超时负责——挂死会变成一条可读的错误，而不是无声等待。
        /// </summary>
        private static Dispatcher ResolveDispatcher()
        {
            var application = System.Windows.Application.Current;
            if (application != null && application.Dispatcher != null)
                return application.Dispatcher;

            var dispatcher = Dispatcher.CurrentDispatcher;

            // 兜底路径下做一个廉价的健全性检查：后台线程（线程池是 MTA）绝不是 UI 线程。
            // 真的在那儿捕获到 Dispatcher，就等于捕获了一个没人泵消息的队列，
            // 后续每次工具调用都会走 CadToolExecutor 的超时分支。留一条日志好定位。
            try
            {
                if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
                {
                    AiExceptionGuard.Log("DwgChat.ResolveDispatcher",
                        new InvalidOperationException(
                            "EnsureInstalled was called from an MTA (non-UI) thread. "
                            + "Tool calls will fall back to the executor timeout instead of running."));
                }
            }
            catch { }

            return dispatcher;
        }

        private static void ApplyAutoCadHostConfig()
        {
            try
            {
                var config = ChatCoreHostConfig.Current;
                if (config == null) return;

                string prompt = LoadEmbeddedText("AutoCADAI.SystemPrompt.AutoCad.txt");
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    config.SystemPromptTemplate = prompt;
                }
                else
                {
                    AiExceptionGuard.Log("DwgChat.ApplyAutoCadHostConfig",
                        new InvalidOperationException("Missing embedded resource: AutoCADAI.SystemPrompt.AutoCad.txt"));
                }

                // ── 界面文案：不换的话 AutoCAD 用户会看到 AutoCAD 的欢迎卡与提示 ──
                config.ProductDisplayName = "CADAI";
                config.HostProductName = "AutoCAD";
                config.UiProductTitle = "CAD 智能对话";
                // 弹窗决策与看门狗要靠它标识"卡住的是哪个宿主的自动化调用"。
                config.HostProcessName = "acad";
                // 弹窗截图目录**不在这里钉死**：它属于"当前对话"，由
                // ChatCoreHostConfig.ModalCaptureDirectoryOrDefault() 在每次拉起 ModalAgent /
                // 抓弹窗时按 Conversations\.current 解析。启动时还没有新会话，提前算会落到
                // Shared 或上一个会话；两个产品各有独立工作区，本来也不会串味。
                string welcome = LoadEmbeddedText("AutoCADAI.Welcome.AutoCad.txt");
                if (!string.IsNullOrWhiteSpace(welcome))
                {
                    config.WelcomeText = welcome;
                }
                else
                {
                    AiExceptionGuard.Log("DwgChat.ApplyAutoCadHostConfig",
                        new InvalidOperationException("Missing embedded resource: AutoCADAI.Welcome.AutoCad.txt"));
                }
                config.ImageFactPromptHint =
                    "Extract only visual facts relevant to the current AutoCAD drawing task: "
                    + "drawn geometry, dimension values, annotation and note text, layer/linetype cues, "
                    + "title-block and table contents, layout/viewport arrangement, error messages, or visible UI state. ";
                // 附件选择器只列 CAD 相关格式：给 AutoCAD 用户列出 *.ipt/*.iam 只会造成困惑。
                config.AttachmentFileFilter =
                    "图纸 / 图片|*.dwg;*.dxf;*.dwt;*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff"
                    + "|所有文件|*.*";

                // ── 工具面收窄（2026-10-16）──
                // 不给策略等于 86 个 cad_* 工具每轮常驻上下文：token 白花、模型还容易挑错。
                // 规则见 CadToolScopePolicy：域名并集 + 无命中回退全量 + 核心工具常开。
                config.ToolScopePolicy = new Hosting.CadToolScopePolicy();

                // ── 工具语义分类 ──
                config.NonMutationToolPrefixes = new[]
                {
                    "cad_doc_active", "cad_doc_list",
                    "cad_layout_list",
                    "cad_drawing_statistics", "cad_drawing_health_check",
                    "cad_layer_list",
                    "cad_text_style_list", "cad_text_font_audit",
                    "cad_selection_get",
                    "cad_query_", "cad_get_", "cad_count_",
                    "cad_view_", "cad_text_search", "cad_block_list", "cad_block_get_",
                    "cad_excel_read", "cad_excel_list", "cad_pdf_read", "cad_pdf_get",
                    "cad_skill_"
                };

                config.MutationToolPrefixes = new[]
                {
                    "cad_doc_save",
                    "cad_layout_switch", "cad_layout_create", "cad_layout_rename",
                    "cad_layer_create", "cad_layer_set_",
                    "cad_text_style_set", "cad_text_font_repair",
                    "cad_selection_set",
                    "cad_draw_", "cad_dimension_", "cad_entity_",
                    "cad_text_modify", "cad_text_batch_replace", "cad_text_merge_",
                    "cad_text_align", "cad_text_distribute",
                    "cad_block_set_",
                    "cad_excel_write", "cad_excel_export", "cad_pdf_merge", "cad_pdf_split",
                    "cad_execute_command", "cad_batch_execute", "cad_audit_purge", "cad_export_pdf"
                };

                // 会显著改变"看起来什么样"的变更：这些完成后值得自动截一张图做视觉校验。
                // 刻意**不**把每一条 cad_draw_* 都放进来——画一根线就上传一张整窗截图去问视觉模型，
                // 代价远大于收益（AutoCAD 侧踩过这个坑：一半时间耗在重复截图上）。
                // 这里只覆盖"成批改变图面观感"的动作。
                config.VisualMutationToolPrefixes = new[]
                {
                    "cad_text_merge_",
                    "cad_text_align",
                    "cad_text_distribute",
                    "cad_text_batch_replace",
                    "cad_entity_delete",
                    "cad_layout_create",
                    "cad_dimension_"
                };

                // 图纸侧写入的正确性靠回读图纸数据验证，不跑"模型状态对比"（那是 AutoCAD 的零件/装配概念）。
                // 这里把全部 CAD 变更都归为"仅图纸变更"，避免 ChatCore 去调 cad_state_compare。
                config.DrawingOnlyMutationPrefixes = new[] { "cad_" };
                config.DrawingOnlyMutationNames = new string[0];

                // 模型在正文里说"我来调用 cad_xxx"却没真发 tool_call 时的识别标记。
                config.UninvokedIntentToolMarkers = new[] { "cad_" };

                // ── 视觉审查：换成 CAD 的截图工具与契约 ──
                config.VisualCaptureToolName = "cad_view_capture_screen";
                config.VisualCaptureArguments = "{\"dpi\":150}";
                // CAD 图纸永远有内容可看，"有没有实体"这道守卫是纯浪费（它还会去调 AutoCAD 的探测工具）。
                config.VisualReviewGuardEnabled = false;
                config.VisualReviewGuardToolName = null;
                // CAD 没有"复核已完成"这个独立工具（截图就是复核），留空即永不匹配。
                config.VisualReviewToolName = null;

                // ── 状态检查点：CAD 没有这个概念，整体关掉 ──
                // 图纸的正确性靠回读实体/统计验证，不存在"零件模型状态对比"。
                // 不关掉的话，ChatCore 会去调 cad_state_compare 并把 "Unknown tool" 塞进上下文。
                config.StateCheckpointEnabled = false;
                config.StateCheckpointToolName = null;
                config.NoStateCheckpointToolNames = new string[0];

                // ── 图纸变更边界：让 CAD 的变更也能拿到 [Mutation boundary] ──
                // 不配的话 NeedsDrawingReview 只认 cad_draw_*，CAD 的变更永远拿不到
                // 这条"改完了就别顺手做无关的自动审图/排版"的边界提示。
                config.DrawingReviewMutationPrefixes = new[]
                {
                    "cad_draw_", "cad_dimension_", "cad_text_", "cad_entity_",
                    "cad_layout_create", "cad_block_set_", "cad_layer_set_",
                    "cad_text_font_repair", "cad_audit_purge"
                };
                // 保存与出图本身不是"改了图要审"，豁免掉。
                config.DrawingReviewExemptNames = new[]
                {
                    "cad_doc_save", "cad_export_pdf"
                };
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("DwgChat.ApplyAutoCadHostConfig", ex);
            }
        }

        private static string LoadEmbeddedText(string resourceName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        AiExceptionGuard.Log("DwgChat.LoadEmbeddedText",
                            new InvalidOperationException("Missing embedded resource: " + resourceName));
                        return null;
                    }
                    using (var reader = new StreamReader(stream))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("DwgChat.LoadEmbeddedText", ex);
                return null;
            }
        }

        /// <summary>AI 对话任务是否正在执行（设置窗口与关窗确认都要看它）。</summary>
        public static bool IsChatTaskRunning
        {
            get
            {
                try
                {
                    return _activeWindow != null && _activeWindow.View.IsTaskRunning;
                }
                catch { return false; }
            }
        }

        /// <summary>设置保存后即时同步到已打开的对话窗口。</summary>
        public static void ApplySettings(AiSettings settings)
        {
            if (settings == null) return;
            try { _activeWindow?.View?.ApplySettings(settings); } catch { }
        }

        /// <summary>
        /// 开启/切换 AI 对话助手（独立浮动窗口模式）。
        /// </summary>
        public static void ToggleChatWindow()
        {
            EnsureInstalled();

            if (_activeWindow != null)
            {
                if (_activeWindow.IsVisible)
                {
                    if (_activeWindow.WindowState == System.Windows.WindowState.Minimized)
                    {
                        _activeWindow.WindowState = System.Windows.WindowState.Normal;
                    }
                    _activeWindow.Activate();
                    return;
                }
                _activeWindow.Show();
                _activeWindow.Activate();
                return;
            }

            _activeWindow = new AiChatWindow();
            _activeWindow.AttachOwner(AcadMainWindowHandle());
            _activeWindow.Closed += (s, e) => _activeWindow = null;
            _activeWindow.Show();
            _activeWindow.Activate();
        }

        /// <summary>
        /// 宿主退出生命周期清理：关闭独立对话窗口。
        /// </summary>
        internal static void Close()
        {
            try
            {
                if (_activeWindow != null && _activeWindow.IsVisible)
                {
                    _activeWindow.Close();
                }
            }
            catch { }
            finally
            {
                _activeWindow = null;
            }
        }

        private static void ShowMessage(string text, System.Windows.MessageBoxImage icon)
        {
            try
            {
                System.Windows.MessageBox.Show(text, CadAI.Core.ChatCoreHostConfig.UiProductTitleOrDefault(),
                    System.Windows.MessageBoxButton.OK, icon);
            }
            catch { }
        }

        /// <summary>
        /// AutoCAD 主窗口句柄，用作 WPF 对话框的 Owner。
        /// 不走 AutoCAD 托管 API（本版本 Application 上没有 MainWindow，取它要引 COM Interop）：
        /// 进程主窗口句柄对 acad.exe 这种单主窗 GUI 进程足够可靠；取不到就返回 Zero（无 Owner，无害）。
        /// </summary>
        private static IntPtr AcadMainWindowHandle()
        {
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return process.MainWindowHandle;
                }
            }
            catch { return IntPtr.Zero; }
        }
    }
}
