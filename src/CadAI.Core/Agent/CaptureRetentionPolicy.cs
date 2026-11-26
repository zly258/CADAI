#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace CadAI.Core
{
    /// <summary>
    /// 截图目录的保留策略（P7，2026-10-14）。
    ///
    /// Workspace\Captures 会随每次视觉校验/模型截图无限增长——实测曾积累
    /// 574 个文件 / 430 MB。截图是"当时看一眼"的证据，不是交付物，
    /// 这里按"最多保留 N 个文件"清理最旧的（Dialogs 子目录是 ModalAgent
    /// 的弹窗取证，同样适用）。
    ///
    /// 纯逻辑、无 COM/UI 依赖：静态方法便于单元测试覆盖边界。
    /// 任何失败都不抛出——清理是增强手段，绝不能让任务失败。
    /// </summary>
    internal static class CaptureRetentionPolicy
    {
        /// <summary>默认保留的文件数上限。</summary>
        public const int DefaultMaxFiles = 200;

        /// <summary>
        /// 清理目录里最旧的文件直到剩余不超过 <paramref name="maxFiles"/>。
        /// 只删 .png/.jpg 等图片扩展（防误删别的产出）；递归含子目录。
        /// 返回删除的文件数。
        /// </summary>
        public static int Enforce(string directory, int maxFiles = DefaultMaxFiles)
        {
            if (maxFiles <= 0) maxFiles = DefaultMaxFiles;
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0;

                var images = new List<FileInfo>();
                foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(path);
                    if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                        && !ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { images.Add(new FileInfo(path)); } catch { }
                }

                if (images.Count <= maxFiles) return 0;
                images.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                int deleted = 0;
                for (int i = 0; i < images.Count - maxFiles; i++)
                {
                    try { images[i].Delete(); deleted++; } catch { }
                }
                return deleted;
            }
            catch
            {
                return 0;
            }
        }
    }
}
