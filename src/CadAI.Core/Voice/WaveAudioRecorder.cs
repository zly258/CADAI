using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CadAI.Core
{
    /// <summary>
    /// 基于 Windows 原生 WinMM（winmm.dll）的轻量麦克风录音器。
    /// 零第三方依赖，采集 16kHz / 16-bit / 单声道 PCM 数据，供 OpenAI 兼容语音转文字接口使用。
    /// </summary>
    internal sealed class WaveAudioRecorder : IDisposable
    {
        private const int WaveMapper = -1;
        private const int CallbackFunction = 0x00030000;
        private const int WimData = 0x3C0;
        private const int BufferCount = 4;
        private const int BufferDurationMs = 150;

        private readonly object _gate = new object();
        private readonly WaveInProc _callback;
        private IntPtr _waveInHandle = IntPtr.Zero;
        private MemoryStream _pcmBuffer;
        private BufferSlot[] _slots;
        private bool _isRecording;
        private bool _stopping;
        private bool _disposed;

        /// <summary>采集到新音频帧时回调实时音量（0.0 ~ 1.0）。</summary>
        public event Action<double> LevelChanged;

        public bool IsRecording
        {
            get { lock (_gate) { return _isRecording; } }
        }

        public WaveAudioRecorder()
        {
            // 持有委托引用，防止非托管回调期间被 GC 回收。
            _callback = OnWaveInCallback;
        }

        /// <summary>检测系统是否存在可用的麦克风录音设备。</summary>
        public static int GetInputDeviceCount()
        {
            try { return (int)waveInGetNumDevs(); }
            catch { return 0; }
        }

        /// <summary>开始麦克风录音。</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WaveAudioRecorder));
                if (_isRecording) return;

                if (GetInputDeviceCount() <= 0)
                    throw new InvalidOperationException("未检测到可用的麦克风设备，请检查系统录音设置。");

                var format = new WaveFormatEx
                {
                    FormatTag = 1, // PCM
                    Channels = WavEncoder.DefaultChannels,
                    SamplesPerSec = WavEncoder.DefaultSampleRate,
                    BitsPerSample = WavEncoder.DefaultBitsPerSample,
                    BlockAlign = (short)(WavEncoder.DefaultChannels * (WavEncoder.DefaultBitsPerSample / 8)),
                    AvgBytesPerSec = WavEncoder.DefaultSampleRate * WavEncoder.DefaultChannels * (WavEncoder.DefaultBitsPerSample / 8),
                    Size = 0
                };

                int openResult = waveInOpen(out _waveInHandle, WaveMapper, ref format, _callback, IntPtr.Zero, CallbackFunction);
                if (openResult != 0 || _waveInHandle == IntPtr.Zero)
                {
                    _waveInHandle = IntPtr.Zero;
                    throw new InvalidOperationException("无法打开麦克风设备（错误码 " + openResult + "），请检查麦克风权限或占用情况。");
                }

                _pcmBuffer = new MemoryStream();
                _stopping = false;
                int bufferBytes = format.AvgBytesPerSec * BufferDurationMs / 1000;
                // 保证偶数字节对齐 16-bit 样本
                bufferBytes = Math.Max(1024, (bufferBytes / 2) * 2);

                _slots = new BufferSlot[BufferCount];
                try
                {
                    int headerSize = Marshal.SizeOf(typeof(WaveHdr));
                    for (int i = 0; i < BufferCount; i++)
                    {
                        var slot = new BufferSlot(bufferBytes);
                        int prep = waveInPrepareHeader(_waveInHandle, slot.HeaderPtr, headerSize);
                        if (prep != 0) throw new InvalidOperationException("准备音频缓冲区失败（错误码 " + prep + "）。");
                        slot.Prepared = true;

                        int add = waveInAddBuffer(_waveInHandle, slot.HeaderPtr, headerSize);
                        if (add != 0) throw new InvalidOperationException("投递音频缓冲区失败（错误码 " + add + "）。");
                        _slots[i] = slot;
                    }

                    int startResult = waveInStart(_waveInHandle);
                    if (startResult != 0)
                        throw new InvalidOperationException("启动录音失败（错误码 " + startResult + "）。");

                    _isRecording = true;
                }
                catch
                {
                    CleanupNativeLocked();
                    throw;
                }
            }
        }

        /// <summary>停止录音并返回完整的 WAV 音频字节数组。</summary>
        public byte[] StopAndGetWav()
        {
            byte[] pcm;
            lock (_gate)
            {
                if (!_isRecording && _waveInHandle == IntPtr.Zero)
                    return Array.Empty<byte>();

                _stopping = true;
                _isRecording = false;

                if (_waveInHandle != IntPtr.Zero)
                {
                    try { waveInStop(_waveInHandle); } catch { }
                    try { waveInReset(_waveInHandle); } catch { }
                }

                CleanupNativeLocked();
                pcm = _pcmBuffer != null ? _pcmBuffer.ToArray() : Array.Empty<byte>();
                _pcmBuffer?.Dispose();
                _pcmBuffer = null;
            }

            return pcm.Length == 0 ? Array.Empty<byte>() : WavEncoder.EncodePcm16(pcm);
        }

        private void OnWaveInCallback(IntPtr hWaveIn, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
        {
            if (uMsg != WimData || dwParam1 == IntPtr.Zero) return;

            double level = 0.0;
            bool hasLevel = false;

            lock (_gate)
            {
                if (_waveInHandle == IntPtr.Zero || _pcmBuffer == null || _slots == null) return;

                WaveHdr hdr = (WaveHdr)Marshal.PtrToStructure(dwParam1, typeof(WaveHdr));
                if (hdr.BytesRecorded > 0 && hdr.DataPtr != IntPtr.Zero)
                {
                    byte[] chunk = new byte[hdr.BytesRecorded];
                    Marshal.Copy(hdr.DataPtr, chunk, 0, hdr.BytesRecorded);
                    _pcmBuffer.Write(chunk, 0, chunk.Length);
                    level = WavEncoder.ComputeRmsLevel(chunk, 0, chunk.Length);
                    hasLevel = true;
                }

                if (_isRecording && !_stopping)
                {
                    try
                    {
                        waveInAddBuffer(_waveInHandle, dwParam1, Marshal.SizeOf(typeof(WaveHdr)));
                    }
                    catch { }
                }
            }

            if (hasLevel)
            {
                try { LevelChanged?.Invoke(level); } catch { }
            }
        }

        private void CleanupNativeLocked()
        {
            int headerSize = Marshal.SizeOf(typeof(WaveHdr));
            if (_slots != null)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    BufferSlot slot = _slots[i];
                    if (slot == null) continue;
                    if (slot.Prepared && _waveInHandle != IntPtr.Zero)
                    {
                        try { waveInUnprepareHeader(_waveInHandle, slot.HeaderPtr, headerSize); } catch { }
                        slot.Prepared = false;
                    }
                    slot.Dispose();
                }
                _slots = null;
            }

            if (_waveInHandle != IntPtr.Zero)
            {
                try { waveInClose(_waveInHandle); } catch { }
                _waveInHandle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stopping = true;
                _isRecording = false;
                if (_waveInHandle != IntPtr.Zero)
                {
                    try { waveInReset(_waveInHandle); } catch { }
                }
                CleanupNativeLocked();
                _pcmBuffer?.Dispose();
                _pcmBuffer = null;
            }
        }

        private sealed class BufferSlot : IDisposable
        {
            private readonly byte[] _data;
            private GCHandle _dataHandle;
            public IntPtr HeaderPtr { get; private set; }
            public bool Prepared { get; set; }

            public BufferSlot(int bufferBytes)
            {
                _data = new byte[bufferBytes];
                _dataHandle = GCHandle.Alloc(_data, GCHandleType.Pinned);
                var hdr = new WaveHdr
                {
                    DataPtr = _dataHandle.AddrOfPinnedObject(),
                    BufferLength = bufferBytes,
                    BytesRecorded = 0,
                    User = IntPtr.Zero,
                    Flags = 0,
                    Loops = 0,
                    Next = IntPtr.Zero,
                    Reserved = IntPtr.Zero
                };
                HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
                Marshal.StructureToPtr(hdr, HeaderPtr, false);
            }

            public void Dispose()
            {
                if (HeaderPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(HeaderPtr);
                    HeaderPtr = IntPtr.Zero;
                }
                if (_dataHandle.IsAllocated)
                    _dataHandle.Free();
            }
        }

        private delegate void WaveInProc(IntPtr hwi, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public short FormatTag;
            public short Channels;
            public int SamplesPerSec;
            public int AvgBytesPerSec;
            public short BlockAlign;
            public short BitsPerSample;
            public short Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr DataPtr;
            public int BufferLength;
            public int BytesRecorded;
            public IntPtr User;
            public int Flags;
            public int Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr hWaveIn, int uDeviceID, ref WaveFormatEx lpFormat, WaveInProc dwCallback, IntPtr dwInstance, int dwFlags);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hWaveIn, IntPtr lpWaveInHdr, int uSize);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hWaveIn);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hWaveIn);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hWaveIn);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hWaveIn);
    }
}
