using System.Text.Json;

namespace CadAI.ModalAgent
{
    /// <summary>一条弹窗事件（结构化日志，供宿主解析与人工排查）。</summary>
    internal sealed class DialogEvent
    {
        public string Time { get; set; } = string.Empty;

        /// <summary>
        /// 事件类型：
        ///   rule-click  规则命中并点击
        ///   rule-close  规则命中并关闭窗口
        ///   rule-ignore 规则显式忽略
        ///   skipped     未命中任何规则（白名单优先，不动作）
        ///   bridge-ask  已上报宿主，等待 AI 决策
        ///   bridge-click 采纳 AI 决策并点击
        ///   bridge-none 宿主无决策 / 超时 / 决策按钮不存在（保持弹窗）
        ///   bridge-deny 采纳的按钮命中危险语义，拒绝点击
        ///   error       处理过程异常
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<string> Buttons { get; set; } = new();
        public string Screenshot { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// 结构化弹窗事件日志（每行一条 JSON）。
    ///
    /// 与 modal-agent.log 的区别：那份是给人看的自由文本，这份是给宿主/脚本解析的。
    /// 单文件超过上限时直接轮转覆盖，避免长期运行把磁盘写满。
    /// </summary>
    internal static class DialogEventLog
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        private static readonly object Sync = new();
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public static void Append(string path, DialogEvent dialogEvent)
        {
            if (string.IsNullOrWhiteSpace(path) || dialogEvent == null) return;
            try
            {
                lock (Sync)
                {
                    Rotate(path);
                    File.AppendAllText(path, JsonSerializer.Serialize(dialogEvent, Options) + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不能影响弹窗处理。
            }
        }

        private static void Rotate(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            try { File.Delete(path); } catch { }
        }
    }
}
