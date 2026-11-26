using System.Runtime.InteropServices;
using System.Text;

using CadAI.Core.Modal;

namespace CadAI.ModalAgent
{
    /// <summary>
    /// 窗口分类与自动应答：只读目标进程窗口结构 + 发送 BM_CLICK / WM_CLOSE。
    /// 全部用户32消息操作都是跨进程投递——目标崩溃不影响本进程，反之亦然。
    ///
    /// 防误杀流水线（顺序固定）：
    ///   进程过滤 → 窗口类过滤 → 无标题过滤 → 自身窗口过滤 → 文件选择器保护
    ///   → 规则表（首条命中即执行）→ standard 模式的单选提示兜底
    ///   → 白名单未命中且启用桥接时上报宿主 AI 决策 → 默认不动作。
    ///
    /// 关于桥接：决策是**异步**的（线程池 + 超时），扫描线程立即返回，
    /// 因此上报永远不会拖慢或卡住 WinEvent/轮询。桥接的任何失败（宿主没在听、
    /// AI 超时、返回的按钮不存在、命中危险语义）都回落为"不动作"，
    /// 所以启用桥接不会让行为比旧版更激进。
    ///
    /// 线程模型：扫描/事件回调在 ModalAgent 的 UI 线程；桥接决策在线程池线程。
    /// 共享状态（_attempts / _pending / _nextAskAt）一律经 _stateLock 访问。
    /// </summary>
    internal sealed class WindowResponder
    {
        private const uint BM_CLICK = 0x00F5;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_COMMAND = 0x0111;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int IDOK = 1, IDYES = 6, IDNO = 7, IDCLOSE = 8, IDCONTINUE = 11;

        /// <summary>同一窗口两次桥接询问之间的冷却时间，避免宿主一直不回包时反复上报。</summary>
        private static readonly TimeSpan BridgeCooldown = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 宿主明确"这次不该问"（例如判定为用户手动弹窗）时的短冷却：
        /// 这种回包没花钱、也没做决定，等自动化真正开始后应当很快能再问一次。
        /// </summary>
        private static readonly TimeSpan BridgeCooldownShort = TimeSpan.FromSeconds(15);

        /// <summary>
        /// 日志节流：扫描是 250ms 一次的轮询，一个持续存在的未命中窗口（例如 AutoCAD 主框架）
        /// 会把文本日志和事件日志刷爆。同一 (类型, 标题) 在这个间隔内只记一次。
        /// </summary>
        private static readonly TimeSpan EmitThrottle = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LogThrottle = TimeSpan.FromSeconds(60);

        private readonly AgentConfig _config;
        private readonly Action<string> _log;
        private readonly Func<DialogRequest, DialogDecision?>? _decide;
        private readonly object _stateLock = new();
        private readonly Dictionary<IntPtr, int> _attempts = new();
        private readonly HashSet<IntPtr> _pending = new();
        private readonly Dictionary<IntPtr, DateTime> _nextAskAt = new();
        private readonly Dictionary<string, DateTime> _lastEmit = new();
        private readonly Dictionary<string, DateTime> _lastLog = new();

        /// <param name="decide">
        /// 桥接决策回调（已含管道往返与超时）。为 null 时完全退化为旧版行为：
        /// 白名单未命中就是不动作。
        /// </param>
        public WindowResponder(AgentConfig config, Action<string> log, Func<DialogRequest, DialogDecision?>? decide = null)
        {
            _config = config;
            _log = log;
            _decide = decide;
        }

        /// <summary>候选窗口入口：过滤流水线 + 规则匹配。返回是否执行了动作。</summary>
        public bool TryRespond(IntPtr hwnd, uint ownerProcessId)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != ownerProcessId) return false;

            // 窗口类过滤（输入法/广播窗口等系统噪声，与标题无关）。
            var classNameBuilder = new StringBuilder(256);
            GetClassName(hwnd, classNameBuilder, 256);
            string className = classNameBuilder.ToString();
            if (_config.IgnoreWindowClasses.Any(c => className.StartsWith(c, StringComparison.OrdinalIgnoreCase))) return false;

            string title = ReadWindowText(hwnd);

            // 无标题窗口不处理；自身/受保护标题窗口跳过。
            if (_config.IgnoreUntitledWindows && title.Trim().Length == 0) return false;
            if (_config.IgnoreTitleMarkers.Any(m => title.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) return false;

            try
            {
                return ClassifyAndRespond(hwnd, title, pid);
            }
            catch (Exception ex)
            {
                _log($"[异常]「{title}」：{ex.Message}");
                Emit("error", title, string.Empty, new List<string>(), string.Empty, ex.Message);
                return false;
            }
        }

        private bool ClassifyAndRespond(IntPtr hwnd, string title, uint pid)
        {
            // 同一窗口反复出现（点击后未消失等）超过上限就放弃，防止死循环点同一个框。
            if (AttemptsOf(hwnd) >= _config.MaxAttemptsPerWindow) return false;

            // 结构判据：被禁用的窗口、子孙窗口过多的窗口都不是弹窗，是主框架。
            // ReadButtons/ReadDialogBody 都用 EnumChildWindows 递归整棵子树，落在主框架上
            // 会把工具条按钮当成"弹窗按钮"、把子弹窗正文当成"弹窗正文"——必须先挡掉。
            // enabled 先判（零成本），避免对主框架白白做一次整棵子树的遍历。
            bool enabled = IsWindowEnabled(hwnd);
            int childCount = enabled ? CountDescendants(hwnd, _config.MaxDialogChildWindows + 1) : 0;
            string? rejection = ModalDecisionSafety.DescribeCandidateRejection(enabled, childCount, _config.MaxDialogChildWindows);
            if (rejection != null)
            {
                LogThrottled("candidate:" + title, $"[跳过]「{title}」{rejection}");
                return false;
            }

            ButtonScan buttons = ReadButtons(hwnd);
            string body = ReadDialogBody(hwnd);
            string combined = Normalize(title + " " + body);

            // 文件选择器：硬保护，任何规则都不允许点它。
            // 例外：正文带保存确认文案的窗口（如「多个文件已在当前会话中更新…」）
            // 标题往往就叫「保存」，那是真业务弹窗，必须放行给规则处理。
            bool saveConfirmation =
                combined.Contains(Normalize("已在当前会话中更新")) ||
                combined.Contains(Normalize("是否要将更改保存"));
            if (!saveConfirmation && _config.FileDialogTitles.Any(m => Normalize(title).Contains(Normalize(m))))
            {
                LogThrottled("filedlg:" + title, $"[跳过]「{title}」文件选择器保护");
                Emit("skipped", title, body, buttons.Choices, string.Empty, "文件选择器保护");
                return false;
            }

            // 规则表：按声明顺序匹配，首条命中即执行。
            foreach (var rule in _config.Rules)
            {
                if (!RuleMatches(rule, title, body, combined)) continue;

                switch (rule.Action)
                {
                    case ResponderAction.Ignore:
                        _log($"[忽略]「{title}」（规则：{rule.Name}）");
                        Emit("rule-ignore", title, body, buttons.Choices, string.Empty, rule.Name);
                        return false;
                    case ResponderAction.Close:
                        SendMessageTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 250, out _);
                        BumpAttempts(hwnd);
                        _log($"[已关闭]「{title}」（规则：{rule.Name}，WM_CLOSE）");
                        Emit("rule-close", title, body, buttons.Choices, string.Empty, rule.Name, "关闭窗口");
                        return true;
                    case ResponderAction.ClickNegative:
                        // 没有识别到否定按钮时，只回退到"取消/否"全等按钮；
                        // 绝不回退到 Positive——在保存确认框里那等于点「保存」。
                        var negative = buttons.Negative != IntPtr.Zero
                            ? buttons.Negative
                            : FindExactCancelButton(buttons);
                        if (negative != IntPtr.Zero)
                        {
                            string clicked = Click(negative, hwnd, rule.Name, title);
                            Emit("rule-click", title, body, buttons.Choices, string.Empty, rule.Name + " → 否定按钮", clicked);
                            return true;
                        }
                        LogThrottled("nobtn:" + title + ":" + rule.Name, $"[跳过]「{title}」规则「{rule.Name}」无可点按钮");
                        Emit("skipped", title, body, buttons.Choices, string.Empty, "规则命中但无可点按钮");
                        return false;
                    default:
                        if (buttons.Positive != IntPtr.Zero)
                        {
                            string clicked = Click(buttons.Positive, hwnd, rule.Name, title);
                            Emit("rule-click", title, body, buttons.Choices, string.Empty, rule.Name + " → 肯定按钮", clicked);
                            return true;
                        }
                        LogThrottled("nobtn:" + title + ":" + rule.Name, $"[跳过]「{title}」规则「{rule.Name}」无可点按钮");
                        Emit("skipped", title, body, buttons.Choices, string.Empty, "规则命中但无可点按钮");
                        return false;
                }
            }

            // standard 模式兜底：唯一按钮且无否定分支的纯提示框，点确定不会改变业务结果。
            if (!_config.WhitelistOnly && buttons.Positive != IntPtr.Zero && buttons.ChoiceCount == 1 && !buttons.HasNegative)
            {
                string clicked = Click(buttons.Positive, hwnd, "单选提示兜底", title);
                Emit("rule-click", title, body, buttons.Choices, string.Empty, "单选提示兜底", clicked);
                return true;
            }

            // 白名单未命中：交给宿主 AI 决策（异步，立即返回）。
            if (BeginBridge(hwnd, title, body, buttons, pid)) return false;

            LogThrottled("unmatched:" + title,
                $"[跳过]「{title}」未命中任何规则（白名单优先，不动作）。按钮=[{string.Join(" / ", buttons.Labels)}]");
            Emit("skipped", title, body, buttons.Choices, string.Empty, "未命中任何规则");
            return false;
        }

        /// <summary>
        /// 登记一次待决弹窗并启动后台决策。返回 false 表示这次不该上报
        /// （桥接未启用、同一窗口已有待决请求、或刚问过还在冷却期）。
        /// </summary>
        private bool BeginBridge(IntPtr hwnd, string title, string body, ButtonScan buttons, uint pid)
        {
            if (_decide == null || !_config.EnableDialogBridge) return false;

            // DecisionTrigger=stall 时本进程一律不主动上报：交给宿主侧的卡死看门狗，
            // 只在"某个工具调用确实卡住了"的时候由宿主主动决策。
            if (!_config.ImmediateBridge) return false;

            // 没有任何可选分支的弹窗问不出结果，何必打扰宿主。
            if (buttons.Choices.Count == 0) return false;

            lock (_stateLock)
            {
                if (_pending.Contains(hwnd)) return false;
                if (_nextAskAt.TryGetValue(hwnd, out DateTime nextAsk) && DateTime.UtcNow < nextAsk) return false;
                _pending.Add(hwnd);
                PruneState();
            }

            var request = new DialogRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Hwnd = HwndKey(hwnd),
                Process = SafeProcessName(pid),
                Pid = (int)pid,
                Title = title,
                Body = body,
                Buttons = new List<string>(buttons.Choices),
                Screenshot = string.Empty
            };

            ThreadPool.QueueUserWorkItem(_ => RunBridge(hwnd, request, title));
            return true;
        }

        /// <summary>
        /// 窗口句柄的字符串键。必须与 Add-in 侧 ModalDecisionService.HwndKey 的格式完全一致，
        /// 否则两层的决策记忆对不上，看门狗会重复处置常驻通道已经在管的弹窗。
        /// </summary>
        private static string HwndKey(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X");

        /// <summary>
        /// 后台决策：抓图 → 上报宿主 → 校验决策按钮 → 点击。
        /// 全程异常都被收敛，绝不向线程池抛出（线程池未处理异常会终止进程）。
        /// </summary>
        private void RunBridge(IntPtr hwnd, DialogRequest request, string title)
        {
            string screenshot = string.Empty;
            TimeSpan cooldown = BridgeCooldown;
            try
            {
                if (_config.CaptureDialogImages && !string.IsNullOrWhiteSpace(_config.CaptureDirectory) && IsWindow(hwnd))
                    screenshot = DialogCapture.TryCapture(hwnd, _config.CaptureDirectory, "dialog") ?? string.Empty;
                request.Screenshot = screenshot;

                _log($"[询问]「{title}」按钮=[{string.Join(" / ", request.Buttons)}]"
                     + (screenshot.Length > 0 ? " 截图=" + screenshot : string.Empty));
                Emit("bridge-ask", title, request.Body, request.Buttons, screenshot, "已上报宿主，等待 AI 决策");

                DialogDecision? decision = _decide?.Invoke(request);
                if (decision == null || string.IsNullOrWhiteSpace(decision.Button))
                {
                    // 宿主明确表示"这次不该问"（如判定为用户手动弹窗）用短冷却；
                    // 超时/连不上则用长冷却，避免每次轮询都去打扰一个没反应的宿主。
                    if (decision != null && string.Equals(decision.Source, "skipped", StringComparison.OrdinalIgnoreCase))
                        cooldown = BridgeCooldownShort;

                    string reason = decision == null ? "无决策/超时/宿主未运行" : decision.Reason;
                    _log($"[放弃]「{title}」未获得可用决策，保持弹窗（{reason}）");
                    Emit("bridge-none", title, request.Body, request.Buttons, screenshot, reason);
                    return;
                }

                if (!IsWindow(hwnd))
                {
                    _log($"[放弃]「{title}」决策返回时弹窗已消失");
                    Emit("bridge-none", title, request.Body, request.Buttons, screenshot, "弹窗已消失");
                    return;
                }

                // 决策按钮必须真实存在于这个弹窗上：模型可能编造按钮文本，
                // 也可能在等待期间弹窗内容已变化。匹配不到就什么都不点。
                ButtonScan current = ReadButtons(hwnd);
                IntPtr target = FindButton(current, decision.Button);
                if (target == IntPtr.Zero)
                {
                    _log($"[放弃]「{title}」决策按钮「{decision.Button}」在当前弹窗中不存在");
                    Emit("bridge-none", title, request.Body, request.Buttons, screenshot,
                        "决策按钮不存在：" + decision.Button);
                    return;
                }

                // 危险语义二次闸门：宿主已过滤一遍，这里再拦一次（进程外最后一道）。
                if (IsDenied(decision.Button))
                {
                    _log($"[拒绝]「{title}」决策按钮「{decision.Button}」命中危险语义，不点击");
                    Emit("bridge-deny", title, request.Body, request.Buttons, screenshot,
                        "危险按钮拒绝：" + decision.Button);
                    return;
                }

                Click(target, hwnd, "AI决策(" + decision.Source + ")", title);
                Emit("bridge-click", title, request.Body, request.Buttons, screenshot,
                    decision.Source + "：" + decision.Reason, decision.Button);
            }
            catch (Exception ex)
            {
                _log("[异常] 弹窗桥接：「" + title + "」" + ex.Message);
                Emit("error", title, request.Body, request.Buttons, screenshot, ex.Message);
            }
            finally
            {
                lock (_stateLock)
                {
                    _pending.Remove(hwnd);
                    _nextAskAt[hwnd] = DateTime.UtcNow + cooldown;
                }
            }
        }

        private bool RuleMatches(ResponderRule rule, string title, string body, string combined)
        {
            if (rule.Markers.Count == 0) return false;
            string target = rule.Match switch
            {
                MatchScope.Title => Normalize(title),
                MatchScope.Body => Normalize(body),
                _ => combined
            };
            if (target.Length == 0) return false;
            return rule.Markers.Any(m => target.Contains(Normalize(m), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>点击按钮并返回被点击按钮的归一化标签（供事件记录写清"点的是什么"）。</summary>
        private string Click(IntPtr button, IntPtr dialog, string rule, string title)
        {
            string label = NormalizeButtonText(ReadWindowText(button));
            BumpAttempts(dialog);
            SendMessageTimeout(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 250, out _);
            int id = GetDlgCtrlID(button);
            if (id == 0 && (label is "确定" or "ok" or "是" or "yes" or "接受" or "accept"))
                id = IDOK;
            if (id != 0 && dialog != IntPtr.Zero)
            {
                // 同时向弹窗宿主投递 WM_COMMAND (BN_CLICKED=0, id)，确保标准对话框的消息循环能立刻捕获并退出模态循环
                SendMessageTimeout(dialog, WM_COMMAND, (IntPtr)id, button, SMTO_ABORTIFHUNG, 250, out _);
            }
            // 把被点的按钮标题写进普通日志：事故复盘时"点了什么"比"命中了哪条规则"更关键
            // （2026-10-13 的日志只有规则名，看不出点的是主窗口上的空标题按钮）。
            _log($"[已点击]「{title}」（{rule} → 「{(label.Length == 0 ? "<无标题按钮>" : label)}」）");
            return label;
        }

        /// <summary>
        /// 记录一次弹窗处置：写结构化事件日志，并把"真的发生了动作"的事件单向报给宿主
        /// （批量翻译报告要展示期间处理了哪些弹窗）。
        /// 节流在这里统一做：扫描是 250ms 一次的轮询，不节流会把日志和宿主都刷爆。
        /// </summary>
        private void Emit(string kind, string title, string body, List<string> buttons, string screenshot, string detail, string action = "")
        {
            // 已产生动作的事件（rule-click / bridge-click / rule-close）绝不节流：
            // 节流是为了压住 250ms 轮询产生的重复"未命中"日志，不是为了让批量报告少记。
            bool actionEvent = kind == "rule-click" || kind == "bridge-click" || kind == "rule-close";
            if (!actionEvent)
            {
                string key = kind + "\u0001" + (title ?? string.Empty);
                lock (_stateLock)
                {
                    DateTime now = DateTime.UtcNow;
                    if (_lastEmit.TryGetValue(key, out DateTime last) && now - last < EmitThrottle) return;
                    _lastEmit[key] = now;
                    if (_lastEmit.Count > 256) PruneThrottle(_lastEmit, now);
                }
            }

            if (!string.IsNullOrWhiteSpace(_config.DialogLogPath))
            {
                DialogEventLog.Append(_config.DialogLogPath, new DialogEvent
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Kind = kind,
                    Title = title ?? string.Empty,
                    Body = body ?? string.Empty,
                    Buttons = buttons ?? new List<string>(),
                    Screenshot = screenshot ?? string.Empty,
                    Detail = detail ?? string.Empty
                });
            }

            NotifyHost(kind, title, action, detail);
        }

        /// <summary>
        /// 单向告知宿主"这个弹窗被怎么处理了"。只报真正产生结果的事件——
        /// 未命中/文件选择器保护之类的"没动作"事件每轮都会出现，报过去只是噪声。
        /// 发送在线程池上做，绝不占用扫描线程。
        /// 形参按可空声明：调用方可能只关心 kind（例如异常路径），内部统一兜底。
        /// </summary>
        private void NotifyHost(string kind, string? title, string? action, string? detail)
        {
            if (!_config.EnableDialogBridge || string.IsNullOrWhiteSpace(_config.PipeName)) return;
            if (!ShouldNotifyHost(kind)) return;

            var message = new DialogEventMessage
            {
                Layer = LayerOf(kind),
                Title = title ?? string.Empty,
                Action = action ?? string.Empty,
                Detail = detail ?? string.Empty
            };
            string pipeName = _config.PipeName;
            ThreadPool.QueueUserWorkItem(_ => DialogBridgeClient.Notify(pipeName, message));
        }

        private static bool ShouldNotifyHost(string kind) => kind is "rule-click" or "rule-close" or "bridge-click" or "bridge-none" or "bridge-deny" or "error";

        private static string LayerOf(string kind) => kind switch
        {
            "rule-click" or "rule-close" => "规则",
            "bridge-click" or "bridge-none" or "bridge-deny" => "AI",
            _ => "记录"
        };

        /// <summary>按内容节流的文本日志：同一句话在窗口持续存在时不再每 250ms 刷一行。</summary>
        private void LogThrottled(string key, string message)
        {
            lock (_stateLock)
            {
                DateTime now = DateTime.UtcNow;
                if (_lastLog.TryGetValue(key, out DateTime last) && now - last < LogThrottle) return;
                _lastLog[key] = now;
                if (_lastLog.Count > 256) PruneThrottle(_lastLog, now);
            }
            _log(message);
        }

        private static void PruneThrottle(Dictionary<string, DateTime> map, DateTime now)
        {
            foreach (string key in map.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToList())
                map.Remove(key);
        }

        private int AttemptsOf(IntPtr hwnd)
        {
            lock (_stateLock)
            {
                _attempts.TryGetValue(hwnd, out int attempts);
                return attempts;
            }
        }

        private void BumpAttempts(IntPtr hwnd)
        {
            lock (_stateLock)
            {
                _attempts.TryGetValue(hwnd, out int attempts);
                _attempts[hwnd] = attempts + 1;
            }
        }

        /// <summary>窗口句柄会随着弹窗关闭失效；积累过多时清掉已消失的条目，避免字典无限增长。</summary>
        private void PruneState()
        {
            if (_attempts.Count > 256)
            {
                foreach (IntPtr key in _attempts.Keys.Where(k => !IsWindow(k)).ToList()) _attempts.Remove(key);
            }
            if (_nextAskAt.Count > 256)
            {
                DateTime now = DateTime.UtcNow;
                foreach (IntPtr key in _nextAskAt.Where(pair => pair.Value < now || !IsWindow(pair.Key)).Select(pair => pair.Key).ToList())
                    _nextAskAt.Remove(key);
            }
        }

        /// <summary>决策按钮在弹窗里的定位：四段递进匹配（见共享的 ModalDecisionSafety.MatchButton）。</summary>
        private IntPtr FindButton(ButtonScan scan, string label)
        {
            var labels = new List<string>();
            foreach (var pair in scan.Candidates) labels.Add(pair.Value);

            string? matched = ModalDecisionSafety.MatchButton(labels, label);
            if (string.IsNullOrEmpty(matched)) return IntPtr.Zero;

            foreach (var pair in scan.Candidates)
            {
                if (string.Equals(pair.Value, matched, StringComparison.Ordinal)) return pair.Key;
            }
            return IntPtr.Zero;
        }

        /// <summary>AI 决策的危险按钮拒绝词（只对桥接决策生效，不影响 Rules）。</summary>
        private bool IsDenied(string label) => ModalDecisionSafety.IsDenied(label, _config.DecisionDenyWords);

        private static string SafeProcessName(uint pid)
        {
            try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { return string.Empty; }
        }

        /// <summary>委托给共享纯逻辑：规则匹配用的严格归一化（去空白/标点/大小写）。</summary>
        private static string Normalize(string value) => ModalDecisionSafety.Normalize(value);

        private sealed class ButtonScan
        {
            public IntPtr Positive = IntPtr.Zero;
            public IntPtr Negative = IntPtr.Zero;
            /// <summary>否定按钮优先级：IDNO &gt; 否定前缀 &gt; 取消/否 &gt; 保存/删除等动作词。</summary>
            public int NegativePriority;
            public int ChoiceCount;
            public bool HasNegative;
            public readonly List<string> Labels = new();

            /// <summary>非展开项的业务按钮（文本 → 按钮句柄），桥接决策的可选集合与定位依据。</summary>
            public readonly List<KeyValuePair<IntPtr, string>> Candidates = new();

            /// <summary>非展开项的按钮文本，去掉空文本，直接上报给宿主。</summary>
            public List<string> Choices
            {
                get
                {
                    var choices = new List<string>();
                    foreach (var pair in Candidates)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Value) && !choices.Contains(pair.Value)) choices.Add(pair.Value);
                    }
                    return choices;
                }
            }
        }

        /// <summary>
        /// 否定前缀：不/勿/别/don't/do not 开头的按钮（如「不保存」「不再提示」）。
        /// 这类按钮必须优先于「保存」「删除」等动作词，否则「不保存」会被
        /// 当成「保存」的同义词，在 Z 序不利时点错。
        /// </summary>
        private static bool HasNegationPrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            return value.StartsWith("不", StringComparison.Ordinal)
                || value.StartsWith("勿", StringComparison.Ordinal)
                || value.StartsWith("别", StringComparison.Ordinal)
                || value.StartsWith("无需", StringComparison.Ordinal)
                || value.StartsWith("don't", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("dont", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("do not", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("not ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>只在「取消/否」全等按钮上回退，绝不再回退到肯定按钮。</summary>
        private static IntPtr FindExactCancelButton(ButtonScan scan)
        {
            if (scan == null) return IntPtr.Zero;
            foreach (KeyValuePair<IntPtr, string> pair in scan.Candidates)
            {
                string text = pair.Value;
                if (text is "取消" or "否" or "no" or "cancel") return pair.Key;
            }
            return IntPtr.Zero;
        }

        private ButtonScan ReadButtons(IntPtr dialog)        {
            var scan = new ButtonScan();
            int bestRank = int.MaxValue;
            EnumChildWindows(dialog, (child, lParam) =>
            {
                if (!IsWindowVisible(child) || !IsWindowEnabled(child)) return true;
                var className = new StringBuilder(256);
                GetClassName(child, className, 256);
                if (!string.Equals(className.ToString(), "Button", StringComparison.OrdinalIgnoreCase)) return true;

                string text = NormalizeButtonText(ReadWindowText(child));
                int id = GetDlgCtrlID(child);
                scan.Labels.Add(text);
                if (IsExpander(text)) return true;

                // 无标题按钮不是弹窗按钮（工具条/框架上的匿名控件）。
                // 只有有标题的按钮才允许被点击：2026-10-13 的事故里被点中的正是
                // 主窗口上一个空标题按钮，点下去之后弹窗被错误地关掉、AutoCAD 随后崩溃。
                bool labeled = ModalDecisionSafety.IsClickableLabel(text);

                scan.ChoiceCount++;
                scan.Candidates.Add(new KeyValuePair<IntPtr, string>(child, text));

                // 否定按钮分级，避免「保存」与「不保存」都算否定时按 Z 序点错：
                //   IDNO > 否定前缀（不/勿/don't）> 取消/否全等 > 保存/删除等动作词全等。
                // 旧实现用 Contains("保存")，导致「不保存」和「保存」同为否定，
                // Z 序不利时会反向点「保存」，把用户不想保存的状态静默落盘。
                bool negationPrefix = HasNegationPrefix(text);
                bool exactCancel = text is "取消" or "否" or "no" or "cancel";
                bool exactActionWord = text is "保存" or "save" or "删除" or "delete"
                    or "覆盖" or "overwrite" or "replace" or "替换";
                bool negative = id is IDNO or 2 || negationPrefix || exactCancel || exactActionWord;
                if (negative)
                {
                    // HasNegative 与标题无关（保守判断），但可点的否定按钮必须有标题。
                    scan.HasNegative = true;
                    if (labeled)
                    {
                        int priority = id == IDNO ? 4
                            : negationPrefix ? 3
                            : exactCancel ? 2
                            : 1;
                        if (priority > scan.NegativePriority)
                        {
                            scan.Negative = child;
                            scan.NegativePriority = priority;
                        }
                    }
                }

                if (!labeled) return true;

                int rank = id switch
                {
                    IDOK => 0, IDYES => 1, IDCONTINUE => 2, IDCLOSE => 3,
                    _ => text is "确定" or "ok" ? 4
                       : text is "是" or "yes" or "接受" or "accept" ? 5
                       : int.MaxValue
                };
                if (rank < bestRank)
                {
                    bestRank = rank;
                    scan.Positive = child;
                }
                return true;
            }, IntPtr.Zero);
            return scan;
        }

        /// <summary>
        /// 数子孙窗口（EnumChildWindows 本身是递归的），数到 <paramref name="cap"/> 就停。
        /// 用于把"主框架"和"弹窗"区分开，顺带避免每 250ms 对主窗口整棵树做一次全量遍历。
        /// </summary>
        private static int CountDescendants(IntPtr hwnd, int cap)
        {
            int count = 0;
            try
            {
                EnumChildWindows(hwnd, (child, lParam) =>
                {
                    count++;
                    return count < cap;
                }, IntPtr.Zero);
            }
            catch { }
            return count;
        }

        /// <summary>委托给共享纯逻辑；词表仍走配置（ExpanderButtons 可覆盖默认）。</summary>
        private bool IsExpander(string text) => ModalDecisionSafety.IsExpander(text, _config.ExpanderButtons);

        private static string ReadDialogBody(IntPtr dialog)
        {
            var text = new StringBuilder();
            EnumChildWindows(dialog, (child, lParam) =>
            {
                var className = new StringBuilder(256);
                GetClassName(child, className, 256);
                string name = className.ToString();
                if (name is "Static" or "Edit")
                {
                    string value = ReadWindowText(child);
                    if (!string.IsNullOrWhiteSpace(value)) text.Append(' ').Append(value);
                }
                return true;
            }, IntPtr.Zero);
            return text.ToString();
        }

        /// <summary>委托给共享纯逻辑：去掉助记符/尾部快捷键括号并小写，用于按钮标签比对。</summary>
        private static string NormalizeButtonText(string text) => ModalDecisionSafety.NormalizeButtonText(text);

        private static string ReadWindowText(IntPtr hwnd)
        {
            var buffer = new StringBuilder(2048);
            GetWindowText(hwnd, buffer, 2048);
            return buffer.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
