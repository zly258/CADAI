using System;
using System.Net;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
// Autodesk.AutoCAD.Runtime 中也存在 Exception 类型，统一指回 System.Exception
using Exception = System.Exception;

namespace CadAI.Host.AutoCAD
{
    /// <summary>
    /// CADAI 智能助手的 AutoCAD 插件加载入口。
    /// 启动时挂接 Idle 事件以幂等挂载 Ribbon 面板，并在退出时清理对话窗口与弹窗看门狗。
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            StartupLog.Write("CADAI 智能助手 v" + VersionInfo.Version + " 已加载  ACADVER=" + AcadVer());

            try
            {
                var savedSettings = CadAI.Core.AiSettings.Load();
                if (savedSettings != null) CadAI.Core.UiLanguage.Set(savedSettings.UiLanguage);
            }
            catch { }

            try
            {
                ServicePointManager.DefaultConnectionLimit = 16;
            }
            catch (Exception ex)
            {
                Report("连接池设置失败（不影响对话功能）：" + ex.Message);
            }

            bool ribbonDone = false;
            bool updateChecked = false;

            try
            {
                AcadApp.Idle += delegate
                {
                    try
                    {
                        if (!ribbonDone) ribbonDone = Ribbon.EnsureLoaded();

                        if (!updateChecked)
                        {
                            updateChecked = true;
                            Updater.CheckAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Report("初始化失败：" + ex.Message);
                    }
                };
            }
            catch (Exception ex)
            {
                Report("Idle 事件挂接失败（欢迎信息与 Ribbon 都不会出现）：" + ex.Message);
            }
        }

        public void Terminate()
        {
            // 宿主退出时优雅停止弹窗看门狗并关闭对话窗口，避免子进程残留
            try { CadAI.Host.AutoCAD.Hosting.ModalAgentLauncher.Stop(); } catch { }
            try { CadAI.Host.AutoCAD.UI.DwgChatHostService.Close(); } catch { }
        }

        private static void Report(string msg)
        {
            try
            {
                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc != null) doc.Editor.WriteMessage("\n[CADAI] " + msg);
            }
            catch (Exception)
            {
            }
        }

        private static string AcadVer()
        {
            try
            {
                object v = AcadApp.GetSystemVariable("ACADVER");
                return v == null ? "?" : Convert.ToString(v);
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
