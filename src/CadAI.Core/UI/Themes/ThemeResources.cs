using System;
using System.Windows;

namespace CadAI.Core
{
    /// <summary>
    /// 主题资源在「可视化树之外」的取用入口。
    ///
    /// 背景：主题字典（UI/Themes/Theme.xaml）是按窗口 / 按视图挂载的——AboutWindow、AiChatView
    /// 各自在自己的 Resources 里 Merge 一份，宿主没有应用级主题字典。于是任何在**控件挂进窗口之前**
    /// 就渲染的主题消费者（MarkdownTextBlock 最典型：构造时赋 Markdown 会立即渲染首帧），
    /// TryFindResource 一律取不到值，只能退到 WPF 默认字号（12）和系统字体；
    /// 等控件进入可视化树、资源可达后再重排一次，用户看到的就是"先小后大"。
    ///
    /// 本类用一份**静态缓存的 Theme.xaml** 兜住这段空窗期：先问应用级字典（宿主若挂了全局主题，
    /// 以宿主的为准），再问这份静态字典。字号、字体、色值仍然只有 Typography/Colors 一个来源，
    /// 代码里不重复写第二份数字。
    /// </summary>
    internal static class ThemeResources
    {
        private const string ThemeDictionaryUri = "pack://application:,,,/CadAI.Core;component/UI/Themes/Theme.xaml";

        private static readonly object Gate = new object();
        private static ResourceDictionary _dictionary;
        private static bool _loadFailed;

        /// <summary>取一个主题值；取不到返回 null，由调用方自行兜底。</summary>
        internal static object Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            // 1) 应用级资源：宿主若挂了全局主题字典，优先听宿主的。
            try
            {
                Application app = Application.Current;
                object fromApp = app?.TryFindResource(key);
                if (fromApp != null) return fromApp;
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Theme.AppResource." + key, error);
            }

            // 2) 静态主题字典：控件还没挂进窗口时，这是唯一能拿到最终值的地方。
            ResourceDictionary dictionary = LoadDictionary();
            if (dictionary == null) return null;
            try
            {
                return Freeze(dictionary.Contains(key) ? dictionary[key] : null);
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Theme.DictResource." + key, error);
                return null;
            }
        }

        /// <summary>取一个主题值并转成 <typeparamref name="T"/>（引用类型），取不到或类型不符返回 null。</summary>
        internal static T Find<T>(string key) where T : class => Find(key) as T;

        /// <summary>
        /// 交给界面的 Freezable（画刷）先冻结。静态字典实际只在 UI 线程被首次触碰，
        /// 但冻结一次就彻底没有"另一个线程拥有该对象"的隐患，跨控件共享也更省内存。
        /// </summary>
        private static object Freeze(object value)
        {
            var freezable = value as Freezable;
            if (freezable == null || freezable.IsFrozen) return value;
            try
            {
                if (freezable.CanFreeze) freezable.Freeze();
            }
            catch (Exception error)
            {
                AiExceptionGuard.Log("Theme.Freeze", error);
            }
            return value;
        }

        private static ResourceDictionary LoadDictionary()
        {
            if (_dictionary != null || _loadFailed) return _dictionary;
            lock (Gate)
            {
                if (_dictionary != null || _loadFailed) return _dictionary;
                try
                {
                    _dictionary = new ResourceDictionary { Source = new Uri(ThemeDictionaryUri, UriKind.Absolute) };
                }
                catch (Exception error)
                {
                    // pack 方案未注册之类的极端情况：记一次日志后永久放弃，
                    // 绝不让它把渲染拖垮——调用方本来就有兜底值。
                    _loadFailed = true;
                    AiExceptionGuard.Log("Theme.LoadDictionary", error);
                }
                return _dictionary;
            }
        }
    }
}
