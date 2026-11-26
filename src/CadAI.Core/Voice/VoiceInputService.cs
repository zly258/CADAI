using System;
using System.Threading;
using System.Threading.Tasks;

namespace CadAI.Core
{
    /// <summary>
    /// AI 对话语音输入统一门面：封装「Windows 本地语音识别」与「OpenAI 兼容语音转文字接口」两种模式。
    /// </summary>
    internal sealed class VoiceInputService : IDisposable
    {
        private readonly object _gate = new object();
        private WaveAudioRecorder _recorder;
        private WindowsSpeechRecognizer _localRecognizer;
        private CancellationTokenSource _transcribeCts;
        private AiSettings _activeSettings;
        private string _activeMode = "local";
        private bool _disposed;

        /// <summary>识别到确定文本（本地模式逐句触发；接口模式停止录音后触发一次）。</summary>
        public event Action<string> TextCommitted;

        /// <summary>本地听写过程中的实时候选预览。</summary>
        public event Action<string> PreviewChanged;

        /// <summary>运行错误（麦克风不可用、接口失败等）。</summary>
        public event Action<string> ErrorOccurred;

        public bool IsRecording { get; private set; }
        public bool IsTranscribing { get; private set; }
        public string ActiveMode => _activeMode;

        /// <summary>按当前配置启动语音输入。</summary>
        public void Start(AiSettings settings)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VoiceInputService));
            if (IsRecording || IsTranscribing) return;

            _activeSettings = (settings ?? AiSettings.Load()).Clone();
            _activeMode = AiSettings.NormalizeVoiceInputMode(_activeSettings.VoiceInputMode);

            if (_activeMode == "api")
            {
                _recorder = new WaveAudioRecorder();
                _recorder.Start();
                IsRecording = true;
            }
            else
            {
                _localRecognizer = new WindowsSpeechRecognizer();
                _localRecognizer.TextRecognized += text => TextCommitted?.Invoke(text);
                _localRecognizer.TextHypothesized += preview => PreviewChanged?.Invoke(preview);
                _localRecognizer.ErrorOccurred += err =>
                {
                    IsRecording = false;
                    ErrorOccurred?.Invoke(err);
                };
                _localRecognizer.Start(_activeSettings.VoiceLanguage);
                IsRecording = true;
            }
        }

        /// <summary>停止语音输入；接口模式下自动上传音频并异步返回识别文本。</summary>
        public async Task<string> StopAsync()
        {
            if (!IsRecording) return string.Empty;
            IsRecording = false;

            if (_activeMode == "local")
            {
                WindowsSpeechRecognizer local = _localRecognizer;
                _localRecognizer = null;
                try { local?.Stop(); local?.Dispose(); } catch { }
                return string.Empty;
            }

            WaveAudioRecorder recorder = _recorder;
            _recorder = null;
            byte[] wavBytes = Array.Empty<byte>();
            try
            {
                if (recorder != null)
                {
                    wavBytes = recorder.StopAndGetWav();
                    recorder.Dispose();
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Voice.StopRecorder", ex);
            }

            if (wavBytes == null || wavBytes.Length <= 44)
                return string.Empty;

            // 过短录音（< 0.25s）直接忽略，避免误触浪费请求
            if (WavEncoder.EstimateDurationSeconds(wavBytes.Length - 44) < 0.25)
                return string.Empty;

            IsTranscribing = true;
            _transcribeCts = new CancellationTokenSource();
            try
            {
                AiSettings cfg = _activeSettings ?? AiSettings.Load();
                string baseUrl = string.IsNullOrWhiteSpace(cfg.VoiceBaseUrl) ? cfg.BaseUrl : cfg.VoiceBaseUrl;
                string apiKey = string.IsNullOrWhiteSpace(cfg.VoiceApiKey) && string.IsNullOrWhiteSpace(cfg.VoiceBaseUrl)
                    ? cfg.ApiKey
                    : cfg.VoiceApiKey;

                string text = await VoiceTranscriptionClient.TranscribeAsync(
                    wavBytes,
                    baseUrl,
                    apiKey,
                    cfg.VoiceModel,
                    cfg.VoiceLanguage,
                    _transcribeCts.Token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text))
                    TextCommitted?.Invoke(text);
                return text;
            }
            finally
            {
                IsTranscribing = false;
                lock (_gate)
                {
                    _transcribeCts?.Dispose();
                    _transcribeCts = null;
                }
            }
        }

        /// <summary>立即取消正在进行的录音或转写。</summary>
        public void Cancel()
        {
            IsRecording = false;
            IsTranscribing = false;
            lock (_gate)
            {
                try { _transcribeCts?.Cancel(); } catch { }
            }
            try { _localRecognizer?.Dispose(); } catch { }
            _localRecognizer = null;
            try { _recorder?.Dispose(); } catch { }
            _recorder = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cancel();
        }
    }
}
