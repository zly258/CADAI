using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using CadAI.Core;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// 卡死看门狗（第三层兜底）。
    ///
    /// 定位：**前两层都没管到时**的兜底，而不是它们的替代品。
    ///   L1 白名单规则（ModalAgent 常驻观察，0 延迟）
    ///   L2 AI 桥接（ModalAgent 上报，规则未命中时立刻决策）
    ///   L3 本看门狗（调用卡住超时后，主动检查宿主是否有模态框）
    ///
    /// L3 存在的理由只有一条但很硬：**L1/L2 都依赖 ModalAgent 这个进程活着**。
    /// 现场日志里出现过 "未找到 ModalAgent.exe"，那次弹窗完全无人处理，
    /// 整条工具链静默挂死。看门狗在 Add-in 进程内，不依赖任何人。
    ///
    /// 语义上刻意保守：
    ///   * 只有"超过阈值仍未返回"才动作，正常路径零行为变化；
    ///   * 只处理常驻通道**没有**处理过的窗口（靠 ModalDecisionService 的决策记忆判断），
    ///     所以不会和 L1/L2 抢着点同一个框；
    ///   * 决策复用 L2 的同一条链路与同一套安全闸门（危险词、按钮必须真实存在）；
    ///   * 检测不到弹窗时**不动作**，只给出"疑似 COM/网络阻塞"的明确诊断——
    ///     绝不把"卡住"直接当成"一定有弹窗"。
    ///   * 全程异常自吞，绝不把异常抛回工具调用线程。
    ///
    /// 阈值默认 45s（用户可用 ai-settings.json 的 ModalStallSeconds 调整）：
    /// 明显高于 ModalAgent 的决策等待窗口（默认 20s，先给常驻通道机会），
    /// 又明显低于 MCP 客户端判僵死的 180s（避免把可恢复的调用直接判死）。
    /// </summary>
    public static class StallWatchdog
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// 阈值缓存时长：读设置要解密一次 DPAPI 密钥，不值得每次工具调用都做一遍；
        /// 1 分钟的滞后对"卡多久算卡住"这种量级完全够用。
        /// </summary>
        private static readonly TimeSpan ThresholdCacheTtl = TimeSpan.FromMinutes(1);

        private static readonly object ThresholdSync = new object();
        private static TimeSpan _cachedThreshold = TimeSpan.FromSeconds(AiSettings.DefaultModalStallSeconds);
        private static DateTime _cachedThresholdAt = DateTime.MinValue;

        /// <summary>当前生效的卡死判定阈值（来自用户设置，读取失败则沿用上一次的值）。</summary>
        internal static TimeSpan StallThreshold()
        {
            lock (ThresholdSync)
            {
                if (DateTime.UtcNow - _cachedThresholdAt < ThresholdCacheTtl) return _cachedThreshold;
                try
                {
                    int seconds = AiSettings.NormalizeModalStallSeconds(AiSettings.Load().ModalStallSeconds);
                    _cachedThreshold = TimeSpan.FromSeconds(seconds);
                }
                catch
                {
                    // 设置读不出来（文件损坏/权限）：沿用上一次的值，绝不因此放弃看门狗。
                }
                _cachedThresholdAt = DateTime.UtcNow;
                return _cachedThreshold;
            }
        }

        /// <summary>同一操作重复输出诊断的最小间隔（卡很久时不要每秒刷一行）。</summary>
        private static readonly TimeSpan NoteThrottle = TimeSpan.FromSeconds(60);

        /// <summary>同一窗口两次代答尝试之间的冷却（跨工具调用生效）。</summary>
        private static readonly TimeSpan HandleCooldown = TimeSpan.FromMinutes(2);

        private static readonly object Sync = new object();
        private static readonly Dictionary<IntPtr, DateTime> HandledAt = new Dictionary<IntPtr, DateTime>();

        /// <summary>
        /// 开启一个被监视的操作作用域。Dispose 即停止监视。
        /// 用法：<c>using (StallWatchdog.Begin("tool.name")) { ...原有逻辑... }</c>
        /// </summary>
        public static IDisposable Begin(string operation)
        {
            var scope = new Scope(operation, StallThreshold());
            scope.Start();
            return scope;
        }

        private static bool TryClaim(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            lock (Sync)
            {
                if (HandledAt.TryGetValue(hwnd, out DateTime at) && DateTime.UtcNow - at < HandleCooldown) return false;
                HandledAt[hwnd] = DateTime.UtcNow;

                if (HandledAt.Count > 128)
                {
                    DateTime cutoff = DateTime.UtcNow - HandleCooldown;
                    foreach (IntPtr key in new List<IntPtr>(HandledAt.Keys))
                    {
                        if (HandledAt[key] < cutoff) HandledAt.Remove(key);
                    }
                }
                return true;
            }
        }

        private static void Log(string message)
        {
            try { AiExceptionGuard.Log("StallWatchdog", new InvalidOperationException(message)); } catch { }
        }

        private static void Report(string message)
        {
            Log(message);
            ModalActivityLog.Add(message);
        }

        private sealed class Scope : IDisposable
        {
            private readonly string _operation;
            private readonly TimeSpan _threshold;
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
            private DateTime _lastNoteAt = DateTime.MinValue;
            private bool _stallLogged;

            public Scope(string operation, TimeSpan threshold)
            {
                _operation = string.IsNullOrWhiteSpace(operation) ? "tool" : operation;
                // 阈值在作用域开始时取快照：中途改设置不影响正在进行的那次判定。
                _threshold = threshold;
            }

            public void Start()
            {
                // 打一次"自动化活动"点：弹窗决策只代答自动化期间的弹窗。
                // 批量翻译不走工具执行器，正是靠这里（以及每秒刷新）把活动窗口撑开，
                // 否则翻译过程中弹的框会被判成"用户手动弹窗"而拒绝代答。
                ModalDecisionService.MarkToolActivity();
                Task.Run(() => RunAsync(_cancellation.Token));
            }

            public void Dispose()
            {
                // 只取消、不释放：Task.Delay 仍挂在令牌的注册回调上，释放可能打断它。
                // 这里没用到 CancelAfter/定时器，不显式释放不会泄漏内核对象。
                try { _cancellation.Cancel(); } catch { }
            }

            private async Task RunAsync(CancellationToken token)
            {
                var stopwatch = Stopwatch.StartNew();
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(CheckInterval, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch { return; }

                    try
                    {
                        // 长操作持续刷新活动窗口，让"自动化正在进行"这个判断始终成立。
                        ModalDecisionService.MarkToolActivity();
                        if (stopwatch.Elapsed < _threshold) continue;

                        if (!_stallLogged)
                        {
                            _stallLogged = true;
                            Log("「" + _operation + "」已运行 " + (int)stopwatch.Elapsed.TotalSeconds
                                + " 秒仍未返回，开始检查是否被宿主模态框阻塞。");
                        }
                        Investigate(stopwatch.Elapsed);
                    }
                    catch (Exception ex)
                    {
                        // 看门狗自身绝不能把异常带回工具调用线程。
                        Log("看门狗异常：" + ex.Message);
                    }
                }
            }

            private void Investigate(TimeSpan elapsed)
            {
                ModalDialogInfo dialog = ModalDialogProbe.Probe(ModalDialogProbe.HostProcessId());
                if (dialog == null)
                {
                    // 没有弹窗本身就是重要诊断：不是弹窗阻塞，而是真卡住。
                    Note(null, elapsed, "未检测到宿主模态框，疑似 COM/网络阻塞，与弹窗无关。");
                    return;
                }

                string labels = string.Join(" / ", dialog.Labels());
                if (dialog.Buttons.Count == 0)
                {
                    // 读不到按钮就没法安全代答（可能是 WPF/自绘弹窗）：只诊断，不动作。
                    Note(dialog.Title, elapsed, "读不到可点按钮，保持不动交给人工。");
                    return;
                }

                string hwndKey = ModalDecisionService.HwndKey(dialog.Hwnd);

                // 常驻通道（L1 规则 / L2 AI 桥接）已经处理或询问过这个窗口：不重复动作。
                if (ModalDecisionService.WasRecentlyDecided(hwndKey))
                {
                    Note(dialog.Title, elapsed, "常驻通道已在处理，看门狗不重复动作（按钮 " + labels + "）。");
                    return;
                }

                if (!TryClaim(dialog.Hwnd)) return;

                // 正文读不到时补一张截图：决策服务只在"文本没有依据"时才把它交给视觉模型
                // （与 ModalAgent 通道的策略一致：文本优先、截图兜底）。
                // 这一步正是看门狗存在的意义——ModalAgent 不在场时，没人能提供这张图。
                string screenshot = string.Empty;
                if (string.IsNullOrWhiteSpace(dialog.Body) || dialog.Body.Trim().Length <= 8)
                {
                    screenshot = ModalDialogCapture.TryCapture(dialog.Hwnd, ChatCoreHostConfig.ModalCaptureDirectoryOrDefault())
                                 ?? string.Empty;
                }

                ModalDialogDecision decision = new ModalDecisionService(Log).Decide(new ModalDialogRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Hwnd = hwndKey,
                    Process = ChatCoreHostConfig.HostProcessNameOrDefault(),
                    Title = dialog.Title,
                    Body = dialog.Body,
                    Buttons = dialog.Labels(),
                    Screenshot = screenshot
                });

                if (decision == null || string.IsNullOrWhiteSpace(decision.Button))
                {
                    string reason = decision == null ? "无决策" : decision.Reason;
                    Note(dialog.Title, elapsed, "未获得可用决策，保持不动（" + reason + "）。");
                    return;
                }

                // 等模型的这几秒里弹窗可能已消失或换成另一层：重新探测并确认仍是同一个窗口。
                ModalDialogInfo current = ModalDialogProbe.Probe(ModalDialogProbe.HostProcessId());
                if (current == null || current.Hwnd != dialog.Hwnd)
                {
                    Note(dialog.Title, elapsed, "模态框在决策返回前已变化，放弃点击。");
                    return;
                }

                IntPtr target = ModalDialogProbe.ResolveButton(current, decision.Button);
                if (target == IntPtr.Zero)
                {
                    Note(dialog.Title, elapsed, "决策按钮「" + decision.Button + "」在当前弹窗中不存在，放弃点击。");
                    return;
                }

                bool clicked = ModalDialogProbe.ClickButton(target);
                if (clicked)
                    Note(dialog.Title, elapsed, "看门狗代答（" + decision.Source + "）", decision.Button);
                else
                    Note(dialog.Title, elapsed, "点击「" + decision.Button + "」失败。");
            }

            /// <summary>
            /// 记录一次看门狗结论：写诊断日志与对话活动，并登记到弹窗事件汇
            /// （批量翻译报告据此说明"这张图期间卡住过、处理了什么"）。
            /// 按 60 秒节流，避免同一个未解决的现场被反复记录。
            /// </summary>
            /// <param name="dialogTitle">弹窗标题；为空表示这次卡住与弹窗无关。</param>
            /// <param name="action">实际动作（被点击的按钮）；空 = 没有动作。</param>
            private void Note(string dialogTitle, TimeSpan elapsed, string message, string action = "")
            {
                if (DateTime.UtcNow - _lastNoteAt < NoteThrottle) return;
                _lastNoteAt = DateTime.UtcNow;

                Report("[" + _operation + " 卡住 " + (int)elapsed.TotalSeconds + "s] " + message);
                ModalEventFeed.Publish("看门狗", string.IsNullOrWhiteSpace(dialogTitle) ? _operation : dialogTitle,
                    action, message);
            }
        }
    }
}
