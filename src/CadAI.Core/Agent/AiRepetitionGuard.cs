using System;
using System.Collections.Generic;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// 模型输出原地重复的检测器：用来掐断"思考内容反复吐同一段"的退化循环。
    ///
    /// 为什么不能靠现有的轮次/工具预算：那种循环发生在**一次流式请求内部**，
    /// 模型只是不停往同一个 delta 流里写相同文字，不产生新的 tool_call，
    /// 轮次上限、no-progress 守卫、工具预算全都没有机会触发；请求头已到达后
    /// 客户端也没有正文超时，界面会一直显示思考直到用户手动点「停止」。
    ///
    /// 判定方式：对最近 64 个字符的滑动窗口做哈希，同一个窗口出现 3 次即认定重复。
    /// 只看"完全相同的一段文字重复出现"，不做语义/相似度判断——正常推理里同一句
    /// 64 字符的话连续出现三次几乎不可能，而退化循环会在几百字符内迅速满足条件。
    /// 短句循环（例如同一句 20 字反复）同样会被覆盖：窗口按字符滑动，同余位置的
    /// 窗口会周期性地落回同一个哈希。
    ///
    /// 阈值（64 / 3）刻意保守，宁可晚一点触发，也不误杀正常的重复强调。
    /// </summary>
    internal sealed class RepetitionDetector
    {
        internal const int WindowLength = 64;
        internal const int MaxHits = 3;

        /// <summary>
        /// 只保留最近 64K 字符的统计：退化循环的重复周期远小于它，清理一次不影响判定；
        /// 不设上限的话，一次超长思考会留下几十万条哈希记录。
        /// </summary>
        private const int MaxTrackedCharacters = 65536;

        private readonly StringBuilder _recent = new StringBuilder();
        private readonly Dictionary<ulong, int> _windowHits = new Dictionary<ulong, int>();
        private int _scanned;

        /// <summary>追加一段增量文本；返回 true 表示检测到原地重复（调用方应中止请求）。</summary>
        public bool Append(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return false;

            _recent.Append(delta);
            for (int end = _scanned; end < _recent.Length; end++)
            {
                if (end < WindowLength - 1) continue;

                ulong hash = HashWindow(end - WindowLength + 1);
                int hits;
                _windowHits.TryGetValue(hash, out hits);
                hits++;
                if (hits >= MaxHits) return true;
                _windowHits[hash] = hits;
            }

            _scanned = _recent.Length;
            Trim();
            return false;
        }

        private void Trim()
        {
            if (_recent.Length <= MaxTrackedCharacters) return;

            // 保留两倍窗口长度，保证下一段增量仍能与旧内容拼出完整窗口。
            int keep = WindowLength * 2;
            string tail = _recent.ToString(_recent.Length - keep, keep);
            _recent.Clear();
            _recent.Append(tail);
            _windowHits.Clear();
            _scanned = 0;
        }

        private ulong HashWindow(int start)
        {
            // FNV-1a（64 位）。只用于相等性统计，不需要加密强度。
            ulong hash = 14695981039346656037UL;
            int end = start + WindowLength;
            for (int i = start; i < end; i++)
            {
                hash ^= _recent[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    /// <summary>
    /// 模型流式输出陷入原地重复时抛出的异常。归类为**永久错误**：
    /// 原样重发同一请求只会得到同样的循环，直接交给模型回退链或用户处理。
    /// </summary>
    internal sealed class AiRepetitionException : Exception
    {
        public AiRepetitionException(string message) : base(message) { }
    }
}
