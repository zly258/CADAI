using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace CadAI.Core
{
    internal sealed class AiChatHost : UserControl
    {
        private readonly ElementHost _elementHost;
        // Phase 2：不再传入 AutoCAD.Application；宿主能力（工具执行、模态守卫）经 ChatCoreHost 注入。
        public AiChatHost()
        {
            // 与 ChatCore 的 Color.WindowBackground 一致：WinForms 侧用纯白会在
            // ElementHost 边缘露出一条刺眼的白边，视觉上像窗口"没铺满"。
            BackColor = System.Drawing.Color.FromArgb(0xE9, 0xED, 0xF2);
            BorderStyle = BorderStyle.None;
            Dock = DockStyle.Fill;
            View = new AiChatView();
            AiExceptionGuard.Install(View.Dispatcher);
            _elementHost = new ElementHost { Dock = DockStyle.Fill, BackColor = BackColor, Child = View };
            Controls.Add(_elementHost);
        }
        public AiChatView View { get; }
        public IntPtr EnsureHandle() { if (!IsHandleCreated) CreateControl(); return Handle; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _elementHost.Child = null; } catch { } try { View.Shutdown(); } catch { } try { _elementHost.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
