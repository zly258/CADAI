using System;
using Autodesk.AutoCAD.ApplicationServices;

namespace CadAI.Mcp.AutoCAD.Core
{
    /// <summary>
    /// 出图（PNG / PDF）统一压成"前台静默打印"。
    ///
    /// 为什么必须有这一层：
    ///   1. BACKGROUNDPLOT 打开时 PlotEngine 的 BeginPlot/EndPlot 只是把作业丢给后台打印队列就返回，
    ///      导致调用方看到"出图流程已结束"但文件未落盘，且下一次出图立刻撞上 eBackgroundPlotInProgress 卡死；
    ///   2. PLOTQUIET 默认值为 0，前台出图遇到警告（如非致命字体提示、图纸边界超限）会弹出模态对话框
    ///      （如「打印和发布详细信息」），阻塞主线程直到人工关闭；设为 1 静默记录不弹框；
    ///   3. FILEDIA 与 CMDDIA 设为 0，防止驱动或命令导出过程中弹出文件选择/命令行对话框。
    /// 出图结束后统一安全还原用户原有的系统变量设置。
    /// </summary>
    internal sealed class ForegroundPlotScope : IDisposable
    {
        private struct SysVarRecord
        {
            public string Name;
            public object Original;
            public bool Changed;
        }

        private readonly SysVarRecord[] _vars;

        private ForegroundPlotScope(SysVarRecord[] vars)
        {
            _vars = vars;
        }

        public static ForegroundPlotScope Enter()
        {
            var vars = new SysVarRecord[]
            {
                SetVar("BACKGROUNDPLOT", (short)0),
                SetVar("PLOTQUIET", (short)1),
                SetVar("FILEDIA", (short)0),
                SetVar("CMDDIA", (short)0)
            };

            return new ForegroundPlotScope(vars);
        }

        private static SysVarRecord SetVar(string name, object targetValue)
        {
            var record = new SysVarRecord { Name = name, Original = null, Changed = false };
            try { record.Original = Application.GetSystemVariable(name); }
            catch { }

            try
            {
                Application.SetSystemVariable(name, targetValue);
                record.Changed = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AutoCADMcp] {name} could not be set: {ex.Message}");
            }
            return record;
        }

        /// <summary>典型的后台打印队列占用错误；调用方据此决定是否短暂等待后重试一次。</summary>
        public static bool IsBackgroundPlotBusy(Exception ex)
        {
            return ex != null
                   && ex.Message != null
                   && ex.Message.IndexOf("eBackgroundPlotInProgress", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>在出图前等待残留后台作业收尾（最多 totalMs，分段轮询）。</summary>
        public static void WaitForBackgroundPlot(int totalMs)
        {
            const int stepMs = 250;
            for (int waited = 0; waited < totalMs; waited += stepMs)
                System.Threading.Thread.Sleep(stepMs);
        }

        public void Dispose()
        {
            if (_vars == null) return;
            for (int i = _vars.Length - 1; i >= 0; i--)
            {
                var v = _vars[i];
                if (!v.Changed || v.Original == null) continue;
                try { Application.SetSystemVariable(v.Name, v.Original); }
                catch { }
            }
        }
    }
}
