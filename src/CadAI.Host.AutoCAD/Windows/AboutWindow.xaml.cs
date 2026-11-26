using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CadAI.Core;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// 简洁关于窗口：仅展示软件名称、版本、简短描述、开发者、项目地址与开源协议（GPL-3.0）。
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            WindowService.OwnToAcad(this, modal: true);
            WindowPlacement.CenterOnOwnerScreen(this);
            SourceInitialized += (s, e) => TryEnableDarkTitleBar();

            Title = UiLanguage.T("关于 CADAI", "About CADAI");
            ProductNameText.Text = VersionInfo.Product;
            VersionText.Text = UiLanguage.T("版本 v", "Version v") + VersionInfo.Version;
            DescLabel.Text = UiLanguage.T("描述", "Description");
            DescriptionText.Text = UiLanguage.T(
                "面向 Autodesk AutoCAD（2019–2026）的开源 CAD 智能对话、弹窗看门狗与 MCP 工具套件。",
                "Open-source AI chat assistant, modal dialog watchdog, and in-process MCP toolset for Autodesk AutoCAD (2019–2026).");
            DevLabel.Text = UiLanguage.T("开发者", "Developer");
            DeveloperText.Text = VersionInfo.Developer + " (GitHub)";
            DeveloperText.ToolTip = UiLanguage.T("在浏览器中打开开发者主页", "Open developer GitHub profile in browser");
            RepoLabel.Text = UiLanguage.T("项目地址", "Repository");
            RepoUrlText.Text = VersionInfo.RepositoryUrl;
            RepoUrlText.ToolTip = UiLanguage.T("在浏览器中打开 GitHub 仓库", "Open GitHub repository in browser");
            SponsorLabel.Text = UiLanguage.T("赞助支持", "Sponsor");
            SponsorText.Text = UiLanguage.T("爱发电 (Afdian)", "Afdian (Sponsor)");
            SponsorText.ToolTip = UiLanguage.T("通过爱发电支持项目持续维护", "Support ongoing development via Afdian");
            LicenseLabel.Text = UiLanguage.T("开源协议", "License");
            LicenseText.Text = VersionInfo.License;
            CloseButton.Content = UiLanguage.T("关闭", "Close");
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void DeveloperText_Click(object sender, MouseButtonEventArgs e) => OpenUrl(VersionInfo.DeveloperUrl);

        private void RepoUrlText_Click(object sender, MouseButtonEventArgs e) => OpenUrl(VersionInfo.RepositoryUrl);

        private void SponsorText_Click(object sender, MouseButtonEventArgs e) => OpenUrl(VersionInfo.SponsorUrl);

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TryEnableDarkTitleBar()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int dark = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
            }
            catch { }
        }
    }
}
