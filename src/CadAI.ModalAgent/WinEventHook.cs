using System.Runtime.InteropServices;

namespace CadAI.ModalAgent
{
    /// <summary>
    /// 进程外 WinEvent 钩子：EVENT_OBJECT_SHOW 事件驱动地发现宿主弹窗。
    /// 用 WINEVENT_OUTOFCONTEXT——钩子回调在本进程执行，不注入宿主，
    /// 本进程无论发生什么（包括崩溃）都不影响宿主。
    /// </summary>
    internal sealed class WinEventHook : IDisposable
    {
        private const uint EVENT_OBJECT_SHOW = 0x8001;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint OBJID_WINDOW = 0x00000000;

        private delegate void WinEventProcDelegate(
            IntPtr hook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint eventThread, uint eventTime);

        private readonly WinEventProcDelegate _callback;   // 钉住防 GC 回收成野指针
        private readonly uint _ownerProcess;
        private readonly Action<IntPtr> _onWindowShown;
        private IntPtr _hook;
        private bool _disposed;

        public bool Started { get; private set; }

        public WinEventHook(uint ownerProcess, Action<IntPtr> onWindowShown)
        {
            _ownerProcess = ownerProcess;
            _onWindowShown = onWindowShown ?? throw new ArgumentNullException(nameof(onWindowShown));
            _callback = OnWinEvent;
        }

        /// <summary>必须在拥有消息泵的线程上调用（Program 主线程 / WinForms UI 线程）。</summary>
        public bool Start()
        {
            _hook = SetWinEventHook(
                EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW,
                IntPtr.Zero, _callback, _ownerProcess, 0, WINEVENT_OUTOFCONTEXT);
            Started = _hook != IntPtr.Zero;
            return Started;
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
        {
            try
            {
                if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
                _onWindowShown(hwnd);
            }
            catch
            {
                // 回调线程异常只崩本工具，不波及宿主；仍静默以防刷屏。
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_hook); } catch { }
                _hook = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProcDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    }
}
