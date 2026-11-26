using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using CadAI.Core;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// CADAI 智能助手的受管 Ribbon 面板（不依赖 .cui 文件）。
    /// 布局：单页签 "AI"（Id = CADAI_TAB），包含「智能助手」与「设置」两个分组。
    /// </summary>
    public static class Ribbon
    {
        private const string TabId = "CADAI_TAB";
        private const string TabTitle = CadAI.Core.Branding.RibbonTabName;

        private static readonly ICommand Handler = new CommandHandler();

        private static readonly List<KeyValuePair<RibbonButton, RibbonIconKind>> Buttons =
            new List<KeyValuePair<RibbonButton, RibbonIconKind>>();

        private static bool _themeHooked;
        private static bool _darkTheme;

        /// <summary>幂等挂载 Ribbon。Ribbon 尚未就绪时返回 false，由调用方稍后重试。</summary>
        public static bool EnsureLoaded()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return false;

                for (int i = ribbon.Tabs.Count - 1; i >= 0; i--)
                {
                    RibbonTab old = ribbon.Tabs[i];
                    if (old != null && string.Equals(old.Id, TabId, StringComparison.OrdinalIgnoreCase))
                        ribbon.Tabs.RemoveAt(i);
                }

                var tab = new RibbonTab { Id = TabId, Title = TabTitle };
                _darkTheme = RibbonIconFactory.IsDarkTheme();
                Buttons.Clear();

                // 面板 1：CAD 智能对话
                var aiPanel = new RibbonPanel();
                var aiSource = new RibbonPanelSource
                {
                    Id = "CADAI_GRP",
                    Title = UiLanguage.T("CAD 智能对话", "CAD AI Chat")
                };
                AddButton(aiSource, "CADAI_B1", RibbonIconKind.AiChat,
                    UiLanguage.T("CAD 智能对话", "CAD AI Chat"), "CADCHAT",
                    UiLanguage.T(
                        "打开 CAD 智能对话窗口（命令 CADCHAT）。\n支持自然语言查图体检、字体修复、绘制编辑、工程标注、表格读写与 PDF 出图。",
                        "Open the CAD AI Chat window (command: CADCHAT).\nAudit drawings, repair fonts, edit geometry, add dimensions, read/write tables, and export PDFs via natural language."));
                aiPanel.Source = aiSource;
                tab.Panels.Add(aiPanel);

                // 面板 2：设置与工具（含中/英文语言切换）
                var cfgPanel = new RibbonPanel();
                var cfgSource = new RibbonPanelSource
                {
                    Id = "CADAI_CFG_GRP",
                    Title = UiLanguage.T("设置", "Settings")
                };
                AddButton(cfgSource, "CADAI_CFG_B1", RibbonIconKind.Settings,
                    UiLanguage.T("设置", "Settings"), "CADCHATCFG",
                    UiLanguage.T("打开对话模型、语音输入与弹窗守护设置（命令 CADCHATCFG）。",
                        "Configure model endpoint, voice input, and ModalAgent guard rules (command: CADCHATCFG)."));
                AddButton(cfgSource, "CADAI_CFG_LANG", RibbonIconKind.Translation,
                    UiLanguage.T("English", "中文"), "CADLANG",
                    UiLanguage.T("切换界面语言：中文 / English（命令 CADLANG）。",
                        "Switch UI language: English / 中文 (command: CADLANG)."));
                AddButton(cfgSource, "CADAI_CFG_B2", RibbonIconKind.About,
                    UiLanguage.T("关于", "About"), "CADABOUT",
                    UiLanguage.T("版本信息与开源协议（命令 CADABOUT）。",
                        "Version information and open-source license (command: CADABOUT)."));
                cfgPanel.Source = cfgSource;
                tab.Panels.Add(cfgPanel);

                ribbon.Tabs.Add(tab);

                HookThemeChange();

                int totalButtons = aiSource.Items.Count + cfgSource.Items.Count;
                StartupLog.Write("Ribbon 已挂载（2 个面板 / " + totalButtons + " 个按钮，"
                    + (_darkTheme ? "暗色" : "亮色") + "主题）");
                return true;
            }
            catch (Exception ex)
            {
                StartupLog.Write("Ribbon 挂载失败：" + ex.Message);
                Report("Ribbon 挂载失败：" + ex.Message + "。可执行 CADRIBBON 重试。");
                return false;
            }
        }

        private static void HookThemeChange()
        {
            if (_themeHooked) return;
            _themeHooked = true;
            try
            {
                AcadApp.SystemVariableChanged += OnSystemVariableChanged;
            }
            catch (Exception ex)
            {
                StartupLog.Write("主题监听挂接失败（切主题后图标需重启 AutoCAD 才更新）：" + ex.Message);
            }
        }

        private static void OnSystemVariableChanged(object sender, SystemVariableChangedEventArgs e)
        {
            try
            {
                if (e == null || !string.Equals(e.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase)) return;

                bool dark = RibbonIconFactory.IsDarkTheme();
                if (dark == _darkTheme) return;
                _darkTheme = dark;

                foreach (var pair in Buttons)
                {
                    RibbonButton button = pair.Key;
                    if (button == null) continue;
                    try
                    {
                        button.LargeImage = RibbonIconFactory.Create(32, pair.Value, dark);
                        button.Image = RibbonIconFactory.Create(16, pair.Value, dark);
                    }
                    catch (Exception) { }
                }
                StartupLog.Write("主题切换为" + (dark ? "暗色" : "亮色") + "，Ribbon 图标已重画");
            }
            catch (Exception)
            {
            }
        }

        private static void AddButton(RibbonPanelSource source, string id, RibbonIconKind kind, string text,
                                      string command, string tooltip)
        {
            var button = new RibbonButton
            {
                Id               = id,
                Text             = text,
                ShowText         = true,
                ShowImage        = true,
                Size             = RibbonItemSize.Large,
                Orientation      = Orientation.Vertical,
                CommandHandler   = Handler,
                CommandParameter = command,
                ToolTip          = tooltip
            };

            try
            {
                button.LargeImage = RibbonIconFactory.Create(32, kind, _darkTheme);
                button.Image      = RibbonIconFactory.Create(16, kind, _darkTheme);
                if (button.LargeImage == null && button.Image == null) button.ShowImage = false;
            }
            catch (Exception)
            {
                button.ShowImage = false;
            }

            Buttons.Add(new KeyValuePair<RibbonButton, RibbonIconKind>(button, kind));
            source.Items.Add(button);
        }

        private static void Report(string msg)
        {
            try
            {
                Document d = AcadApp.DocumentManager.MdiActiveDocument;
                if (d != null) d.Editor.WriteMessage("\n[CADAI] " + msg);
            }
            catch (Exception) { }
        }

        private class CommandHandler : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }

            public bool CanExecute(object parameter) { return true; }

            public void Execute(object parameter)
            {
                string command = null;
                RibbonButton button = parameter as RibbonButton;
                if (button != null) command = button.CommandParameter as string;
                if (string.IsNullOrEmpty(command)) command = parameter as string;
                if (string.IsNullOrEmpty(command)) return;

                try
                {
                    Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                    if (doc == null) return;
                    doc.SendStringToExecute(command.TrimEnd() + " ", true, false, true);
                }
                catch (Exception ex)
                {
                    StartupLog.Write("按钮命令发送失败：" + command + " / " + ex.Message);
                }
            }
        }
    }
}
