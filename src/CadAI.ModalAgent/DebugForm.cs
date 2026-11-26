namespace CadAI.ModalAgent
{
    /// <summary>调试窗口：显示执行记录与运行状态。仅 --debug 或配置 debug=true 时显示。</summary>
    internal sealed class DebugForm : Form
    {
        private readonly TextBox _logBox;
        private readonly Label _status;

        public DebugForm(string identity = "ModalAgent")
        {
            Text = $"ModalAgent[{identity}] 调试窗口";
            Width = 860;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            _status = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 4, 0, 0),
                Text = "启动中…"
            };

            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F)
            };

            Controls.Add(_logBox);
            Controls.Add(_status);
        }

        public void SetStatus(string text) => SafeInvoke(() => _status.Text = text);

        public void Log(string message) => SafeInvoke(() =>
        {
            _logBox.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        });

        private void SafeInvoke(Action action)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }
    }
}
