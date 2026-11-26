using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

using CadAI.Core;

namespace CadAI.Core.Modal
{
    /// <summary>
    /// ModalAgent 上报的消息。Kind 为 "event" 时是单向事件（宿主只登记、不回包），
    /// 否则是一次待决策的弹窗快照。
    /// </summary>
    public sealed class ModalDialogRequest
    {
        /// <summary>消息类型：空/其它 = 弹窗决策请求；"event" = 弹窗处理事件。</summary>
        public string Kind { get; set; }

        public string Id { get; set; }

        /// <summary>窗口句柄的字符串键（与 ModalDecisionService.HwndKey 同格式），用于决策记忆去重。</summary>
        public string Hwnd { get; set; }

        public string Process { get; set; }
        public int Pid { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public List<string> Buttons { get; set; }
        public string Screenshot { get; set; }

        // 以下三个字段只在事件消息（Kind=event）里使用。
        /// <summary>处理层：规则 / AI / 看门狗。</summary>
        public string Layer { get; set; }

        /// <summary>实际动作（被点击的按钮文本）；空 = 没有动作。</summary>
        public string Action { get; set; }

        public string Detail { get; set; }
    }

    /// <summary>回给 ModalAgent 的决策；Button 为空 = 不动作（保持弹窗交给人工）。</summary>
    public sealed class ModalDialogDecision
    {
        public string Id { get; set; }
        public string Button { get; set; }
        public string Reason { get; set; }
        public string Source { get; set; }
    }

    /// <summary>
    /// 弹窗桥接服务端：在后台线程上监听命名管道，接收 ModalAgent 的弹窗上报并回一个决策。
    ///
    /// 为什么必须是后台线程：宿主弹出模态框时，AutoCAD 主 STA 线程正在模态消息循环里，
    /// 而卡住的那次工具调用（进程内 COM 走 Dispatcher.Invoke / 子进程走跨进程 COM）
    /// 还在等它返回。整条决策链路如果在主线程上跑就是死锁，所以这里从头到尾不碰 UI 线程、
    /// 不碰 AutoCAD API——只做"收消息 →（可选的）模型请求 → 回消息"。
    ///
    /// 稳定性：所有异常都收敛成"回一个空决策"，让 ModalAgent 保持不动作；
    /// 服务线程是后台线程，进程退出时不会阻塞关闭。
    /// </summary>
    public sealed class ModalDialogBridgeServer : IDisposable
    {
        /// <summary>读取一条请求的超时：客户端异常挂起时不能让服务线程永久等待。</summary>
        private const int RequestReadTimeoutMs = 5000;

        private readonly string _pipeName;
        private readonly Func<ModalDialogRequest, ModalDialogDecision> _decide;
        private readonly Action<string> _log;
        private readonly JavaScriptSerializer _json = AgentJson.New();

        private Thread _thread;
        private volatile bool _stopping;

        public ModalDialogBridgeServer(string pipeName, Func<ModalDialogRequest, ModalDialogDecision> decide, Action<string> log)
        {
            _pipeName = pipeName;
            _decide = decide ?? (request => null);
            _log = log ?? (message => { });
        }

        public void Start()
        {
            if (_thread != null) return;
            _stopping = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ChatCore.ModalDialogBridge"
            };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stopping)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        4,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    // 复用同一个服务实例连续接受多次连接：每次处理完 Disconnect 回到监听态。
                    while (!_stopping)
                    {
                        IAsyncResult wait = server.BeginWaitForConnection(null, null);
                        // 分片等待，保证 Dispose 时能及时退出（管道等待本身不可取消）。
                        while (!wait.AsyncWaitHandle.WaitOne(200))
                        {
                            if (_stopping) break;
                        }
                        if (_stopping) break;

                        try { server.EndWaitForConnection(wait); }
                        catch (Exception ex)
                        {
                            Log("桥接等待连接失败，重建管道：" + ex.Message);
                            break;
                        }

                        HandleConnection(server);

                        try { server.Disconnect(); }
                        catch (Exception ex)
                        {
                            Log("桥接断开连接失败，重建管道：" + ex.Message);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("桥接服务异常：" + ex.Message);
                    Thread.Sleep(500);
                }
                finally
                {
                    try { server?.Dispose(); } catch { }
                }
            }
        }

        private void HandleConnection(NamedPipeServerStream server)
        {
            try
            {
                using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, true))
                using (var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                {
                    // 同步 ReadLine 不可取消，放到线程池上等超时。
                    System.Threading.Tasks.Task<string> readTask =
                        System.Threading.Tasks.Task.Run(() => reader.ReadLine());
                    if (!readTask.Wait(RequestReadTimeoutMs))
                    {
                        Log("桥接请求读取超时。");
                        return;
                    }

                    string line = readTask.Result;
                    if (string.IsNullOrWhiteSpace(line)) return;

                    ModalDialogRequest request = ParseRequest(line);
                    if (request == null)
                    {
                        Write(writer, new ModalDialogDecision { Id = string.Empty, Button = string.Empty, Reason = "请求解析失败", Source = "error" });
                        return;
                    }

                    // 单向事件（ModalAgent 自己用规则处理掉的弹窗）：只登记到事件汇，
                    // 供批量翻译报告等消费方取用，不回包。
                    if (string.Equals(request.Kind, "event", StringComparison.OrdinalIgnoreCase))
                    {
                        ModalEventFeed.Publish(request.Layer, request.Title, request.Action, request.Detail);
                        return;
                    }

                    ModalDialogDecision decision = null;
                    try { decision = _decide(request); }
                    catch (Exception ex) { Log("决策异常：" + ex.Message); }
                    if (decision == null)
                        decision = new ModalDialogDecision { Button = string.Empty, Reason = "无决策", Source = "none" };
                    decision.Id = request.Id;

                    Write(writer, decision);
                }
            }
            catch (Exception ex)
            {
                Log("桥接处理异常：" + ex.Message);
            }
        }

        private void Write(StreamWriter writer, ModalDialogDecision decision)
        {
            // 键名固定小写：ModalAgent 侧按 camelCase 写入、按大小写不敏感读取，
            // 两边都不依赖 JavaScriptSerializer 的命名策略。
            var payload = new Dictionary<string, object>
            {
                ["id"] = decision.Id ?? string.Empty,
                ["button"] = decision.Button ?? string.Empty,
                ["reason"] = decision.Reason ?? string.Empty,
                ["source"] = decision.Source ?? string.Empty
            };
            writer.WriteLine(_json.Serialize(payload));
        }

        private ModalDialogRequest ParseRequest(string line)
        {
            var root = _json.DeserializeObject(line) as Dictionary<string, object>;
            if (root == null) return null;

            var request = new ModalDialogRequest
            {
                Kind = Text(root, "kind"),
                Id = Text(root, "id"),
                Hwnd = Text(root, "hwnd"),
                Process = Text(root, "process"),
                Title = Text(root, "title"),
                Body = Text(root, "body"),
                Screenshot = Text(root, "screenshot"),
                Layer = Text(root, "layer"),
                Action = Text(root, "action"),
                Detail = Text(root, "detail"),
                Buttons = Strings(root, "buttons")
            };

            object pid;
            if (root.TryGetValue("pid", out pid) && pid != null)
            {
                try { request.Pid = Convert.ToInt32(pid); } catch { request.Pid = 0; }
            }
            return request;
        }

        private static string Text(IDictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return string.Empty;
            return Convert.ToString(value) ?? string.Empty;
        }

        private static List<string> Strings(IDictionary<string, object> map, string key)
        {
            var result = new List<string>();
            object value;
            if (map == null || !map.TryGetValue(key, out value) || value == null) return result;

            if (value is string single)
            {
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single);
                return result;
            }
            if (value is IEnumerable sequence)
            {
                foreach (object item in sequence)
                {
                    string text = Convert.ToString(item);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
                }
            }
            return result;
        }

        private void Log(string message)
        {
            try { _log("[ModalBridge] " + message); } catch { }
        }

        public void Dispose()
        {
            _stopping = true;
            // 不 Join：服务线程是后台线程，且管道等待不可取消；强等只会拖住 UI 线程。
            // 正在处理的请求结束时线程自然退出，最坏情况随进程一起结束。
        }
    }
}
