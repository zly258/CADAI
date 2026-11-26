using System;
using System.Collections.Generic;

namespace CadAI.Core.Modal
{
    /// <summary>一条"弹窗处理"记录：谁处理的、哪个弹窗、点了什么、为什么。</summary>
    public sealed class ModalEventRecord
    {
        public DateTime Time = DateTime.Now;

        /// <summary>处理层：规则 / AI / 看门狗。</summary>
        public string Layer = string.Empty;

        public string Title = string.Empty;

        /// <summary>实际动作（被点击的按钮文本）；空 = 没有动作（保持弹窗）。</summary>
        public string Action = string.Empty;

        public string Detail = string.Empty;

        public string Format()
        {
            string line = Time.ToString("HH:mm:ss") + "  [" + Layer + "] 「" + Title + "」";
            line += string.IsNullOrWhiteSpace(Action) ? " 未动作" : " → " + Action;
            if (!string.IsNullOrWhiteSpace(Detail)) line += "（" + Detail + "）";
            return line;
        }
    }

    /// <summary>
    /// 弹窗事件汇（进程内单例）。
    ///
    /// 为什么需要它：弹窗实际是在三个地方被处理的——ModalAgent 的白名单规则（独立进程）、
    /// AI 桥接（Add-in 后台线程）、卡死看门狗（Add-in 线程池）——而最需要这份记录的消费方
    /// （批量翻译报告）完全在另一条流程上。这里做统一的线程安全收口：谁处理谁登记，
    /// 报告按图纸边界取走，就能说清"这张图期间哪个弹窗被自动处理了"。
    /// 在此之前，批量翻译的报告对弹窗处理是完全空白的：明明自动点了十几个框，报告里一个字都没有。
    ///
    /// 连续重复的同一条记录会被丢弃（看门狗对同一个未解决的现场会周期性复报），
    /// 容量有上限、满了丢最旧的，避免长批次把内存撑起来。
    /// </summary>
    public static class ModalEventFeed
    {
        private const int Capacity = 200;

        private static readonly object Sync = new object();
        private static readonly List<ModalEventRecord> Pending = new List<ModalEventRecord>();
        private static string _lastSignature = string.Empty;

        public static void Publish(string layer, string title, string action, string detail)
        {
            try
            {
                var record = new ModalEventRecord
                {
                    Layer = string.IsNullOrWhiteSpace(layer) ? "记录" : layer.Trim(),
                    Title = (title ?? string.Empty).Trim(),
                    Action = (action ?? string.Empty).Trim(),
                    Detail = (detail ?? string.Empty).Trim()
                };
                string signature = record.Layer + "\u0001" + record.Title + "\u0001" + record.Action + "\u0001" + record.Detail;

                lock (Sync)
                {
                    if (string.Equals(signature, _lastSignature, StringComparison.Ordinal)) return;
                    _lastSignature = signature;
                    Pending.Add(record);
                    while (Pending.Count > Capacity) Pending.RemoveAt(0);
                }
            }
            catch
            {
                // 事件登记是增强手段：失败绝不能影响弹窗处理本身。
            }
        }

        /// <summary>取走并清空待处理记录（已格式化）。没有任何记录时返回空列表。</summary>
        public static List<string> Drain()
        {
            var lines = new List<string>();
            try
            {
                lock (Sync)
                {
                    foreach (ModalEventRecord record in Pending) lines.Add(record.Format());
                    Pending.Clear();
                    _lastSignature = string.Empty;
                }
            }
            catch { }
            return lines;
        }
    }
}
