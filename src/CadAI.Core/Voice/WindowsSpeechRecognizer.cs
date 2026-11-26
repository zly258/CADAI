using System;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;

namespace CadAI.Core
{
    /// <summary>
    /// 基于 Windows 内置 System.Speech 的离线本地语音听写引擎。
    /// 无需联网、无需外部服务；若当前系统未安装语音识别语言包，会给出明确可操作的提示。
    /// </summary>
    internal sealed class WindowsSpeechRecognizer : IDisposable
    {
        private SpeechRecognitionEngine _engine;
        private bool _disposed;

        /// <summary>识别出一段确定的文本。</summary>
        public event Action<string> TextRecognized;

        /// <summary>识别出临时候选文本（说话过程中的实时预览）。</summary>
        public event Action<string> TextHypothesized;

        /// <summary>后台识别器异常时回调。</summary>
        public event Action<string> ErrorOccurred;

        public bool IsListening { get; private set; }

        /// <summary>检查系统是否安装了任意可用的 Windows 语音识别器。</summary>
        public static bool IsAvailable(out string summary)
        {
            try
            {
                var installed = SpeechRecognitionEngine.InstalledRecognizers();
                if (installed == null || installed.Count == 0)
                {
                    summary = "系统未安装 Windows 语音识别语言包";
                    return false;
                }
                summary = "已检测到 " + installed.Count + " 个本地语音引擎（"
                    + string.Join("、", installed.Select(r => r.Culture.DisplayName).Distinct()) + "）";
                return true;
            }
            catch (Exception ex)
            {
                summary = "本地语音引擎不可用：" + ex.Message;
                return false;
            }
        }

        public void Start(string languageCode)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsSpeechRecognizer));
            if (IsListening) return;

            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed == null || installed.Count == 0)
            {
                throw new InvalidOperationException(
                    "当前系统未安装 Windows 本地语音识别语言包。请在「Windows 设置 → 时间和语言 → 语音」安装中文语音包，或在「设置 → 语音输入」切换为语音识别接口模式。");
            }

            RecognizerInfo chosen = ChooseRecognizer(installed, languageCode);
            _engine = new SpeechRecognitionEngine(chosen);
            try
            {
                _engine.LoadGrammar(new DictationGrammar());
                _engine.SetInputToDefaultAudioDevice();
                _engine.SpeechRecognized += Engine_SpeechRecognized;
                _engine.SpeechHypothesized += Engine_SpeechHypothesized;
                _engine.RecognizeCompleted += Engine_RecognizeCompleted;
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                IsListening = true;
            }
            catch (InvalidOperationException ex)
            {
                DisposeEngine();
                throw new InvalidOperationException(
                    "无法启动本地语音识别（请检查默认麦克风是否可用）：" + ex.Message, ex);
            }
            catch
            {
                DisposeEngine();
                throw;
            }
        }

        public void Stop()
        {
            if (!IsListening && _engine == null) return;
            IsListening = false;
            DisposeEngine();
        }

        private static RecognizerInfo ChooseRecognizer(
            System.Collections.ObjectModel.ReadOnlyCollection<RecognizerInfo> installed,
            string languageCode)
        {
            string normalized = AiSettings.NormalizeVoiceLanguage(languageCode);
            if (normalized == "en")
            {
                var en = installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase));
                if (en != null) return en;
            }
            else if (normalized == "zh")
            {
                var zh = installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase));
                if (zh != null) return zh;
            }

            // 回退：优先当前 UI 文化，其次第一个已安装引擎
            var ui = installed.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture));
            return ui ?? installed[0];
        }

        private void Engine_SpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            if (e?.Result == null) return;
            string text = (e.Result.Text ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                try { TextRecognized?.Invoke(text); } catch { }
            }
        }

        private void Engine_SpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            if (e?.Result == null) return;
            string text = (e.Result.Text ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                try { TextHypothesized?.Invoke(text); } catch { }
            }
        }

        private void Engine_RecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (e?.Error != null && IsListening)
            {
                IsListening = false;
                try { ErrorOccurred?.Invoke(e.Error.Message); } catch { }
            }
        }

        private void DisposeEngine()
        {
            if (_engine == null) return;
            try { _engine.SpeechRecognized -= Engine_SpeechRecognized; } catch { }
            try { _engine.SpeechHypothesized -= Engine_SpeechHypothesized; } catch { }
            try { _engine.RecognizeCompleted -= Engine_RecognizeCompleted; } catch { }
            try { _engine.RecognizeAsyncCancel(); } catch { }
            try { _engine.Dispose(); } catch { }
            _engine = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsListening = false;
            DisposeEngine();
        }
    }
}
