using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CadAI.ModalAgent
{
    /// <summary>
    /// ModalAgent：进程外模态弹窗自动应答工具。
    /// 用 WinEvent（EVENT_OBJECT_SHOW, OUTOFCONTEXT）+ 轮询兜底发现指定宿主进程的弹窗，
    /// 按配置规则自动点击按钮或关闭窗口；白名单未命中时可选上报宿主由 AI 决策。
    /// 独立进程运行，自身崩溃不影响宿主。
    ///
    /// 用法：ModalAgent.exe [--debug] [--config 路径] [--process 进程名]
    ///   --debug                  显示调试窗口（含执行记录）
    ///   --config <path>          指定配置文件（默认 exe 同目录 modal-agent.json）
    ///   --process <name>         覆盖配置里的宿主进程名
    ///   --pipe <name>            覆盖桥接管道名（空 = 关闭桥接）
    ///   --capture-dir <path>     覆盖弹窗截图目录
    ///   --dialog-log <path>      覆盖结构化弹窗事件日志路径
    ///   --decision-timeout <ms>  覆盖等待宿主决策的超时
    ///   --no-bridge              强制关闭桥接（只走白名单规则）
    /// </summary>
    internal static class Program
    {
        private static AgentConfig _config = new();
        private static WindowResponder? _responder;
        private static DebugForm? _debugForm;
        private static FileStream? _logStream;
        private static StreamWriter? _logWriter;
        /// <summary>日志写入锁：扫描线程与桥接线程会并发调用 WriteLog。</summary>
        private static readonly object _logLock = new object();

        /// <summary>唯一标识：互斥量/关机事件都带这个后缀，不同项目、不同软件互不干扰。</summary>
        private static string _identity = "ModalAgent";

        /// <summary>上次动作时间（收到弹窗并处理时刷新）——空闲退出兜底用。</summary>
        private static long _lastActivityTicks = DateTime.UtcNow.Ticks;

        private static EventWaitHandle? _shutdownEvent;

        [STAThread]
        private static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            // 默认配置按 exe 同目录解析。用相对路径会落到子进程继承的 AutoCAD 工作目录，
            // 结果是配置文件静默失效、只用内置默认值（详见 AgentConfig.Load 注释）。
            string configPath = Path.Combine(AppContext.BaseDirectory, "modal-agent.json");
            bool debugFlag = false;
            string? processOverride = null;
            string? pipeOverride = null;
            string? captureOverride = null;
            string? dialogLogOverride = null;
            int? decisionTimeoutOverride = null;
            bool bridgeDisabled = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] is "--debug" or "-d") debugFlag = true;
                else if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] is "--process" or "-p" && i + 1 < args.Length) processOverride = args[++i];
                else if (args[i] is "--pipe" && i + 1 < args.Length) pipeOverride = args[++i];
                else if (args[i] is "--capture-dir" && i + 1 < args.Length) captureOverride = args[++i];
                else if (args[i] is "--dialog-log" && i + 1 < args.Length) dialogLogOverride = args[++i];
                else if (args[i] is "--decision-timeout" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedTimeout)) decisionTimeoutOverride = parsedTimeout;
                else if (args[i] is "--no-bridge") bridgeDisabled = true;
                else if (args[i] is "--id" or "-i" && i + 1 < args.Length) _identity = args[++i];
            }

            _config = AgentConfig.Load(configPath);
            string loadError = AgentConfig.LastLoadError;
            _config.Identity = _identity;
            if (processOverride != null) _config.ProcessNames = new List<string> { processOverride };
            if (debugFlag) _config.Debug = true;
            if (pipeOverride != null) _config.PipeName = pipeOverride;
            if (captureOverride != null) _config.CaptureDirectory = captureOverride;
            if (dialogLogOverride != null) _config.DialogLogPath = dialogLogOverride;
            if (decisionTimeoutOverride.HasValue) _config.DecisionTimeoutMs = decisionTimeoutOverride.Value;
            if (bridgeDisabled) _config.EnableDialogBridge = false;

            // 空值即"用默认"：管道名按标识推导，事件日志与截图目录给绝对路径，
            // 绝不依赖当前工作目录（宿主拉起时 CWD 是宿主主程序的目录）。
            if (string.IsNullOrWhiteSpace(_config.PipeName)) _config.PipeName = "CADAI.ModalAgent." + _identity;
            if (string.IsNullOrWhiteSpace(_config.DialogLogPath))
                _config.DialogLogPath = Path.Combine(AppContext.BaseDirectory, "modal-agent.dialogs.jsonl");
            if (string.IsNullOrWhiteSpace(_config.CaptureDirectory))
            {
                bool isCad = _identity.IndexOf("cad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             _identity.IndexOf("dwg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             _config.ProcessNames.Any(p => p.IndexOf("acad", StringComparison.OrdinalIgnoreCase) >= 0);
                string productFolder = isCad ? "CADAI" : "CADAI";
                string workspace = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    productFolder);

                // 与宿主/工具面同一套布局：优先落当前对话的 Captures\Dialogs\；
                // 读不到 .current 时退到 Shared\Captures\Dialogs\。
                // 绝不回退到工作区根目录的 Captures\（否则根目录又出现一份）。
                string? conversation = ReadCurrentConversation(workspace);
                _config.CaptureDirectory = conversation != null
                    ? Path.Combine(conversation, "Captures", "Dialogs")
                    : Path.Combine(workspace, "Shared", "Captures", "Dialogs");
            }

            OpenLogFile();
            WriteLog($"启动：id={_identity} 配置={configPath}");
            WriteLog(loadError.Length == 0
                ? $"配置已加载：Rules={_config.Rules.Count} Mode={_config.Mode} Debug={_config.Debug}"
                : $"配置未生效（使用内置默认值）：{loadError}");

            Func<DialogRequest, DialogDecision?>? decide = null;
            if (_config.EnableDialogBridge)
            {
                // 管道名与超时在这里取快照，避免后台线程读到运行期被改动的配置。
                string pipeName = _config.PipeName;
                int timeoutMs = _config.DecisionTimeoutMs;
                decide = request => DialogBridgeClient.Request(pipeName, request, timeoutMs);
                WriteLog($"桥接已启用：pipe={pipeName} 决策超时={timeoutMs}ms 截图目录={_config.CaptureDirectory}");
            }
            else
            {
                WriteLog("桥接已关闭：仅使用白名单规则。");
            }

            _responder = new WindowResponder(_config, m => { _debugForm?.Log(m); WriteLog(m); TouchActivity(); }, decide);

            // 唯一标识兜底：上次残留的同标识实例收到关机事件后优雅退出，本实例接管。
            _shutdownEvent = AcquireIdentity(_identity);

            if (_config.Debug)
            {
                _debugForm = new DebugForm(_identity);
                _debugForm.Shown += (_, _) => StartMonitoring();
                Application.Run(_debugForm);
            }
            else
            {
                StartMonitoring();
                Application.Run();   // 纯消息泵，无窗口
            }
        }

        /// <summary>
        /// 按标识建立单实例互斥量。若发现同标识残留实例，先置位它的关机事件等它退出
        /// （互斥量被释放），避免一次启动两个相同身份的 agent 互相点弹窗。
        /// 关机事件名 Global\ModalAgent_{id}_shutdown 只属于这个标识，
        /// 关闭别的项目的 agent 不会误伤。
        /// </summary>
        /// <summary>
        /// 读 <c>&lt;workspace&gt;\Conversations\.current</c> 得到当前对话目录。
        /// 只接受 Conversations 下的直接子目录，防止指针被写成越权路径。
        /// </summary>
        private static string? ReadCurrentConversation(string workspace)
        {
            try
            {
                string conversations = Path.Combine(workspace, "Conversations");
                string pointer = Path.Combine(conversations, ".current");
                if (!File.Exists(pointer)) return null;

                string name = File.ReadAllText(pointer).Trim();
                if (string.IsNullOrWhiteSpace(name)) return null;

                string candidate = Path.GetFullPath(Path.Combine(conversations, name));
                string root = Path.GetFullPath(conversations);
                if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
                return Directory.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static EventWaitHandle AcquireIdentity(string identity)
        {
            string shutdownName = @"Global\ModalAgent_" + identity + "_shutdown";
            var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, shutdownName, out bool createdNew);

            if (!createdNew)
            {
                WriteLog("发现同标识残留实例，通知其退出…");
                shutdown.Set();
                Thread.Sleep(1500); // 给残留实例退出时间
                shutdown.Reset();
            }
            return shutdown;
        }

        private static void TouchActivity()
            => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        private static void StartMonitoring()
        {
            // 定时器跑在 WinForms UI 线程（有消息泵），WinEvent 回调也在该线程投递。
            // PollIntervalMs<=0 = 纯事件驱动：不做 EnumWindows 轮询，只保留 1s 的
            // 关机/空闲看护（旧实现用 Math.Max(100,…) 把它悄悄变成 100ms 轮询）。
            bool pollingEnabled = _config.PollIntervalMs > 0;
            var scanTimer = new System.Windows.Forms.Timer
            {
                Interval = pollingEnabled ? Math.Max(100, _config.PollIntervalMs) : 1000
            };
            uint currentPid = 0;
            WinEventHook? hook = null;

            scanTimer.Tick += (_, _) =>
            {
                try
                {
                    if (currentPid == 0 || !IsProcessAlive(currentPid))
                    {
                        hook?.Dispose();
                        hook = null;
                        currentPid = FindTargetProcess();
                        if (currentPid != 0)
                        {
                            string name = Process.GetProcessById((int)currentPid).ProcessName;
                            string message = $"已挂接宿主：{name} (PID {currentPid})";
                            _debugForm?.SetStatus(message);
                            _debugForm?.Log(message);
                            WriteLog(message);
                            hook = new WinEventHook(currentPid, hwnd => _responder?.TryRespond(hwnd, currentPid));
                            if (!hook.Start())
                            {
                                _debugForm?.Log("[警告] WinEvent 挂钩失败，仅靠轮询。");
                            }
                        }
                        else
                        {
                            _debugForm?.SetStatus($"等待宿主进程（{string.Join(", ", _config.ProcessNames)}）…");
                        }
                    }

                    if (currentPid != 0 && pollingEnabled)
                    {
                        EnumWindows((hwnd, lParam) =>
                        {
                            if (_responder?.TryRespond(hwnd, currentPid) == true) TouchActivity();
                            return true;
                        }, IntPtr.Zero);
                    }

                    // 兜底一：宿主发来关机信号（批量任务结束/下次启动接管），优雅退出。
                    if (_shutdownEvent != null && _shutdownEvent.WaitOne(0))
                    {
                        WriteLog("收到关机信号，退出。");
                        _debugForm?.SetStatus("已收到关机信号，退出。");
                        Application.Exit();
                    }

                    // 兜底二：空闲太久（没有处理过任何弹窗）自动退出，避免长期滞留后台。
                    DateTime lastActivity = new DateTime(
                        Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                    if (_config.IdleExitMinutes > 0 &&
                        (DateTime.UtcNow - lastActivity).TotalMinutes >= _config.IdleExitMinutes)
                    {
                        WriteLog($"空闲超过 {_config.IdleExitMinutes} 分钟，自动退出。");
                        Application.Exit();
                    }
                }
                catch (Exception ex)
                {
                    WriteLog("扫描异常：" + ex.Message);
                }
            };
            scanTimer.Start();
        }

        private static uint FindTargetProcess()
        {
            foreach (string name in _config.ProcessNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0) return (uint)processes[0].Id;
                }
                finally
                {
                    // GetProcessesByName 返回的每个 Process 都持有系统句柄；
                    // 每 250ms 调一次且不 Dispose，常驻一小时会累积上千个句柄。
                    foreach (Process process in processes) process.Dispose();
                }
            }
            return 0;
        }

        private static bool IsProcessAlive(uint pid)
        {
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                    return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void OpenLogFile()
        {
            try
            {
                string path = string.IsNullOrWhiteSpace(_config.LogFilePath)
                    ? Path.Combine(AppContext.BaseDirectory, "modal-agent.log")
                    : _config.LogFilePath;
                _logStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _logWriter = new StreamWriter(_logStream, new System.Text.UTF8Encoding(true)) { AutoFlush = true };
                _logWriter.WriteLine($"===== ModalAgent 启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            }
            catch { }
        }

        private static void WriteLog(string message)
        {
            // 扫描线程与桥接线程会并发写同一个 StreamWriter；不加锁时
            // 交错写入会丢行或写坏 UTF-8 多字节序列。
            lock (_logLock)
            {
                try { _logWriter?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message); } catch { }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    }
}
