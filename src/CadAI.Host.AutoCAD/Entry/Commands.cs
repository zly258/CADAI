using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(CadAI.Host.AutoCAD.Commands))]

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// CADAI 智能助手的 AutoCAD 命令入口：
    ///   CADCHAT       打开 AI 对话助手窗口
    ///   CADCHATCFG    打开 AI 模型与语音设置窗口
    ///   CADWORKSPACE  打开对话工作区目录
    ///   CADABOUT      关于窗口
    ///   CADRIBBON     手动重新挂载 Ribbon 面板
    /// </summary>
    public class Commands
    {
        /// <summary>AI 对话助手（呼出基于 CadAI.Core + CadAI.Mcp.AutoCAD 的对话窗口）。</summary>
        [CommandMethod("CADCHAT", CommandFlags.Modal)]
        public void OpenAiChat()
        {
            try
            {
                CadAI.Host.AutoCAD.UI.DwgChatHostService.ToggleChatWindow();
            }
            catch (Exception ex)
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[CADAI] 打开失败: " + ex.Message);
            }
        }

        /// <summary>打开 AI 对话模型与语音输入设置窗口。</summary>
        [CommandMethod("CADCHATCFG", CommandFlags.Modal)]
        public void OpenAiSettings()
        {
            try
            {
                CadAI.Host.AutoCAD.UI.DwgChatHostService.EnsureInstalled();

                var options = new CadAI.Core.UnifiedSettingsOptions
                {
                    WindowTitle = CadAI.Core.UiLanguage.T("设置", "Settings"),
                    InitialTab = CadAI.Core.SettingsInitialTab.Chat
                };

                var win = new CadAI.Core.AiSettingsWindow(CadAI.Core.AiSettings.Load(), options);
                WindowService.OwnToAcad(win, modal: true);
                if (win.ShowDialog() != true) return;

                if (win.Settings != null)
                {
                    win.Settings.Save();
                    CadAI.Host.AutoCAD.UI.DwgChatHostService.ApplySettings(win.Settings);
                }
            }
            catch (Exception ex)
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[CADAI] 打开设置失败: " + ex.Message);
            }
        }

        /// <summary>打开当前助手的工作区目录（对话记录 / 截图 / 导出文件）。</summary>
        [CommandMethod("CADWORKSPACE", CommandFlags.Modal)]
        public void OpenWorkspace()
        {
            try
            {
                string dir = CadAI.Core.ChatCoreHost.RequireEnvironment().WorkspaceRoot;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex)
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[CADAI] 打开工作区失败: " + ex.Message);
            }
        }

        /// <summary>打开关于窗口。</summary>
        [CommandMethod("CADABOUT", CommandFlags.Modal)]
        public void About()
        {
            var win = new AboutWindow();
            win.ShowDialog();
        }

        /// <summary>在中英文界面语言之间一键切换，并实时刷新 Ribbon 与已打开的窗口。</summary>
        [CommandMethod("CADLANG", CommandFlags.Modal)]
        public void ToggleLanguage()
        {
            try
            {
                CadAI.Host.AutoCAD.UI.DwgChatHostService.EnsureInstalled();
                string next = CadAI.Core.UiLanguage.Toggle();
                var settings = CadAI.Core.AiSettings.Load();
                settings.UiLanguage = next;
                settings.Save();
                Ribbon.EnsureLoaded();

                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[CADAI] " + CadAI.Core.UiLanguage.T(
                    "界面语言已切换为：简体中文",
                    "UI language switched to: English"));
            }
            catch (Exception ex)
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n[CADAI] " + CadAI.Core.UiLanguage.T("切换语言失败: ", "Failed to switch language: ") + ex.Message);
            }
        }

        /// <summary>手动重新挂载 Ribbon 面板。</summary>
        [CommandMethod("CADRIBBON", CommandFlags.Modal)]
        public void LoadRibbon()
        {
            if (Ribbon.EnsureLoaded()) return;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                doc.Editor.WriteMessage(
                    "\n[CADAI] Ribbon 未挂载。可能原因：AutoCAD 的 Ribbon UI 尚未就绪（稍后再试），" +
                    "或当前工作空间未启用 Ribbon（执行 RIBBON 打开）。命令本身不受影响，可直接输入 CADCHAT 使用。");
            }
        }
    }
}
