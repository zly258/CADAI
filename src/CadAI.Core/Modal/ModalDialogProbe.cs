using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using CadAI.Core.Modal;

namespace CadAI.Core.Modal
{
    /// <summary>弹窗上的一个按钮（句柄 + 归一化标签）。</summary>
    public sealed class ModalButton
    {
        public IntPtr Hwnd;
        public string Label = string.Empty;
    }

    /// <summary>探测到的宿主模态框。</summary>
    public sealed class ModalDialogInfo
    {
        public IntPtr Hwnd;
        public string Title = string.Empty;
        public string Body = string.Empty;
        public string ClassName = string.Empty;
        public List<ModalButton> Buttons = new List<ModalButton>();

        public List<string> Labels()
        {
            var labels = new List<string>();
            foreach (ModalButton button in Buttons)
            {
                if (!string.IsNullOrWhiteSpace(button.Label) && !labels.Contains(button.Label)) labels.Add(button.Label);
            }
            return labels;
        }
    }

    /// <summary>
    /// 宿主模态框探测（只读），供卡死看门狗使用。
    ///
    /// 为什么 Add-in 要自己探测一遍，而不是等 ModalAgent 上报：看门狗要解决的正是
    /// "ModalAgent 没在跑 / 挂了 / 漏检"这一类场景——现场日志里出现过
    /// "未找到 ModalAgent.exe"，那次弹窗完全无人处理。等它上报就永远等不到。
    ///
    /// 判据（Win32 模态框的标准特征）：属于宿主进程 + 可见 + 自身可用 + 有 owner
    /// 且 owner 已被禁用 + 有标题 + 不是本插件自己的窗口。
    ///
    /// 最后一条必须靠类名和标题双重排除：聊天窗口是 AutoCAD 主窗口的 owned window
    /// （AttachOwner(MainFrameHWND)），主窗口被模态框禁用时它自己也满足"owner 被禁用"，
    /// 一旦误判就会去点插件自己的界面。它是 WPF 窗口（类名以 HwndWrapper 开头）
    /// 且标题带品牌名，两道过滤都能拦住。
    ///
    /// 注意：这里的探测结果**只**用于"读"和"点按钮"两件事，点击走跨进程
    /// BM_CLICK（与 ModalAgent 同一套机制），不重入 COM、不碰 UI 线程，
    /// 因此主线程停在模态循环里时依然可用。
    /// </summary>
    public static class ModalDialogProbe
    {
        private const int GwOwner = 4;
        private const uint BmClick = 0x00F5;
        private const uint SmtoAbortIfHung = 0x0002;

        // 这些标记用来**跳过宿主自己的窗口**：绝不能让"自动应答"去点 AI 助手自己的对话框。
        // 默认取自 Branding 单一来源（避免改了产品名却忘了改这里）+ 历史品牌名；
        // 宿主可追加自己的标记（例如旧版窗口标题），合并后生效。
        private static readonly string BrandMarker = Branding.ProductName;
        private const string LegacyBrandMarker = "AutoCAD AI";
        private static readonly List<string> ExtraIgnoreTitleMarkers = new List<string>();

        /// <summary>追加宿主专属的"跳过自身窗口"标题标记（大小写不敏感）。</summary>
        public static void AddIgnoreTitleMarkers(params string[] markers)
        {
            if (markers == null) return;
            lock (ExtraIgnoreTitleMarkers)
            {
                foreach (string marker in markers)
                {
                    if (!string.IsNullOrWhiteSpace(marker)) ExtraIgnoreTitleMarkers.Add(marker);
                }
            }
        }

        private static bool IsOwnWindow(string title)
        {
            if (title.IndexOf(BrandMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                // 宿主在 ChatCoreHostConfig.UiProductTitle 上配置的界面标题（如「CADAI 智能助手」）
                // 与窗口标题同源，这里必须一并忽略，否则看门狗会把自家弹窗当目标去点。
                string uiTitle = ChatCoreHostConfig.UiProductTitleOrDefault();
                if (!string.IsNullOrWhiteSpace(uiTitle) &&
                    title.IndexOf(uiTitle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            if (title.IndexOf(LegacyBrandMarker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            lock (ExtraIgnoreTitleMarkers)
            {
                foreach (string marker in ExtraIgnoreTitleMarkers)
                {
                    if (title.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        /// <summary>宿主进程号。Add-in 就运行在 AutoCAD 进程内，所以就是当前进程。</summary>
        public static int HostProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return 0; }
        }

        /// <summary>
        /// 取最上层的宿主动态模态框；没有则返回 null。EnumWindows 按 Z 序自上而下，
        /// 所以第一个命中的窗口就是最顶上那个（嵌套弹窗时正是先要处理的）。
        /// </summary>
        public static ModalDialogInfo Probe(int processId)
        {
            ModalDialogInfo found = null;
            try
            {
                EnumWindows((hwnd, lParam) =>
                {
                    if (found != null) return false;
                    if (!IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return true;

                    GetWindowThreadProcessId(hwnd, out uint ownerProcessId);
                    if (processId > 0 && ownerProcessId != (uint)processId) return true;

                    IntPtr owner = GetWindow(hwnd, GwOwner);
                    if (owner == IntPtr.Zero || IsWindowEnabled(owner)) return true;

                    string title = ReadText(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    if (IsOwnWindow(title)) return true;

                    string className = ReadClass(hwnd);
                    if (className.StartsWith("HwndWrapper", StringComparison.OrdinalIgnoreCase)) return true;

                    found = new ModalDialogInfo
                    {
                        Hwnd = hwnd,
                        Title = title,
                        ClassName = className,
                        Body = ReadBody(hwnd),
                        Buttons = ReadButtons(hwnd)
                    };
                    return false;
                }, IntPtr.Zero);
            }
            catch
            {
                return null;
            }
            return found;
        }

        /// <summary>
        /// 在当前弹窗上按标签定位按钮：四段递进匹配（见 <see cref="ModalDecisionSafety.MatchButton"/>）。
        /// 找不到返回 Zero——看门狗宁可不动，也不点一个"最接近"的按钮。
        /// </summary>
        public static IntPtr ResolveButton(ModalDialogInfo dialog, string label)
        {
            if (dialog == null || dialog.Buttons == null || string.IsNullOrWhiteSpace(label)) return IntPtr.Zero;

            var labels = new List<string>();
            foreach (ModalButton button in dialog.Buttons) labels.Add(button.Label);

            string matched = ModalDecisionSafety.MatchButton(labels, label);
            if (string.IsNullOrEmpty(matched)) return IntPtr.Zero;

            foreach (ModalButton button in dialog.Buttons)
            {
                if (string.Equals(button.Label, matched, StringComparison.Ordinal)) return button.Hwnd;
            }
            return IntPtr.Zero;
        }

        /// <summary>跨进程点击。带 250ms + ABORTIFHUNG 超时，绝不在卡死的窗口上无限等待。</summary>
        public static bool ClickButton(IntPtr buttonHwnd)
        {
            if (buttonHwnd == IntPtr.Zero) return false;
            try
            {
                SendMessageTimeout(buttonHwnd, BmClick, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 250, out _);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<ModalButton> ReadButtons(IntPtr dialog)
        {
            var buttons = new List<ModalButton>();
            try
            {
                EnumChildWindows(dialog, (child, lParam) =>
                {
                    if (!IsWindowVisible(child) || !IsWindowEnabled(child)) return true;
                    if (!string.Equals(ReadClass(child), "Button", StringComparison.OrdinalIgnoreCase)) return true;

                    string label = ModalDecisionSafety.NormalizeButtonText(ReadText(child));
                    if (label.Length == 0) return true;
                    if (ModalDecisionSafety.IsExpander(label)) return true;

                    foreach (ModalButton existing in buttons)
                    {
                        if (string.Equals(existing.Label, label, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    buttons.Add(new ModalButton { Hwnd = child, Label = label });
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return buttons;
        }

        private static string ReadBody(IntPtr dialog)
        {
            var text = new StringBuilder();
            try
            {
                EnumChildWindows(dialog, (child, lParam) =>
                {
                    string name = ReadClass(child);
                    if (name == "Static" || name == "Edit")
                    {
                        string value = ReadText(child);
                        if (!string.IsNullOrWhiteSpace(value)) text.Append(' ').Append(value);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return text.ToString();
        }

        private static string ReadText(IntPtr hwnd)
        {
            var buffer = new StringBuilder(2048);
            GetWindowText(hwnd, buffer, 2048);
            return buffer.ToString();
        }

        private static string ReadClass(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            GetClassName(hwnd, buffer, 256);
            return buffer.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    }
}
