#nullable enable
using System;
using System.Diagnostics;
using System.Threading;

namespace CadAI.Core
{
    /// <summary>
    /// 「发现新版本」提示框：**非阻塞**弹窗 + 一键打开更新目录。两个宿主共用一套实现。
    ///
    /// 三条硬约束（都是踩过的坑）：
    ///  1. **不在 CAD 主线程做任何 IO**：共享盘不可达时 <c>Directory.Exists</c> / <c>File.Exists</c>
    ///     会一直等 SMB 超时（几十秒），在 UI 线程上就是整个 CAD 界面卡死。目录可达性一律由调用方
    ///     在后台线程判定好，作为 <paramref name="directoryReachable"/> 传进来；这里只负责弹框和开目录。
    ///  2. **不只依赖 WPF Dispatcher**：AutoCAD 宿主未必有 WPF <c>Application</c>（用户没开过对话窗口时
    ///     <c>Application.Current</c> 就是 null），只走 Dispatcher 会让提示永远不出现。这里先试宿主 UI
    ///     线程，取不到就自建一条 STA 线程弹框——不依赖宿主的任何 UI 设施。
    ///  3. **绝不抛出**：更新提示属于"可有可无"的功能。异常一旦逃到宿主线程就是跨线程未处理异常，
    ///     会直接把 CAD 进程带走；所以从入口到出口全兜住，失败就是"没提示"，不影响任何功能。
    /// </summary>
    public static class UpdatePrompt
    {
        /// <summary>
        /// 弹提示。调用方保证本方法**不在 CAD 主线程做 IO**（本方法自身也不做）。
        /// </summary>
        /// <param name="caption">标题（一般传产品名）。</param>
        /// <param name="message">正文。</param>
        /// <param name="directoryToOpen">点「是」时用资源管理器打开的目录。</param>
        /// <param name="directoryReachable">该目录当前是否可达（调用方在后台线程判定）。</param>
        public static void Show(string caption, string message, string directoryToOpen, bool directoryReachable)
        {
            try
            {
                System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                {
                    if (dispatcher.CheckAccess())
                    {
                        Prompt(caption, message, directoryToOpen, directoryReachable);
                    }
                    else
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                            Prompt(caption, message, directoryToOpen, directoryReachable)));
                    }
                    return;
                }
            }
            catch
            {
                // 宿主 UI 线程不可用，走下面的自建线程。
            }

            try
            {
                var thread = new Thread(() => Prompt(caption, message, directoryToOpen, directoryReachable))
                {
                    IsBackground = true,
                    Name = "CADAI-UpdatePrompt"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch
            {
                // 提示弹不出来不影响任何功能。
            }
        }

        private static void Prompt(string caption, string message, string directoryToOpen, bool directoryReachable)
        {
            try
            {
                if (ShowDialog(message, caption, System.Windows.Forms.MessageBoxButtons.YesNo) !=
                    System.Windows.Forms.DialogResult.Yes)
                {
                    return;
                }

                if (!directoryReachable)
                {
                    // 不再让资源管理器弹一个"找不到路径"的原始错误框——这里给一句人能看懂的话。
                    ShowDialog(
                        "更新目录当前不可访问：\n" + directoryToOpen + "\n\n请稍后重试，或直接联系维护人员。",
                        caption,
                        System.Windows.Forms.MessageBoxButtons.OK);
                    return;
                }

                OpenDirectory(directoryToOpen);
            }
            catch
            {
                // 同上：提示环节的任何失败都不向外抛。
            }
        }

        /// <summary>
        /// 屏幕居中的消息框：先在屏幕中心放一个 1x1 的隐形 WinForms 窗体当 owner，
        /// 消息框就会以它为中心显示。CAD 启动阶段宿主主窗口常常还没就绪，普通
        /// MessageBox.Show 只跟随活动窗口，位置不可控。
        /// </summary>
        private static System.Windows.Forms.DialogResult ShowDialog(
            string text, string caption, System.Windows.Forms.MessageBoxButtons buttons)
        {
            using (var host = new System.Windows.Forms.Form())
            {
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.PrimaryScreen;
                host.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                host.Location = new System.Drawing.Point(
                    screen.Bounds.Left + screen.Bounds.Width / 2,
                    screen.Bounds.Top + screen.Bounds.Height / 2);
                host.Size = new System.Drawing.Size(1, 1);
                host.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
                host.ShowInTaskbar = false;
                host.TopMost = true;
                host.Opacity = 0;
                host.Show();
                try
                {
                    return System.Windows.Forms.MessageBox.Show(
                        host, text, caption, buttons, System.Windows.Forms.MessageBoxIcon.Information);
                }
                finally
                {
                    host.Close();
                }
            }
        }

        /// <summary>
        /// 用资源管理器打开目录。**不做可达性检查**（那会阻塞），explorer 自己也不阻塞调用方：
        /// 路径含中文括号与空格，必须加引号，否则 explorer 会按空格切分参数。
        /// </summary>
        private static void OpenDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打不开就算了，不打扰用户。
            }
        }
    }
}