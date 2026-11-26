using System;
using System.Windows;
using System.Windows.Threading;

namespace CadAI.Core
{
    /// <summary>
    /// AiChatView 的语音输入部分：点击「语音」开始录音/听写，再次点击停止并自动填入输入框。
    /// </summary>
    public partial class AiChatView
    {
        private const int MaxVoiceDurationSeconds = 120;

        private VoiceInputService _voiceService;
        private DispatcherTimer _voiceTimer;
        private DateTime _voiceStartedAtUtc;

        private async void Voice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_voiceService != null && _voiceService.IsTranscribing)
                    return;

                if (_voiceService != null && _voiceService.IsRecording)
                {
                    await StopVoiceCaptureAsync();
                }
                else
                {
                    StartVoiceCapture();
                }
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.VoiceClick", ex);
                StopVoiceInputCleanup();
                ShowWarning("语音输入失败：" + ex.Message);
            }
        }

        private void StartVoiceCapture()
        {
            EnsureVoiceService();
            AiSettings current = _settings ?? AiSettings.Load();
            _voiceService.Start(current);

            _voiceStartedAtUtc = DateTime.UtcNow;
            if (_voiceTimer == null)
            {
                _voiceTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                _voiceTimer.Tick += VoiceTimer_Tick;
            }
            _voiceTimer.Start();

            UpdateVoiceButtonUi(isRecording: true, isTranscribing: false, hint: _voiceService.ActiveMode == "api" ? "正在录音，说完点击停止…" : "正在聆听，请直接说话…");
        }

        private async System.Threading.Tasks.Task StopVoiceCaptureAsync()
        {
            if (_voiceTimer != null) _voiceTimer.Stop();
            if (_voiceService == null)
            {
                UpdateVoiceButtonUi(false, false, null);
                return;
            }

            bool isApi = string.Equals(_voiceService.ActiveMode, "api", StringComparison.OrdinalIgnoreCase);
            if (isApi)
                UpdateVoiceButtonUi(isRecording: false, isTranscribing: true, hint: "正在识别语音…");
            else
                UpdateVoiceButtonUi(isRecording: false, isTranscribing: false, hint: null);

            try
            {
                await _voiceService.StopAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.VoiceTranscribe", ex);
                ShowWarning("语音识别失败：" + ex.Message);
            }
            finally
            {
                UpdateVoiceButtonUi(isRecording: false, isTranscribing: false, hint: null);
                FocusInput();
            }
        }

        private void EnsureVoiceService()
        {
            if (_voiceService != null) return;
            _voiceService = new VoiceInputService();
            _voiceService.TextCommitted += text => InvokeUi(() => InsertRecognizedVoiceText(text));
            _voiceService.PreviewChanged += preview => InvokeUi(() =>
            {
                if (_voiceService != null && _voiceService.IsRecording && VoiceHintText != null)
                {
                    VoiceHintText.Text = preview;
                    VoiceHintText.Visibility = Visibility.Visible;
                }
            });
            _voiceService.ErrorOccurred += err => InvokeUi(() =>
            {
                StopVoiceInputCleanup();
                ShowWarning("语音识别中断：" + err);
            });
        }

        private async void VoiceTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_voiceService == null || !_voiceService.IsRecording)
                {
                    _voiceTimer?.Stop();
                    return;
                }

                TimeSpan elapsed = DateTime.UtcNow - _voiceStartedAtUtc;
                if (VoiceHintText != null)
                {
                    VoiceHintText.Text = "录音中 " + elapsed.ToString(@"mm\:ss");
                    VoiceHintText.Visibility = Visibility.Visible;
                }

                if (elapsed.TotalSeconds >= MaxVoiceDurationSeconds)
                    await StopVoiceCaptureAsync();
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.VoiceTimer", ex);
            }
        }

        private void InsertRecognizedVoiceText(string text)
        {
            string cleaned = (text ?? string.Empty).Trim();
            if (cleaned.Length == 0 || InputBox == null) return;

            string existing = InputBox.Text ?? string.Empty;
            int caret = InputBox.SelectionStart;
            int selLen = InputBox.SelectionLength;

            if (caret >= 0 && caret <= existing.Length)
            {
                string before = existing.Substring(0, caret);
                string after = existing.Substring(caret + selLen);
                string inserted = before + cleaned + after;
                InputBox.Text = inserted;
                InputBox.SelectionStart = before.Length + cleaned.Length;
                InputBox.SelectionLength = 0;
            }
            else
            {
                InputBox.Text = existing.Length == 0 ? cleaned : existing + cleaned;
                InputBox.SelectionStart = InputBox.Text.Length;
            }

            FocusInput();
        }

        private void UpdateVoiceButtonUi(bool isRecording, bool isTranscribing, string hint)
        {
            if (VoiceButton != null)
            {
                VoiceButton.IsEnabled = !isTranscribing;
                if (isRecording)
                {
                    SetButtonIcon(VoiceButton, MaterialSymbols.StopCircle, "点击停止语音输入", 17);
                    ApplyStyle(VoiceButton, "Button.Icon.Danger");
                }
                else if (isTranscribing)
                {
                    SetButtonIcon(VoiceButton, MaterialSymbols.Mic, "正在将录音转为文字", 17);
                    ApplyStyle(VoiceButton, "Button.Icon.Tonal");
                }
                else
                {
                    SetButtonIcon(VoiceButton, MaterialSymbols.Mic, "语音输入：点击开始说话，再次点击停止（可在「设置 → 语音输入」切换本地识别或接口识别）", 17);
                    ApplyStyle(VoiceButton, "Button.Icon");
                }
            }

            if (VoiceHintText != null)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    VoiceHintText.Text = hint;
                    VoiceHintText.Visibility = Visibility.Visible;
                }
                else
                {
                    VoiceHintText.Text = string.Empty;
                    VoiceHintText.Visibility = Visibility.Collapsed;
                }
            }
        }

        internal void StopVoiceInputCleanup()
        {
            try { _voiceTimer?.Stop(); } catch { }
            try { _voiceService?.Cancel(); } catch { }
            try { _voiceService?.Dispose(); } catch { }
            _voiceService = null;
            try { UpdateVoiceButtonUi(false, false, null); } catch { }
        }
    }
}
