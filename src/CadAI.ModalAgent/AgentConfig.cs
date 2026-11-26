using System.Text.Json;
using System.Text.Json.Serialization;

namespace CadAI.ModalAgent
{
    /// <summary>匹配范围。</summary>
    public enum MatchScope
    {
        /// <summary>标题+正文合并匹配。</summary>
        Any = 0,
        /// <summary>仅标题。</summary>
        Title = 1,
        /// <summary>仅正文（窗口内的 Static/Edit 文本）。</summary>
        Body = 2
    }

    /// <summary>规则命中后的动作。</summary>
    public enum ResponderAction
    {
        /// <summary>点正按钮（确定/是/OK…）。</summary>
        ClickPositive = 0,
        /// <summary>点否定按钮（取消/否…；无否定按钮时回退正按钮）。</summary>
        ClickNegative = 1,
        /// <summary>发送 WM_CLOSE 关闭窗口（适用于无按钮弹窗）。</summary>
        Close = 2,
        /// <summary>明确不动作（只记录），用于显式排除。</summary>
        Ignore = 3
    }

    /// <summary>单条应答规则：标记列表按范围匹配，命中即执行动作。</summary>
    public sealed class ResponderRule
    {
        public string Name { get; set; } = "";
        public List<string> Markers { get; set; } = new();
        public MatchScope Match { get; set; } = MatchScope.Any;
        public ResponderAction Action { get; set; } = ResponderAction.ClickPositive;
    }

    /// <summary>
    /// ModalAgent 配置。默认读取 exe 同目录的 modal-agent.json（可选，支持注释），
    /// 也可用 --config 指定路径；文件缺失或损坏时使用内置默认值。
    /// </summary>
    internal sealed class AgentConfig
    {
        /// <summary>要监视的宿主进程名（不含 .exe，大小写不敏感），默认 AutoCAD（acad）。</summary>
        public List<string> ProcessNames { get; set; } = new() { "acad" };

        /// <summary>调试模式：显示窗口与执行记录（命令行 --debug 可覆盖）。</summary>
        public bool Debug { get; set; } = false;

        /// <summary>应答模式：whitelist（白名单优先，默认）或 standard。</summary>
        public string Mode { get; set; } = "whitelist";

        /// <summary>轮询兜底间隔（毫秒）；0 表示关闭轮询，仅靠 WinEvent 事件驱动。</summary>
        public int PollIntervalMs { get; set; } = 250;

        /// <summary>同一个 HWND 的动作次数上限（防死循环点同一个框）。</summary>
        public int MaxAttemptsPerWindow { get; set; } = 3;

        /// <summary>候选窗口的子孙窗口数上限：超过它就判定不是弹窗（防止误判主框架窗口）。</summary>
        public int MaxDialogChildWindows { get; set; } = 100;

        /// <summary>空闲自动退出（分钟）；0 表示不启用。</summary>
        public int IdleExitMinutes { get; set; } = 60;

        /// <summary>唯一标识（由 --id 传入）。</summary>
        public string Identity { get; set; } = "ModalAgent";

        /// <summary>无标题的窗口一律不处理。</summary>
        public bool IgnoreUntitledWindows { get; set; } = true;

        /// <summary>忽略的窗口类名（输入法/广播窗口等系统噪声）。</summary>
        public List<string> IgnoreWindowClasses { get; set; } = new()
        {
            "CiceroUIWndFrame", "MSCTFIME UI", "Default IME", "Sogou_TSF_UI",
            "GDI+ Window", "DDE Server Window", ".NET-BroadcastEventWindow",
            "MediaContextNotificationWindow", "SystemResourceNotifyWindow",
            "NotificationWindowHelper", "AdApplicationButton", "HintWnd",
            "Hidden Window", "RibbonHwndSource"
        };

        /// <summary>窗口标题包含这些标记的窗口跳过（如插件自己的窗口）。</summary>
        public List<string> IgnoreTitleMarkers { get; set; } = new() { "CADAI", "CAD AI" };

        /// <summary>文件选择器标题标记：无论任何规则，永不自动点击。</summary>
        public List<string> FileDialogTitles { get; set; } = new()
        {
            "打开", "保存", "另存", "选择文件", "选择文件夹", "浏览",
            "Open", "Save", "Save As", "Browse", "Select File", "Select Folder"
        };

        /// <summary>展开型按钮文本（详细信息/帮助等，不算业务选项分支）。</summary>
        public List<string> ExpanderButtons { get; set; } =
            new(CadAI.Core.Modal.ModalDecisionSafety.DefaultExpanderLabels);

        /// <summary>默认应答规则（按顺序匹配，首条命中即执行）。</summary>
        public List<ResponderRule> Rules { get; set; } = new()
        {
            new ResponderRule
            {
                Name = "会话更新保存框→保存",
                Match = MatchScope.Any,
                Action = ResponderAction.ClickPositive,
                Markers = new List<string>
                {
                    "多个文件已在当前会话中更新", "是否要将更改保存到"
                }
            },
            new ResponderRule
            {
                Name = "保存确认框→不保存",
                Match = MatchScope.Any,
                Action = ResponderAction.ClickNegative,
                Markers = new List<string>
                {
                    "是否要将更改保存", "已经在当前会话中更新", "是否保存对",
                    "doyouwanttosavethechanges", "doyouwanttosavechangesto", "savethechangestodocument"
                }
            },
            new ResponderRule
            {
                Name = "已知无害弹窗→确定",
                Match = MatchScope.Any,
                Action = ResponderAction.ClickPositive,
                Markers = new List<string>
                {
                    "样式冲突", "styleconflict", "样式定义", "stylelibrary", "将使用样式库定义", "样式定义不同",
                    "找到字体", "字体替换", "字体缺失", "missingfont", "substitutefont", "fontsubstitut", "fontnotfound",
                    "旧版本dwg", "dwgmigration", "迁移dwg", "olderversiondwg",
                    "无法启动autodeskdesktopconnect服务", "desktopconnect", "guidedtutorialplugin",
                    "继续从当前位置打开文件", "选定文件的位置不在激活项目文件的搜索路径",
                    "工程图更新延迟"
                }
            }
        };

        /// <summary>执行记录文件路径（空 = exe 同目录 modal-agent.log）。</summary>
        public string LogFilePath { get; set; } = "";

        /// <summary>是否启用弹窗桥接：白名单未命中时上报宿主由 AI 决策。</summary>
        public bool EnableDialogBridge { get; set; } = true;

        /// <summary>桥接触发时机：immediate 或 stall。</summary>
        public string DecisionTrigger { get; set; } = "immediate";

        /// <summary>等待宿主决策的总超时（毫秒）。</summary>
        public int DecisionTimeoutMs { get; set; } = 20000;

        /// <summary>桥接上报时是否附带弹窗截图。</summary>
        public bool CaptureDialogImages { get; set; } = true;

        /// <summary>截图输出目录（空 = %USERPROFILE%\Documents\CADAI\Captures\Dialogs）。</summary>
        public string CaptureDirectory { get; set; } = "";

        /// <summary>宿主管道名（空 = 由 --id 推导为 CADAI.ModalAgent.&lt;id&gt;）。</summary>
        public string PipeName { get; set; } = "";

        /// <summary>结构化弹窗事件日志（空 = exe 同目录 modal-agent.dialogs.jsonl）。</summary>
        public string DialogLogPath { get; set; } = "";

        /// <summary>AI 决策的危险按钮拒绝词。</summary>
        public List<string> DecisionDenyWords { get; set; } =
            new(CadAI.Core.Modal.ModalDecisionSafety.DefaultDenyWords);

        public bool WhitelistOnly => !string.Equals(Mode, "standard", StringComparison.OrdinalIgnoreCase);

        public bool ImmediateBridge => !string.Equals(DecisionTrigger, "stall", StringComparison.OrdinalIgnoreCase);

        public static AgentConfig Load(string path)
        {
            LastLoadError = string.Empty;
            var config = new AgentConfig();
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return config;
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var loaded = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), options);
                if (loaded == null) return config;
                LastLoadError = string.Empty;
                return loaded;
            }
            catch (Exception ex)
            {
                LastLoadError = ex.Message;
                TryLog($"配置加载失败，使用默认值：{ex.Message}（{path}）");
                return config;
            }
        }

        public static string LastLoadError { get; private set; } = string.Empty;

        private static void TryLog(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "modal-agent.log"),
                    DateTime.Now + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
