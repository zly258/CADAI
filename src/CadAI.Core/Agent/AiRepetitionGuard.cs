using System;
using System.Collections.Generic;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// 模型输出原地重复的检测器：用来掐断"思考内容反复吐同一段"的退化循环。
    ///
    /// 优化后的防误判判定策略：
    ///  1. **低熵过滤**：跳过 Markdown 分隔线（---- / ==== / ────）、表格边框或纯空白等低信息量窗口
    ///     （去空白后不同字符少于 6 种），杜绝单条分隔线触发误判。
    ///  2. **非重叠位置追踪**：同一哈希的命中位置间隔必须 ≥ WindowLength，避免重叠窗口自我计数。
    ///  3. **局部近似周期约束**：仅当最近 3 次非重叠命中的相邻间距均 ≤ 2048 字符，且两次间距近似一致
    ///     （较大间距 ≤ 2 × 较小间距 + 64）时才判定为原地死循环；相隔数千字的正常复述（如 CAD 图层规范、
    ///     参数模板或工具名）不会误判。
    /// </summary>
    internal sealed class RepetitionDetector
    {
        internal const int WindowLength = 64;
        internal const int MaxHits = 3;

        /// <summary>单次退化循环允许的最大周期跨度（字符）；超过此跨度的远距复述视为正常推理。</summary>
        private const int MaxLocalPeriod = 2048;

        /// <summary>窗口内去空白后的最小不同字符数；低于此值视为分隔线/装饰符而非语义死循环。</summary>
        private const int MinDistinctNonWhitespaceChars = 6;

        /// <summary>单次最多保留最近 64K 字符的缓冲区，避免超长思考占用过多内存。</summary>
        private const int MaxTrackedCharacters = 65536;

        private readonly StringBuilder _recent = new StringBuilder();
        private readonly Dictionary<ulong, List<int>> _windowPositions = new Dictionary<ulong, List<int>>();
        private int _baseOffset;
        private int _scanned;

        /// <summary>追加一段增量文本；返回 true 表示检测到局部周期性原地重复（调用方应中止请求）。</summary>
        public bool Append(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return false;

            _recent.Append(delta);
            for (int end = _scanned; end < _recent.Length; end++)
            {
                if (end < WindowLength - 1) continue;

                int start = end - WindowLength + 1;
                if (!HasSufficientEntropy(start)) continue;

                ulong hash = HashWindow(start);
                int absPos = _baseOffset + start;

                if (!_windowPositions.TryGetValue(hash, out List<int> positions))
                {
                    positions = new List<int>(4) { absPos };
                    _windowPositions[hash] = positions;
                    continue;
                }

                int lastPos = positions[positions.Count - 1];
                // 仅统计非重叠出现，避免同一段内相邻字符滑动窗口自我叠加计数
                if (absPos - lastPos < WindowLength) continue;

                positions.Add(absPos);
                // 只保留最近的若干次位置，避免超长流中列表无限增长
                if (positions.Count > MaxHits + 2)
                    positions.RemoveRange(0, positions.Count - (MaxHits + 2));

                if (IsLocalPeriodicLoop(positions))
                    return true;
            }

            _scanned = _recent.Length;
            Trim();
            return false;
        }

        private static bool IsLocalPeriodicLoop(List<int> positions)
        {
            int count = positions.Count;
            if (count < MaxHits) return false;

            int p2 = positions[count - 1];
            int p1 = positions[count - 2];
            int p0 = positions[count - 3];

            int gap1 = p1 - p0;
            int gap2 = p2 - p1;

            if (gap1 > MaxLocalPeriod || gap2 > MaxLocalPeriod) return false;

            int minGap = Math.Min(gap1, gap2);
            int maxGap = Math.Max(gap1, gap2);
            // 真实退化循环的重复间隔高度稳定；放宽至 2 倍 + 64 字符以容忍轻微变体
            return maxGap <= (minGap * 2) + WindowLength;
        }

        private bool HasSufficientEntropy(int start)
        {
            // 统计窗口内非空白不同字符数（最多检查到达到阈值即早退，开销极低）
            char[] seen = new char[MinDistinctNonWhitespaceChars];
            int distinct = 0;
            int end = start + WindowLength;
            for (int i = start; i < end; i++)
            {
                char c = _recent[i];
                if (char.IsWhiteSpace(c)) continue;

                bool exists = false;
                for (int j = 0; j < distinct; j++)
                {
                    if (seen[j] == c) { exists = true; break; }
                }
                if (exists) continue;

                seen[distinct++] = c;
                if (distinct >= MinDistinctNonWhitespaceChars) return true;
            }
            return false;
        }

        private void Trim()
        {
            if (_recent.Length <= MaxTrackedCharacters) return;

            // 保留最近 4096 字符以完整维持局部周期窗口的连续性
            int keep = Math.Min(_recent.Length, MaxLocalPeriod * 2);
            int removed = _recent.Length - keep;
            string tail = _recent.ToString(removed, keep);
            _recent.Clear();
            _recent.Append(tail);
            _baseOffset += removed;
            _scanned = _recent.Length;

            // 仅淘汰早于局部窗口下界的过旧哈希记录
            int minKeepPos = _baseOffset - MaxLocalPeriod;
            var staleKeys = new List<ulong>();
            foreach (var kv in _windowPositions)
            {
                List<int> list = kv.Value;
                if (list.Count == 0 || list[list.Count - 1] < minKeepPos)
                    staleKeys.Add(kv.Key);
            }
            foreach (ulong k in staleKeys) _windowPositions.Remove(k);
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
    /// 原样重发同一请求只会得到同样的循环，直接交给上层纠偏重试或用户处理。
    /// </summary>
    internal sealed class AiRepetitionException : Exception
    {
        public AiRepetitionException(string message) : base(message) { }
    }
}
