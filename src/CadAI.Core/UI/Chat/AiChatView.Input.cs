using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CadAI.Core
{
    public partial class AiChatView
    {
        private bool _inputKeyHandlerInstalled;

        private void UnifiedLoaded(object sender, RoutedEventArgs e)
        {
            AiChatView_Loaded(sender, e);
            InstallInputKeyHandler();
            SizeChanged += AiChatView_SizeChanged;
        }

        private void UnifiedUnloaded(object sender, RoutedEventArgs e)
        {
            StopVoiceInputCleanup();
            SizeChanged -= AiChatView_SizeChanged;
            ClearPendingImage();
        }

        private void InstallInputKeyHandler()
        {
            if (_inputKeyHandlerInstalled || InputBox == null) return;
            InputBox.AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(UnifiedInputBox_KeyDown), true);
            _inputKeyHandlerInstalled = true;
        }

        private async void UnifiedSend_Click(object sender, RoutedEventArgs e)
        {
            // async void 事件处理器是 WPF 的既定模式，但未被捕获的异常会直接终止进程，
            // 因此统一兜底记录，避免一次偶发失败拖垮整个 AutoCAD。
            try { await SendUnifiedWithAttachmentsAsync(); }
            catch (Exception ex) { AiExceptionGuard.Log("Chat.UnifiedSend", ex); }
        }

        private async void UnifiedInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.V)
                {
                    if (TryPasteImageFromClipboard()) { e.Handled = true; return; }
                }
                if (e.Key != Key.Enter) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;
                e.Handled = true;
                await SendUnifiedWithAttachmentsAsync();
            }
            catch (Exception ex) { AiExceptionGuard.Log("Chat.UnifiedInputKeyDown", ex); }
        }

        // ===== 输入区高度 =====
        // 默认 Height=Auto：随内容生长到 TextBox.Multiline 的 MaxHeight（180）。
        // 用户拖动顶部把手后改为固定像素高度（只在本次窗口生命周期内有效，不落盘）；
        // 双击把手清掉本地值，回到「随内容自动生长」。
        private const double InputResizeMinHeight = 80;
        private const double InputResizeHardMaxHeight = 420;
        /// <summary>顶栏 / 输入区其余行 / 版权 / 至少两行消息的固定余量：手动拉高时给它留位。</summary>
        private const double InputResizeChromeReserve = 330;

        /// <summary>拖动过程中的当前高度。DragDelta 给的是增量，必须自己累计。</summary>
        private double _inputResizeHeight;

        private void InputResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (InputBox == null) return;
            // 起手把 Auto 高度固化成当前实际像素，避免第一下拖动跳变。
            _inputResizeHeight = InputBox.ActualHeight;
            InputBox.Height = _inputResizeHeight;
        }

        private void InputResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (InputBox == null) return;
            double max = ResolveInputResizeMaxHeight();
            // 向上拖（VerticalChange 为负）= 输入区变高。
            double target = Math.Max(InputResizeMinHeight, Math.Min(max, _inputResizeHeight - e.VerticalChange));
            _inputResizeHeight = target;
            // 显式覆盖样式里的 MaxHeight=180：手动拉高后不能被样式上限压回去。
            InputBox.MaxHeight = max;
            InputBox.Height = target;
        }

        private void InputResizeGrip_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (InputBox == null) return;
            // 清掉本地值：样式里的 Height=Auto / MaxHeight 重新生效。
            InputBox.ClearValue(FrameworkElement.HeightProperty);
            InputBox.ClearValue(FrameworkElement.MaxHeightProperty);
            _inputResizeHeight = 0;
            e.Handled = true;
        }

        /// <summary>手动拉高的上限：先扣掉固定 chrome，再夹到硬上限，保证消息区始终留得下。</summary>
        private double ResolveInputResizeMaxHeight()
        {
            double view = ActualHeight;
            double cap = view > 0 ? view - InputResizeChromeReserve : InputResizeHardMaxHeight;
            return Math.Max(InputResizeMinHeight, Math.Min(InputResizeHardMaxHeight, cap));
        }

        /// <summary>窗口变矮时把固定高度一起压回来，否则输入区会把消息区挤成 0。</summary>
        private void AiChatView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (InputBox == null || double.IsNaN(InputBox.Height)) return;
            double max = ResolveInputResizeMaxHeight();
            InputBox.MaxHeight = max;
            if (InputBox.Height <= max) return;
            InputBox.Height = max;
            _inputResizeHeight = max;
        }

        private void AiChatView_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void AiChatView_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;

                int addedImages = 0;
                var otherFiles = new System.Collections.Generic.List<string>();
                foreach (string file in files)
                {
                    if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file)) continue;
                    if (IsSupportedImageFile(file))
                    {
                        if (_pendingImages.Count < MaxPendingImages && TryAddImageFromFile(file)) addedImages++;
                        else otherFiles.Add(file);
                    }
                    else if (IsSupportedPathAttachment(file))
                    {
                        otherFiles.Add(file);
                    }
                }
                if (otherFiles.Count > 0) AddPendingPaths(otherFiles);
                else if (addedImages > 0) UpdateAttachmentPreviewUi();
                e.Handled = true;
                FocusInput();
            }
            catch (Exception ex)
            {
                AiExceptionGuard.Log("Chat.DropFiles", ex);
            }
        }
    }
}
