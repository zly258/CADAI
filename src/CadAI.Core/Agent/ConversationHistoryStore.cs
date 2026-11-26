using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CadAI.Core
{
    internal sealed partial class ConversationHistoryStore
    {
        private readonly JavaScriptSerializer _json = AgentJson.New();
        private readonly string _model;
        private readonly string _baseUrl;
        private readonly List<HistoryEvent> _events = new List<HistoryEvent>();
        private IReadOnlyList<AgentMessage> _lastMessages;
        private string _title = "新对话";

        public ConversationHistoryStore(AiSettings settings)
        {
            _model = settings?.Model ?? string.Empty;
            _baseUrl = settings?.BaseUrl ?? string.Empty;
            StartedAt = DateTime.Now;
            string shortId = Guid.NewGuid().ToString("N").Substring(0, 8);

            // 2026-10-14：会话记录改为落在「按对话分目录」的 Conversation/ 下，
            // 与本次会话产出的模型/图纸/导出/截图放在同一个会话目录里，便于按对话追溯。
            // 分目录关闭或创建失败时，退回旧的 History/yyyyMMdd/ 布局（不阻断对话）。
            string conversationDirectory = AiWorkspace.BeginConversation(shortId, StartedAt, _title);
            string directory;
            if (AiWorkspace.UseConversationLayout
                && !string.Equals(conversationDirectory, AiWorkspace.RootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.Combine(conversationDirectory, "Conversation");
                ConversationDirectory = conversationDirectory;
            }
            else
            {
                directory = Path.Combine(RootDirectory, StartedAt.ToString("yyyyMMdd"));
                ConversationDirectory = string.Empty;
            }
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory, "session-" + StartedAt.ToString("HHmmss") + "-" + shortId + ".md");
        }

        public static string RootDirectory => AiWorkspace.HistoryDirectory;
        public DateTime StartedAt { get; }
        public string FilePath { get; }

        /// <summary>本次会话的目录（按对话分目录时非空；旧布局时为空串）。</summary>
        public string ConversationDirectory { get; }

        public void Record(string title, string body, bool code = false)
        {
            lock (_saveGate)
            {
                _events.Add(new HistoryEvent { Time = DateTime.Now, Title = title ?? string.Empty, Body = body ?? string.Empty, Code = code });
                Save(_lastMessages);
            }
        }

        public void RecordUsage(string stage, AgentUsage usage)
        {
            if (usage == null) return;
            string prefix = usage.IsEstimated ? "≈" : string.Empty;
            Record("METRICS · " + stage,
                "elapsed=" + usage.Duration.TotalSeconds.ToString("0.000") + "s | prompt=" + prefix + usage.PromptTokens +
                " | completion=" + prefix + usage.CompletionTokens + " | total=" + prefix + usage.TotalTokens);
        }

        // 防抖：一轮对话里 SaveHistory/Record 会被调 15+ 次，每次都全量重写 .md（O(n²) 且在 UI 线程）。
        // 距上次落盘不足 MinSaveInterval 时只记脏标记，由一次性定时器迟写；
        // Flush() 在会话结束/新对话时强制落盘，保证不丢数据。
        //
        // 线程模型：Save 会被 AgentSession 后台线程调用（ConfigureAwait(false) 之后），
        // 也会从 UI 线程来——所有状态读写与落盘都必须持有 _saveGate。
        // 定时器用 System.Threading.Timer 而非 DispatcherTimer：首次 Save 可能发生在
        // 后台线程，DispatcherTimer 会绑定到没有消息泵的后台 Dispatcher，Tick 永不触发。
        private static readonly TimeSpan MinSaveInterval = TimeSpan.FromSeconds(2);
        private readonly object _saveGate = new object();
        private readonly System.Diagnostics.Stopwatch _saveClock = System.Diagnostics.Stopwatch.StartNew();
        private bool _dirty;
        private System.Threading.Timer _deferredSaveTimer;

        public void Save(IReadOnlyList<AgentMessage> messages)
        {
            lock (_saveGate)
            {
                // 快照列表结构：AgentSession 可能在后台线程继续追加消息，
                // 直接保存调用方传入的活引用会在序列化时抛
                // "Collection was modified"，异常被吞后历史快照偶发丢失。
                if (messages != null) _lastMessages = new List<AgentMessage>(messages);
                if (_saveClock.Elapsed < MinSaveInterval)
                {
                    _dirty = true;
                    EnsureDeferredSave();
                    return;
                }
                SaveCore();
            }
        }

        /// <summary>强制立即落盘（会话 Dispose / 新对话时调用）。</summary>
        public void Flush()
        {
            lock (_saveGate)
            {
                if (!_dirty && _saveClock.Elapsed >= MinSaveInterval) return;
                SaveCore();
            }
        }

        private void EnsureDeferredSave()
        {
            if (_deferredSaveTimer != null) return;
            // 一次性定时器：到期后只做"标记 + 触发一次 Save"，实际落盘经由 TakeSaveLock 的
            // Save 路径完成，回调里不长时间持锁。回调在线程池线程执行，落盘本身有锁保护。
            _deferredSaveTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    lock (_saveGate)
                    {
                        System.Threading.Timer timer = _deferredSaveTimer;
                        _deferredSaveTimer = null;
                        timer?.Dispose();
                        if (_dirty) SaveCore();
                    }
                }
                catch { }
            }, null, MinSaveInterval, Timeout.InfiniteTimeSpan);
        }

        private void SaveCore()
        {
            _dirty = false;
            _saveClock.Restart();
            try
            {
                if (_lastMessages != null && string.Equals(_title, "新对话", StringComparison.Ordinal))
                {
                    AgentMessage firstUser = _lastMessages.FirstOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
                    // 标题取用户真正输入的内容：带附件的消息正文里还拼着 [用户附件] 清单，
                    // 直接拿来当标题会在历史列表里露出提示词；只发了附件时用文件名兜底。
                    if (firstUser?.Content is string text)
                    {
                        string title = UserPromptText.ToDisplay(text);
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            IReadOnlyList<string> names = UserPromptText.AttachmentNames(text);
                            if (names.Count > 0) title = names[0];
                        }
                        if (!string.IsNullOrWhiteSpace(title)) _title = NormalizeTitle(title);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                sb.AppendLine("# " + Branding.ProductName + " History").AppendLine();
                sb.AppendLine("- Title: " + _title);
                sb.AppendLine("- Started: " + StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- Updated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- Model: " + _model);
                sb.AppendLine("- Base URL: " + _baseUrl).AppendLine();
                sb.AppendLine("## Conversation").AppendLine();

                if (_lastMessages != null)
                {
                    for (int i = 0; i < _lastMessages.Count; i++)
                    {
                        AgentMessage message = _lastMessages[i];
                        sb.AppendLine("### " + (i + 1) + ". " + (message.Role ?? "unknown").ToUpperInvariant()).AppendLine();
                        AppendValue(sb, message.Content);
                        if (message.ToolCalls != null)
                        {
                            sb.AppendLine().AppendLine("#### Tool calls").AppendLine("````json");
                            sb.AppendLine(_json.Serialize(message.ToolCalls)).AppendLine("````");
                        }
                        sb.AppendLine();
                    }
                }

                sb.AppendLine("## Diagnostics");
                foreach (HistoryEvent item in _events)
                {
                    sb.AppendLine().AppendLine("### [" + item.Time.ToString("HH:mm:ss.fff") + "] " + item.Title).AppendLine();
                    if (item.Code) sb.AppendLine("````text").AppendLine(item.Body).AppendLine("````");
                    else sb.AppendLine(item.Body);
                }

                // 原子落盘：先写临时文件再 File.Replace。此前直接 WriteAllText 覆写，
                // 进程在写中途崩溃/断电会同时损毁主文件与恢复快照，用户以为有历史其实没有。
                WriteAtomic(FilePath, sb.ToString(), new UTF8Encoding(true));
                if (_lastMessages != null)
                    WriteAtomic(SnapshotPath(FilePath), _json.Serialize(_lastMessages), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 落盘失败不能完全静默：至少留一条诊断痕迹，否则历史丢失无从排查。
                try { AiExceptionGuard.Log("ConversationHistoryStore.SaveCore", ex); } catch { }
            }
        }

        /// <summary>temp + File.Replace 原子写入。Replace 的三参数版本要求目标已存在，两种情况分别处理。</summary>
        private static void WriteAtomic(string path, string content, Encoding encoding)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, encoding);

            // File.Replace 要求目标文件的**独占**访问。历史文件会被别的进程短暂打开
            // （测试驱动采集、编辑器、杀毒扫描），此时它抛 "无法删除要被替换的文件"。
            // 异常冒到 SaveCore 只留一条日志，界面上完全看不出来——
            // 结果是**历史记录从此不再更新**，会话文件停在旧内容上。
            //
            // 2026-10-14 实测：一次测试运行里连续 5 次失败
            // （12:38 / 13:21 / 13:32 / 13:38 / 13:42），给事后分析造成很大干扰。
            //
            // 处理：短暂重试 → 退化为"删除后移动"（非原子，但保住内容）→ 都不行才抛。
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    File.Replace(temp, path, null);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    System.Threading.Thread.Sleep(60 * attempt);
                }
                catch (IOException)
                {
                    break;
                }
            }

            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // 连退路都失败：清掉临时文件再抛，避免堆一堆 .tmp 残渣。
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        public static void Delete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
            string snapshot = SnapshotPath(path);
            if (File.Exists(snapshot)) File.Delete(snapshot);
        }

        public static string ReadTitle(string path)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    int count = 0;
                    while ((line = reader.ReadLine()) != null && count++ < 15)
                    {
                        if (line.StartsWith("- Title:", StringComparison.OrdinalIgnoreCase))
                            return line.Substring(8).Trim();
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        public static void Rename(string path, string title)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("历史记录不存在。", path);
            title = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("名称不能为空。");
            string[] lines = File.ReadAllLines(path);
            bool replaced = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("- Title:", StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = "- Title: " + title;
                replaced = true;
                break;
            }
            if (!replaced) lines = new[] { "# " + Branding.ProductName + " History", "", "- Title: " + title }.Concat(lines).ToArray();
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        public static string NormalizeTitle(string text)
        {
            string oneLine = string.Join(" ", (text ?? string.Empty).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            // 用户提示词常以 Markdown 标题开头（"# 工程图翻译 ..."），标题里不应露出井号。
            oneLine = oneLine.TrimStart();
            while (oneLine.Length > 0 && (oneLine[0] == '#' || oneLine[0] == '*')) oneLine = oneLine.Substring(1).TrimStart();
            while (oneLine.Contains("  ")) oneLine = oneLine.Replace("  ", " ");
            const int max = 20;
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }

        private void AppendValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.AppendLine("<null>"); return; }
            if (value is string text) { sb.AppendLine(text); return; }
            sb.AppendLine("````json").AppendLine(_json.Serialize(value)).AppendLine("````");
        }

        private sealed class HistoryEvent
        {
            public DateTime Time { get; set; }
            public string Title { get; set; }
            public string Body { get; set; }
            public bool Code { get; set; }
        }
    }
}
