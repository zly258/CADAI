using System;
using System.IO;
using System.Text;

namespace CadAI.Core
{
    /// <summary>
    /// 轻量 WAV（RIFF PCM）编码器与音频能量工具。
    /// 纯 BCL 实现，无 COM/平台依赖，可直接链接进离线单元测试工程验证。
    /// </summary>
    public static class WavEncoder
    {
        public const int DefaultSampleRate = 16000;
        public const short DefaultBitsPerSample = 16;
        public const short DefaultChannels = 1;

        /// <summary>
        /// 将原始 PCM 采样字节流封装为标准 44 字节 RIFF/WAVE 头 + 数据体。
        /// </summary>
        public static byte[] EncodePcm16(
            byte[] pcmData,
            int sampleRate = DefaultSampleRate,
            short channels = DefaultChannels,
            short bitsPerSample = DefaultBitsPerSample)
        {
            byte[] payload = pcmData ?? Array.Empty<byte>();
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (bitsPerSample != 8 && bitsPerSample != 16)
                throw new ArgumentOutOfRangeException(nameof(bitsPerSample));

            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));
            int dataSize = payload.Length;
            int riffChunkSize = 36 + dataSize;

            using (var ms = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            {
                // RIFF 块头
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(riffChunkSize);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt 子块（PCM = 1）
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write(bitsPerSample);

                // data 子块
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
                if (dataSize > 0)
                    writer.Write(payload, 0, dataSize);

                writer.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>
        /// 计算 16-bit 小端 PCM 样本的归一化 RMS 音量（0.0 ~ 1.0）。
        /// </summary>
        public static double ComputeRmsLevel(byte[] pcm16, int offset, int count)
        {
            if (pcm16 == null || count < 2) return 0.0;
            int end = Math.Min(pcm16.Length, offset + count);
            int samples = 0;
            double sumSquares = 0.0;

            for (int i = Math.Max(0, offset); i + 1 < end; i += 2)
            {
                short sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
                samples++;
            }

            if (samples == 0) return 0.0;
            return Math.Min(1.0, Math.Sqrt(sumSquares / samples));
        }

        /// <summary>
        /// 估算 PCM 字节流对应的录音时长（秒）。
        /// </summary>
        public static double EstimateDurationSeconds(
            int byteLength,
            int sampleRate = DefaultSampleRate,
            short channels = DefaultChannels,
            short bitsPerSample = DefaultBitsPerSample)
        {
            if (byteLength <= 0 || sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0) return 0.0;
            double bytesPerSecond = (double)sampleRate * channels * (bitsPerSample / 8.0);
            return byteLength / bytesPerSecond;
        }
    }
}
