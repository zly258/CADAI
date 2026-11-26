using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// ModalAgent（进程外弹窗应答工具）的启动/停止管理——宿主无关版本。
    ///
    /// 与宿主的全部约定都通过参数传入，不在本类写死任何宿主名：
    /// - <paramref name="identity"/>：唯一标识，进入关机事件名 Global\ModalAgent_{identity}_shutdown
    ///   与管道名（默认推导），与其他项目/其他软件的 agent 互不干扰；
    /// - <paramref name="targetProcessName"/>：agent 监视的宿主进程名（如 "AutoCAD"、"acad"）；
    /// - <paramref name="pipeName"/>：弹窗桥接管道名。ModalDialogBridgeServer 用同名监听，两边必须一致。
    ///
    /// exe 路径解析顺序：runtime.json 的 ModalAgentPath →
    /// 程序集目录\ModalAgent\ModalAgent.exe → 程序集目录\ModalAgent.exe → 仓库 .build\bin\ModalAgent。
    /// runtime.json 位置可经 <see cref="RuntimeConfigPath"/> 覆盖（默认在 LogDirectory 旁找）。
    /// </summary>
    public static class ModalAgentLauncher
    {
        private static readonly object SyncRoot = new object();
        private static Process _process;
        private static string _identity = "CADAI";
        private static bool _startedOnce;
        private static string _pipeName;
        private static string _runtimeConfigPath;
        private static int _activeLeases;
        private static bool _hostPinned;

        /// <summary>覆盖 runtime.json 的查找位置（完整路径）。空 = 优先用 ChatCoreHost.RuntimeConfigPathOverride，最后回落到 LogDirectory 旁的 cadai.runtime.json。</summary>
        public static string RuntimeConfigPath
        {
            get => _runtimeConfigPath;
            set => _runtimeConfigPath = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>当前 identity（默认 "CADAI"；宿主在 Start 前可通过 <see cref="Configure"/> 改）。</summary>
        public static string Identity => _identity;

        /// <summary>弹窗桥接管道名：显式设置优先，否则按 identity 推导。</summary>
        public static string PipeName => _pipeName ?? ("CADAI.ModalAgent." + _identity);

        private static string ShutdownEventName => @"Global\ModalAgent_" + _identity + "_shutdown";

        /// <summary>
        /// 调整默认 identity / 管道名 / runtime.json 位置。传入 null 的字段保持原值。
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// 已经 Start 过之后再改 identity/管道名：旧 agent 持有的关机事件与桥接管道都是按旧名
        /// 创建的，改完 Stop/IsRunning 摸不到旧实例、新实例也不知道旧管道——会双 agent 并存
        /// 互不感知。这是配置错误，直接抛而不是静默错配。
        /// </exception>
        public static void Configure(string identity = null, string pipeName = null, string runtimeConfigPath = null)
        {
            bool changesIdentityOrPipe =
                (!string.IsNullOrWhiteSpace(identity) && !string.Equals(identity, _identity, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(pipeName) && !string.Equals(pipeName, PipeName, StringComparison.Ordinal));
            if (changesIdentityOrPipe && _startedOnce)
                throw new InvalidOperationException(
                    "ModalAgentLauncher.Configure 不能在启动后修改 identity/pipeName（当前 identity=" + _identity
                    + "）。旧实例的关机事件与管道都按旧名创建，改了会导致双 agent 并存。");
            if (!string.IsNullOrWhiteSpace(identity)) _identity = identity;
            if (!string.IsNullOrWhiteSpace(pipeName)) _pipeName = pipeName;
            if (!string.IsNullOrWhiteSpace(runtimeConfigPath)) _runtimeConfigPath = runtimeConfigPath;
        }

        /// <summary>
        /// 同标识的 ModalAgent 是否已在运行。
        /// 判据是它启动时创建并一直持有的关机事件对象：agent 退出即释放最后一个句柄，
        /// 命名事件随之销毁，因此"能 OpenExisting 到"就等于"进程还活着"。
        /// </summary>
        public static bool IsRunning()
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShutdownEventName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 弹窗截图目录（优先显式配置，否则工作区 Captures\Dialogs）。
        /// 走 ChatCore 配置与环境而不是自行拼路径，保证跨宿主一致。
        /// </summary>
        public static string CaptureDirectory
        {
            get
            {
                try
                {
                    string configured = ChatCoreHostConfig.ModalCaptureDirectoryOrDefault();
                    if (!string.IsNullOrWhiteSpace(configured)) return configured;
                }
                catch { }

                try
                {
                    var environment = ChatCoreHost.Environment;
                    if (environment != null && !string.IsNullOrWhiteSpace(environment.WorkspaceRoot))
                        return AiWorkspace.DialogCapturesDirectory;
                }
                catch { }
                return null;
            }
        }

        /// <summary>
        /// 启动 ModalAgent。已在运行则直接复用（幂等），需要重启时先通知旧实例退出。
        /// 返回是否可用。
        /// </summary>
        /// <summary>
        /// 获取一个作用域租约（供批量翻译等临时任务使用）：
        /// 若调用前看门狗已由智能助手常驻拉起，则租约释放时不终止看门狗；
        /// 仅当看门狗由本租约首次拉起且无其他活动租约/常驻宿主时，Dispose 才通知退出。
        /// </summary>
        public static IDisposable AcquireLease(string targetProcessName, bool debug = false)
        {
            bool wasAlreadyRunning;
            lock (SyncRoot)
            {
                wasAlreadyRunning = IsRunning() || _hostPinned;
                Interlocked.Increment(ref _activeLeases);
            }
            StartCore(targetProcessName, debug, pinForHost: false);
            return new LeaseScope(wasAlreadyRunning);
        }

        private sealed class LeaseScope : IDisposable
        {
            private readonly bool _wasAlreadyRunning;
            private int _disposed;

            public LeaseScope(bool wasAlreadyRunning)
            {
                _wasAlreadyRunning = wasAlreadyRunning;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                int remaining = Interlocked.Decrement(ref _activeLeases);
                if (remaining <= 0 && !_wasAlreadyRunning && !_hostPinned)
                {
                    Stop();
                }
            }
        }

        public static bool Start(string targetProcessName, bool debug = false)
        {
            _hostPinned = true;
            return StartCore(targetProcessName, debug, pinForHost: true);
        }

        private static bool StartCore(string targetProcessName, bool debug, bool pinForHost)
        {
            lock (SyncRoot)
            {
                try
                {
                    // 幂等是必须的：工具执行器随每条聊天消息创建，每次都 Stop+Start 会让新实例
                    // 在 AcquireIdentity 里白等 1.5 秒（旧实例还没退干净），这段时间没有任何进程
                    // 在看弹窗，而且 WinEvent 钩子被反复重建。debug 是显式请求，才强制重启。
                    if (!debug && IsRunning()) return true;

                    StopCore();

                AiSettings currentSettings = null;
                try { currentSettings = AiSettings.Load(); } catch { }
                if (currentSettings != null && !currentSettings.ModalAgentEnabled)
                    return false;

                string exePath = ResolveExePath();
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    AiExceptionGuard.Log("ModalAgentLauncher.Start",
                        new InvalidOperationException("未找到 ModalAgent.exe（检查 runtime.json 的 ModalAgentPath 或部署目录）。"));
                    return false;
                }

                string workingDirectory = Path.GetDirectoryName(exePath);
                var arguments = new StringBuilder();
                arguments.Append("--id ").Append(_identity);
                arguments.Append(" --process ").Append(Quote(targetProcessName));
                arguments.Append(" --pipe ").Append(Quote(PipeName));

                // 优先使用用户在设置界面保存的自定义弹窗规则文件（%APPDATA%\CADAI\AutoCAD\modal-agent.user.json）
                string userConfigPath = ResolveUserConfigPath();
                if (!string.IsNullOrWhiteSpace(userConfigPath) && File.Exists(userConfigPath))
                {
                    arguments.Append(" --config ").Append(Quote(userConfigPath));
                }
                else if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    string hostConfig = Path.Combine(workingDirectory, "modal-agent." + _identity.ToLowerInvariant() + ".json");
                    arguments.Append(" --config ").Append(Quote(File.Exists(hostConfig) ? hostConfig : Path.Combine(workingDirectory, "modal-agent.json")));
                }

                string captureDirectory = CaptureDirectory;
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                    arguments.Append(" --capture-dir ").Append(Quote(captureDirectory));

                if (debug) arguments.Append(" --debug");

                var info = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(workingDirectory)) info.WorkingDirectory = workingDirectory;

                _process = Process.Start(info);
                _startedOnce = true;
                return _process != null;
            }
                catch (Exception ex)
                {
                    AiExceptionGuard.Log("ModalAgentLauncher.Start", ex);
                    return false;
                }
            }
        }

        /// <summary>用户自定义弹窗规则文件路径（落在宿主用户数据目录）。</summary>
        public static string ResolveUserConfigPath()
        {
            try
            {
                string dir = ChatCoreHost.Environment?.UserDataDirectory;
                if (string.IsNullOrWhiteSpace(dir)) dir = ProductDataPaths.AutoCadUserData;
                return Path.Combine(dir, "modal-agent.user.json");
            }
            catch { return null; }
        }

        /// <summary>
        /// 将设置界面中的 ModalAgent 开关与自定义规则合并写入用户配置文件，并重启看门狗使其立即生效。
        /// </summary>
        public static void ApplyUserSettings(AiSettings settings)
        {
            if (settings == null) return;
            try
            {
                string userPath = ResolveUserConfigPath();
                if (!string.IsNullOrWhiteSpace(userPath))
                {
                    string dir = Path.GetDirectoryName(userPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    // 读取默认规则作为基底（剥离单行注释后解析），再追加用户自定义规则
                    var payload = new Dictionary<string, object>
                    {
                        ["ProcessNames"] = new[] { ChatCoreHostConfig.HostProcessNameOrDefault() },
                        ["Mode"] = "whitelist",
                        ["Debug"] = false,
                        ["MaxDialogChildWindows"] = 100,
                        ["EnableDialogBridge"] = settings.ModalAiBridgeEnabled,
                        ["DecisionTrigger"] = "immediate",
                        ["DecisionTimeoutMs"] = 20000,
                        ["CaptureDialogImages"] = true,
                        ["DecisionDenyWords"] = ModalDecisionSafety.DefaultDenyWords,
                        ["ExpanderButtons"] = ModalDecisionSafety.DefaultExpanderLabels
                    };

                    var rules = new List<Dictionary<string, object>>();
                    // 先加入用户自定义规则（优先级高于内置默认规则）
                    if (settings.ModalCustomRules != null)
                    {
                        foreach (ModalRuleItem item in settings.ModalCustomRules)
                        {
                            if (item == null || string.IsNullOrWhiteSpace(item.Markers)) continue;
                            var markers = item.Markers
                                .Split(new[] { ',', '，', ';', '；', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0)
                                .ToList();
                            if (markers.Count == 0) continue;
                            rules.Add(new Dictionary<string, object>
                            {
                                ["Name"] = string.IsNullOrWhiteSpace(item.Name) ? "自定义规则" : item.Name.Trim(),
                                ["Match"] = string.IsNullOrWhiteSpace(item.Match) ? "Any" : item.Match.Trim(),
                                ["Action"] = string.IsNullOrWhiteSpace(item.Action) ? "ClickPositive" : item.Action.Trim(),
                                ["Markers"] = markers
                            });
                        }
                    }

                    // 保留基础无害 AutoCAD 弹窗规则
                    rules.Add(new Dictionary<string, object>
                    {
                        ["Name"] = "已知无害弹窗→确定",
                        ["Match"] = "Any",
                        ["Action"] = "ClickPositive",
                        ["Markers"] = new[] { "字体替换", "字体缺失", "missingfont", "substitutefont", "旧版本dwg", "desktopconnect", "样式冲突" }
                    });
                    payload["Rules"] = rules;

                    string json = AgentJson.New().Serialize(payload);
                    File.WriteAllText(userPath, json, Encoding.UTF8);
                }

                // 若当前已初始化宿主，立即应用启停状态
                Stop();
                if (settings.ModalAgentEnabled)
                {
                    Start(ChatCoreHostConfig.HostProcessNameOrDefault());
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("ModalAgentLauncher.ApplyUserSettings", ex);
            }
        }

        /// <summary>命令行参数引用：路径可能含空格（用户名/ProgramData 自定义目录）。</summary>
        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", string.Empty) + "\"";

        /// <summary>
        /// 通知 ModalAgent 退出（置位其关机事件；找不到就是没在跑，忽略）。
        /// 置位后等它真正释放事件再返回：否则紧接着的 Start() 会把"正在退出"误判成
        /// "已在运行"而跳过启动，结果是这一轮根本没有 agent 在看弹窗（只能等看门狗兜底）。
        /// 子进程按 250ms 轮询响应，正常几十毫秒就退干净，这里的上限只为兜住异常情况。
        /// </summary>
        public static void Stop()
        {
            lock (SyncRoot)
            {
                _hostPinned = false;
                Interlocked.Exchange(ref _activeLeases, 0);
                StopCore();
            }
        }

        private static void StopCore()
        {
            try
            {
                using var shutdown = EventWaitHandle.OpenExisting(ShutdownEventName);
                shutdown.Set();
            }
            catch { }

            for (int attempt = 0; attempt < 15 && IsRunning(); attempt++)
            {
                Thread.Sleep(100);
            }
            _process = null;
        }

        /// <summary>
        /// exe 路径解析。candidates 追加项在前（宿主可给部署专属位置），追加项都找不到
        /// 才走默认三步：程序集目录旁 → 仓库 .build\bin → runtime.json。
        /// </summary>
        private static string ResolveExePath()
        {
            // 1) runtime.json 的 ModalAgentPath（位置可被 RuntimeConfigPath 覆盖）。
            string runtimePath = ReadRuntimeModalAgentPath();
            if (!string.IsNullOrWhiteSpace(runtimePath) && File.Exists(runtimePath)) return runtimePath;

            // 2) 程序集目录旁的常见位置。
            string assemblyDir = Path.GetDirectoryName(typeof(ModalAgentLauncher).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(assemblyDir, "ModalAgent", "CadAI.ModalAgent.exe"),
                    Path.Combine(assemblyDir, "CadAI.ModalAgent.exe"),
                    Path.Combine(assemblyDir, "ModalAgent", "ModalAgent.exe"),
                    Path.Combine(assemblyDir, "ModalAgent.exe")
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }

                // 3) 开发机：仓库 .build\bin\CadAI.ModalAgent（程序集目录向上回溯到仓库根）。
                var directory = new DirectoryInfo(assemblyDir);
                for (int depth = 0; depth < 6 && directory != null; depth++, directory = directory.Parent)
                {
                    string candidate = Path.Combine(directory.FullName, ".build", "bin", "CadAI.ModalAgent", "CadAI.ModalAgent.exe");
                    if (File.Exists(candidate)) return candidate;
                    string legacy = Path.Combine(directory.FullName, ".build", "bin", "ModalAgent", "ModalAgent.exe");
                    if (File.Exists(legacy)) return legacy;
                }
            }
            return null;
        }

        private static string ReadRuntimeModalAgentPath()
        {
            try
            {
                string path = _runtimeConfigPath;
                // 宿主启动时注入的 runtime.json 路径（AutoCAD: cadai.runtime.json，
                // AutoCAD: cadai.runtime.json）。优先用它，避免共享核心写死 AutoCAD 文件名。
                if (string.IsNullOrWhiteSpace(path)) path = ChatCoreHost.RuntimeConfigPathOverride;
                if (string.IsNullOrWhiteSpace(path))
                {
                    path = ChatCoreHost.RequireEnvironment() is { } environment
                        ? Path.Combine(environment.LogDirectory, "..", "cadai.runtime.json")
                        : string.Empty;
                    path = Path.GetFullPath(path);
                }
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                // 轻量提取 "ModalAgentPath": "..."，避免为单个可选项引入反序列化依赖。
                const string key = "\"ModalAgentPath\"";
                int keyIndex = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0) return null;
                int quoteStart = json.IndexOf('"', keyIndex + key.Length);
                if (quoteStart < 0) return null;
                int quoteEnd = json.IndexOf('"', quoteStart + 1);
                if (quoteEnd <= quoteStart) return null;
                return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
            }
            catch { return null; }
        }
    }
}
