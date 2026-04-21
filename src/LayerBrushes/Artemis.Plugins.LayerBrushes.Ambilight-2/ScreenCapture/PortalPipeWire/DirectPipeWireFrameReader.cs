using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class DirectPipeWireFrameReader : IPortalPipeWireFrameReader
{
    private static readonly ILogger Logger = Log.ForContext<DirectPipeWireFrameReader>();
    private static readonly ProcessCallback OnProcess = Process;
    private static readonly StateChangedCallback OnStateChanged = StateChanged;

    private const string PipeWireLibrary = "libpipewire-0.3.so.0";
    private const int PwDirectionInput = 0;
    private const uint PwStreamFlagAutoconnect = 1 << 0;
    private const uint PwStreamFlagMapBuffers = 1 << 2;
    private const uint PwStreamEventsVersion = 2;
    private const int SpaFormatParamBytes = 256;

    private const uint SpaTypeId = 3;
    private const uint SpaTypeRectangle = 10;
    private const uint SpaTypeFraction = 11;
    private const uint SpaTypeObject = 15;
    private const uint SpaTypeObjectFormat = 0x40003;
    private const uint SpaParamEnumFormat = 3;
    private const uint SpaFormatMediaType = 1;
    private const uint SpaFormatMediaSubtype = 2;
    private const uint SpaFormatVideoFormat = 0x20001;
    private const uint SpaFormatVideoSize = 0x20003;
    private const uint SpaFormatVideoFramerate = 0x20004;
    private const uint SpaMediaTypeVideo = 2;
    private const uint SpaMediaSubtypeRaw = 1;
    private const uint SpaVideoFormatBgrx = 8;
    private const uint SpaVideoFormatBgra = 12;

    private readonly PortalPipeWireOutput _output;
    private readonly object _frameLock = new();
    private readonly object _lifecycleLock = new();
    private readonly int _portalRemoteFd;
    private GCHandle _selfHandle;
    private Thread? _loopThread;
    private IntPtr _mainLoop;
    private IntPtr _loop;
    private IntPtr _context;
    private IntPtr _core;
    private IntPtr _stream;
    private IntPtr _listenerHook;
    private IntPtr _events;
    private IntPtr _formatParam;
    private IntPtr _formatParamArray;
    private byte[] _latestFrame = [];
    private volatile bool _hasFrame;
    private int _frameWidth;
    private int _frameHeight;
    private int _sourceDownscaleLevel;
    private int _targetFrameWidth;
    private int _targetFrameHeight;
    private int _targetDownscaleLevel;
    private int _fpsLimit = 30;
    private long _nextFrameTicks;
    private int _receivedFrameLogCount;
    private int _unsupportedFrameLogCount;
    private int _missingFrameLogCount;
    private int _ownedRemoteFd = -1;

    public DirectPipeWireFrameReader(PortalPipeWireOutput output, int pipeWireRemoteFd)
    {
        _output = output;
        _portalRemoteFd = pipeWireRemoteFd;
        Configure(0, _fpsLimit);
        Start();
    }

    public static bool IsRuntimeAvailable(out string? reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "not running on Linux";
            return false;
        }

        if (!NativeLibrary.TryLoad(PipeWireLibrary, out IntPtr handle))
        {
            reason = $"{PipeWireLibrary} is not available";
            return false;
        }

        NativeLibrary.Free(handle);
        reason = null;
        return true;
    }

    public void Configure(int sourceDownscaleLevel, int fpsLimit)
    {
        sourceDownscaleLevel = Math.Clamp(sourceDownscaleLevel, 0, 8);
        fpsLimit = fpsLimit <= 0 ? 30 : Math.Clamp(fpsLimit, 1, PortalPipeWireFrameReader.MaxFpsLimit);

        int targetWidth = Math.Max(1, _output.Display.Width >> sourceDownscaleLevel);
        int targetHeight = Math.Max(1, _output.Display.Height >> sourceDownscaleLevel);
        bool changed =
            targetWidth != Volatile.Read(ref _targetFrameWidth) ||
            targetHeight != Volatile.Read(ref _targetFrameHeight) ||
            sourceDownscaleLevel != Volatile.Read(ref _targetDownscaleLevel) ||
            fpsLimit != Volatile.Read(ref _fpsLimit);

        Interlocked.Exchange(ref _targetFrameWidth, targetWidth);
        Interlocked.Exchange(ref _targetFrameHeight, targetHeight);
        Interlocked.Exchange(ref _targetDownscaleLevel, sourceDownscaleLevel);
        Interlocked.Exchange(ref _fpsLimit, fpsLimit);
        Interlocked.Exchange(ref _nextFrameTicks, 0);

        AmbilightLinuxDiagnostics.Write(Logger,
            $"configured direct PipeWire reader display={_output.StableId} requested={_targetFrameWidth}x{_targetFrameHeight} downscale={sourceDownscaleLevel} fps={fpsLimit}");

        if (changed && _stream != IntPtr.Zero)
            Restart();
    }

    public bool TryCopyLatestFrame(ref byte[] destination, out int sourceWidth, out int sourceHeight, out int sourceDownscaleLevel)
    {
        if (!_hasFrame)
        {
            if (_missingFrameLogCount < 5)
            {
                _missingFrameLogCount++;
                AmbilightLinuxDiagnostics.Write(Logger, $"direct PipeWire does not have a frame yet for {_output.StableId}");
            }

            sourceWidth = 0;
            sourceHeight = 0;
            sourceDownscaleLevel = 0;
            return false;
        }

        lock (_frameLock)
        {
            if (destination.Length != _latestFrame.Length)
                destination = new byte[_latestFrame.Length];

            _latestFrame.AsSpan().CopyTo(destination);
            sourceWidth = _frameWidth;
            sourceHeight = _frameHeight;
            sourceDownscaleLevel = _sourceDownscaleLevel;
        }

        return true;
    }

    public void Restart()
    {
        lock (_lifecycleLock)
        {
            StopNoLock();
            _hasFrame = false;
            StartNoLock();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
            StopNoLock();
    }

    private void Start()
    {
        lock (_lifecycleLock)
            StartNoLock();
    }

    private void StartNoLock()
    {
        if (_stream != IntPtr.Zero)
            return;

        try
        {
        int duplicatedFd = dup(_portalRemoteFd);
        if (duplicatedFd < 0)
            throw new InvalidOperationException($"dup({_portalRemoteFd}) failed with errno {Marshal.GetLastPInvokeError()}");

        _ownedRemoteFd = duplicatedFd;
        _selfHandle = GCHandle.Alloc(this);

        pw_init(IntPtr.Zero, IntPtr.Zero);
        _mainLoop = pw_main_loop_new(IntPtr.Zero);
        if (_mainLoop == IntPtr.Zero)
            throw new InvalidOperationException("pw_main_loop_new failed.");

        _loop = pw_main_loop_get_loop(_mainLoop);
        if (_loop == IntPtr.Zero)
            throw new InvalidOperationException("pw_main_loop_get_loop failed.");

        _context = pw_context_new(_loop, IntPtr.Zero, UIntPtr.Zero);
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("pw_context_new failed.");

        _core = pw_context_connect_fd(_context, duplicatedFd, IntPtr.Zero, UIntPtr.Zero);
        if (_core == IntPtr.Zero)
            throw new InvalidOperationException("pw_context_connect_fd failed.");

        _ownedRemoteFd = -1; // PipeWire owns the duplicated fd after a successful connect.
        _stream = pw_stream_new(_core, "Artemis Ambilight Direct PipeWire", IntPtr.Zero);
        if (_stream == IntPtr.Zero)
            throw new InvalidOperationException("pw_stream_new failed.");

        _listenerHook = Marshal.AllocHGlobal(128);
        ZeroMemory(_listenerHook, 128);
        _events = Marshal.AllocHGlobal(Marshal.SizeOf<PwStreamEvents>());
        Marshal.StructureToPtr(new PwStreamEvents
        {
            Version = PwStreamEventsVersion,
            StateChanged = Marshal.GetFunctionPointerForDelegate(OnStateChanged),
            Process = Marshal.GetFunctionPointerForDelegate(OnProcess)
        }, _events, false);

        pw_stream_add_listener(_stream, _listenerHook, _events, GCHandle.ToIntPtr(_selfHandle));

        int targetFrameWidth = Math.Max(1, Volatile.Read(ref _targetFrameWidth));
        int targetFrameHeight = Math.Max(1, Volatile.Read(ref _targetFrameHeight));
        int fpsLimit = Volatile.Read(ref _fpsLimit);
        BuildFormatParams(targetFrameWidth, targetFrameHeight, fpsLimit);

        int result = pw_stream_connect(
            _stream,
            PwDirectionInput,
            _output.NodeId,
            PwStreamFlagAutoconnect | PwStreamFlagMapBuffers,
            _formatParamArray,
            1);

        if (result < 0)
            throw new InvalidOperationException($"pw_stream_connect failed with result {result}.");

        _loopThread = new Thread(() =>
        {
            try
            {
                int loopResult = pw_main_loop_run(_mainLoop);
                if (loopResult < 0)
                    AmbilightLinuxDiagnostics.Write(Logger, $"direct PipeWire main loop exited for {_output.StableId} with result {loopResult}");
            }
            catch (Exception ex)
            {
                AmbilightLinuxDiagnostics.Write(Logger, $"direct PipeWire main loop failed for {_output.StableId}: {ex.GetBaseException().Message}");
                Logger.Warning(ex, "Direct PipeWire main loop failed for {Display}", _output.StableId);
            }
        })
        {
            IsBackground = true,
            Name = $"Ambilight PipeWire {_output.Display.DeviceName}"
        };
        _loopThread.Start();

        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire reader connected display={_output.StableId} node={_output.NodeId} requested={targetFrameWidth}x{targetFrameHeight} fps={fpsLimit}");
        }
        catch
        {
            StopNoLock();
            throw;
        }
    }

    private void StopNoLock()
    {
        try
        {
            if (_mainLoop != IntPtr.Zero)
                pw_main_loop_quit(_mainLoop);
        }
        catch { }

        try { _loopThread?.Join(TimeSpan.FromSeconds(2)); }
        catch { }

        _loopThread = null;

        try
        {
            if (_stream != IntPtr.Zero)
                pw_stream_destroy(_stream);
        }
        catch { }
        _stream = IntPtr.Zero;

        try
        {
            if (_core != IntPtr.Zero)
                pw_core_disconnect(_core);
        }
        catch { }
        _core = IntPtr.Zero;

        try
        {
            if (_context != IntPtr.Zero)
                pw_context_destroy(_context);
        }
        catch { }
        _context = IntPtr.Zero;
        _loop = IntPtr.Zero;

        try
        {
            if (_mainLoop != IntPtr.Zero)
                pw_main_loop_destroy(_mainLoop);
        }
        catch { }
        _mainLoop = IntPtr.Zero;

        if (_events != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_events);
            _events = IntPtr.Zero;
        }

        if (_listenerHook != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_listenerHook);
            _listenerHook = IntPtr.Zero;
        }

        if (_formatParamArray != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formatParamArray);
            _formatParamArray = IntPtr.Zero;
        }

        if (_formatParam != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formatParam);
            _formatParam = IntPtr.Zero;
        }

        if (_selfHandle.IsAllocated)
            _selfHandle.Free();

        if (_ownedRemoteFd >= 0)
        {
            try { close(_ownedRemoteFd); }
            catch { }
            _ownedRemoteFd = -1;
        }
    }

    private void ProcessFrame()
    {
        IntPtr pwBufferPtr = pw_stream_dequeue_buffer(_stream);
        if (pwBufferPtr == IntPtr.Zero)
            return;

        try
        {
            if (ShouldDropFrameForFps())
                return;

            var pwBuffer = Marshal.PtrToStructure<PwBuffer>(pwBufferPtr);
            if (pwBuffer.Buffer == IntPtr.Zero)
                return;

            var spaBuffer = Marshal.PtrToStructure<SpaBuffer>(pwBuffer.Buffer);
            if (spaBuffer.NDatas == 0 || spaBuffer.Datas == IntPtr.Zero)
                return;

            var spaData = Marshal.PtrToStructure<SpaData>(spaBuffer.Datas);
            if (spaData.Data == IntPtr.Zero || spaData.Chunk == IntPtr.Zero)
                return;

            var chunk = Marshal.PtrToStructure<SpaChunk>(spaData.Chunk);
            if (!TryGetFrameLayout(chunk, out int width, out int height, out int downscaleLevel, out int stride, out int frameBytes))
                return;

            IntPtr source = IntPtr.Add(spaData.Data, checked((int)chunk.Offset));
            int rowBytes = checked(width * 4);
            lock (_frameLock)
            {
                if (_latestFrame.Length != frameBytes)
                    _latestFrame = new byte[frameBytes];

                if (stride == rowBytes)
                {
                    Marshal.Copy(source, _latestFrame, 0, frameBytes);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(IntPtr.Add(source, y * stride), _latestFrame, y * rowBytes, rowBytes);
                }

                _frameWidth = width;
                _frameHeight = height;
                _sourceDownscaleLevel = downscaleLevel;
                _hasFrame = true;
            }

            if (_receivedFrameLogCount < 3)
            {
                _receivedFrameLogCount++;
                AmbilightLinuxDiagnostics.Write(Logger,
                    $"received direct PipeWire frame {_receivedFrameLogCount} for {_output.StableId} ({width}x{height}, downscale={downscaleLevel}, stride={stride})");
            }
        }
        finally
        {
            pw_stream_queue_buffer(_stream, pwBufferPtr);
        }
    }

    private bool TryGetFrameLayout(SpaChunk chunk, out int width, out int height, out int downscaleLevel, out int stride, out int frameBytes)
    {
        int nativeWidth = _output.Display.Width;
        int nativeHeight = _output.Display.Height;
        int targetWidth = Math.Max(1, Volatile.Read(ref _targetFrameWidth));
        int targetHeight = Math.Max(1, Volatile.Read(ref _targetFrameHeight));
        int targetDownscale = Math.Clamp(Volatile.Read(ref _targetDownscaleLevel), 0, 8);
        int providedBytes = checked((int)Math.Min(chunk.Size, int.MaxValue));
        int chunkStride = Math.Abs(chunk.Stride);

        if (TryMatchLayout(providedBytes, chunkStride, nativeWidth, nativeHeight, 0, out width, out height, out downscaleLevel, out stride, out frameBytes))
            return true;

        if (TryMatchLayout(providedBytes, chunkStride, targetWidth, targetHeight, targetDownscale, out width, out height, out downscaleLevel, out stride, out frameBytes))
            return true;

        if (_unsupportedFrameLogCount < 5)
        {
            _unsupportedFrameLogCount++;
            AmbilightLinuxDiagnostics.Write(Logger,
                $"direct PipeWire frame layout unsupported for {_output.StableId}: bytes={providedBytes} stride={chunk.Stride} target={targetWidth}x{targetHeight} native={nativeWidth}x{nativeHeight}");
        }

        width = 0;
        height = 0;
        downscaleLevel = 0;
        stride = 0;
        frameBytes = 0;
        return false;
    }

    private unsafe void BuildFormatParams(int width, int height, int fpsLimit)
    {
        if (_formatParam != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formatParam);
            _formatParam = IntPtr.Zero;
        }

        if (_formatParamArray != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formatParamArray);
            _formatParamArray = IntPtr.Zero;
        }

        _formatParam = Marshal.AllocHGlobal(SpaFormatParamBytes);
        ZeroMemory(_formatParam, SpaFormatParamBytes);

        var writer = new SpaPodWriter((byte*)_formatParam.ToPointer(), SpaFormatParamBytes);
        writer.BeginObject(SpaTypeObjectFormat, SpaParamEnumFormat);
        writer.AddId(SpaFormatMediaType, SpaMediaTypeVideo);
        writer.AddId(SpaFormatMediaSubtype, SpaMediaSubtypeRaw);
        writer.AddId(SpaFormatVideoFormat, SpaVideoFormatBgrx);
        writer.AddRectangle(SpaFormatVideoSize, (uint)width, (uint)height);
        writer.AddFraction(SpaFormatVideoFramerate, (uint)Math.Max(1, fpsLimit), 1);
        writer.EndObject();

        _formatParamArray = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_formatParamArray, _formatParam);

        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire requested SPA format for {_output.StableId}: BGRx {width}x{height} {Math.Max(1, fpsLimit)}/1 fps");
    }

    private static bool TryMatchLayout(int providedBytes, int chunkStride, int expectedWidth, int expectedHeight, int expectedDownscale, out int width, out int height, out int downscaleLevel, out int stride, out int frameBytes)
    {
        int rowBytes = checked(expectedWidth * 4);
        int contiguousBytes = checked(rowBytes * expectedHeight);
        int candidateStride = chunkStride == 0 ? rowBytes : chunkStride;

        if (candidateStride < rowBytes || providedBytes < candidateStride * expectedHeight)
        {
            width = 0;
            height = 0;
            downscaleLevel = 0;
            stride = 0;
            frameBytes = 0;
            return false;
        }

        width = expectedWidth;
        height = expectedHeight;
        downscaleLevel = expectedDownscale;
        stride = candidateStride;
        frameBytes = contiguousBytes;
        return true;
    }

    private bool ShouldDropFrameForFps()
    {
        int fpsLimit = Volatile.Read(ref _fpsLimit);
        if (fpsLimit <= 0)
            return false;

        long now = Stopwatch.GetTimestamp();
        long next = Interlocked.Read(ref _nextFrameTicks);
        if (now < next)
            return true;

        long interval = Math.Max(1, Stopwatch.Frequency / fpsLimit);
        Interlocked.Exchange(ref _nextFrameTicks, now + interval);
        return false;
    }

    private static void Process(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is DirectPipeWireFrameReader reader)
            reader.ProcessFrame();
    }

    private static void StateChanged(IntPtr data, int oldState, int state, IntPtr error)
    {
        if (data == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is not DirectPipeWireFrameReader reader)
            return;

        string? errorText = error == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(error);
        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire state changed for {reader._output.StableId}: {oldState}->{state}{(string.IsNullOrWhiteSpace(errorText) ? string.Empty : $" error={errorText}")}");
    }

    private static unsafe void ZeroMemory(IntPtr pointer, int bytes)
    {
        new Span<byte>(pointer.ToPointer(), bytes).Clear();
    }

    private unsafe ref struct SpaPodWriter
    {
        private readonly byte* _buffer;
        private readonly int _capacity;
        private int _offset;
        private int _objectStart;

        public SpaPodWriter(byte* buffer, int capacity)
        {
            _buffer = buffer;
            _capacity = capacity;
            _offset = 0;
            _objectStart = -1;
        }

        public void BeginObject(uint objectType, uint objectId)
        {
            _objectStart = _offset;
            WriteUInt(0); // patched by EndObject
            WriteUInt(SpaTypeObject);
            WriteUInt(objectType);
            WriteUInt(objectId);
        }

        public void EndObject()
        {
            if (_objectStart < 0)
                throw new InvalidOperationException("No SPA POD object is open.");

            uint objectBodySize = checked((uint)(_offset - _objectStart - 8));
            WriteUIntAt(_objectStart, objectBodySize);
            _objectStart = -1;
        }

        public void AddId(uint key, uint value)
        {
            BeginProperty(key);
            WriteUInt(4);
            WriteUInt(SpaTypeId);
            WriteUInt(value);
            WriteUInt(0);
        }

        public void AddRectangle(uint key, uint width, uint height)
        {
            BeginProperty(key);
            WriteUInt(8);
            WriteUInt(SpaTypeRectangle);
            WriteUInt(width);
            WriteUInt(height);
        }

        public void AddFraction(uint key, uint numerator, uint denominator)
        {
            BeginProperty(key);
            WriteUInt(8);
            WriteUInt(SpaTypeFraction);
            WriteUInt(numerator);
            WriteUInt(denominator);
        }

        private void BeginProperty(uint key)
        {
            Align();
            WriteUInt(key);
            WriteUInt(0);
        }

        private void Align()
        {
            while ((_offset & 7) != 0)
                WriteByte(0);
        }

        private void WriteByte(byte value)
        {
            if (_offset >= _capacity)
                throw new InvalidOperationException("SPA POD parameter buffer is too small.");

            _buffer[_offset++] = value;
        }

        private void WriteUInt(uint value)
        {
            if (_offset + 4 > _capacity)
                throw new InvalidOperationException("SPA POD parameter buffer is too small.");

            *(uint*)(_buffer + _offset) = value;
            _offset += 4;
        }

        private void WriteUIntAt(int offset, uint value)
        {
            if (offset < 0 || offset + 4 > _capacity)
                throw new ArgumentOutOfRangeException(nameof(offset));

            *(uint*)(_buffer + offset) = value;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PwStreamEvents
    {
        public uint Version;
        public IntPtr Destroy;
        public IntPtr StateChanged;
        public IntPtr ControlInfo;
        public IntPtr IoChanged;
        public IntPtr ParamChanged;
        public IntPtr AddBuffer;
        public IntPtr RemoveBuffer;
        public IntPtr Process;
        public IntPtr Drained;
        public IntPtr Command;
        public IntPtr TriggerDone;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PwBuffer
    {
        public IntPtr Buffer;
        public IntPtr UserData;
        public ulong Size;
        public ulong Requested;
        public ulong Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpaBuffer
    {
        public uint NMetas;
        public uint NDatas;
        public IntPtr Metas;
        public IntPtr Datas;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpaData
    {
        public uint Type;
        public uint Flags;
        public long Fd;
        public uint MapOffset;
        public uint MaxSize;
        public IntPtr Data;
        public IntPtr Chunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpaChunk
    {
        public uint Offset;
        public uint Size;
        public int Stride;
        public int Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProcessCallback(IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateChangedCallback(IntPtr data, int oldState, int state, IntPtr error);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_init(IntPtr argc, IntPtr argv);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_main_loop_new(IntPtr properties);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_main_loop_get_loop(IntPtr loop);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_main_loop_run(IntPtr loop);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_main_loop_quit(IntPtr loop);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_main_loop_destroy(IntPtr loop);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_context_new(IntPtr loop, IntPtr properties, UIntPtr userDataSize);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_context_destroy(IntPtr context);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr properties, UIntPtr userDataSize);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_core_disconnect(IntPtr core);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr pw_stream_new(IntPtr core, string name, IntPtr properties);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_stream_destroy(IntPtr stream);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_stream_add_listener(IntPtr stream, IntPtr listener, IntPtr events, IntPtr data);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, uint flags, IntPtr parameters, uint parameterCount);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
