using System;
using System.IO;
using System.Text;
using CadAI.Core;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// 语音输入（WAV 编码/能量计算）与统一设置界面契约测试。
/// </summary>
public sealed class VoiceInputTests
{
    [Fact]
    public void WavEncoder_writes_valid_riff_wave_header_and_payload()
    {
        byte[] pcm = new byte[3200]; // 0.1s @ 16kHz 16-bit mono
        for (int i = 0; i < pcm.Length; i += 2)
        {
            short sample = 1000;
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        byte[] wav = WavEncoder.EncodePcm16(pcm);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(32000, BitConverter.ToInt32(wav, 28)); // byteRate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32)); // blockAlign
        Assert.Equal(16, BitConverter.ToInt16(wav, 34)); // bitsPerSample
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void WavEncoder_computes_rms_level_and_duration()
    {
        byte[] silence = new byte[320];
        Assert.Equal(0.0, WavEncoder.ComputeRmsLevel(silence, 0, silence.Length), 6);

        byte[] halfScale = new byte[320];
        for (int i = 0; i < halfScale.Length; i += 2)
        {
            short sample = 16384; // 0.5 of 32768
            halfScale[i] = (byte)(sample & 0xFF);
            halfScale[i + 1] = (byte)((sample >> 8) & 0xFF);
        }
        Assert.InRange(WavEncoder.ComputeRmsLevel(halfScale, 0, halfScale.Length), 0.49, 0.51);
        Assert.InRange(WavEncoder.EstimateDurationSeconds(32000), 0.99, 1.01);
    }

    [Fact]
    public void UnifiedSettingsWindow_has_sidebar_navigation_model_fetcher_and_voice()
    {
        string xaml = File.ReadAllText(RepoPaths.Resolve("src", "CadAI.Core", "UI", "Settings", "AiSettingsWindow.xaml"));
        Assert.Contains("x:Name=\"NavModelButton\"", xaml);
        Assert.Contains("x:Name=\"NavRuntimeButton\"", xaml);
        Assert.Contains("x:Name=\"NavVoiceButton\"", xaml);
        Assert.Contains("x:Name=\"FetchModelsButton\"", xaml);
        Assert.Contains("x:Name=\"AvailableModelsList\"", xaml);

        string chatXaml = File.ReadAllText(RepoPaths.Resolve("src", "CadAI.Core", "UI", "Chat", "AiChatView.xaml"));
        Assert.Contains("x:Name=\"VoiceButton\"", chatXaml);
    }

    [Fact]
    public void Ribbon_has_no_duplicate_translation_settings_button()
    {
        string cadRibbon = File.ReadAllText(RepoPaths.Resolve("src", "CadAI.Host.AutoCAD", "Entry", "Ribbon.cs"));
        Assert.DoesNotContain("\"翻译设置\"", cadRibbon);
        Assert.DoesNotContain("\"助手设置\"", cadRibbon);
    }
}
