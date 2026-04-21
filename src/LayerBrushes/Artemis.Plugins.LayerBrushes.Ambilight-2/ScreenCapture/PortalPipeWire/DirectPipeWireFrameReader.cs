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
    private static readonly ParamChangedCallback OnParamChanged = ParamChanged;
    private static readonly BufferChangedCallback OnAddBuffer = AddBuffer;
    private static readonly BufferChangedCallback OnRemoveBuffer = RemoveBuffer;

    private const string PipeWireLibrary = "libpipewire-0.3.so.0";
    private const int PwDirectionInput = 0;
    private const uint PwStreamFlagAutoconnect = 1 << 0;
    private const uint PwStreamFlagMapBuffers = 1 << 2;
    private const uint PwStreamEventsVersion = 2;
    private const int SpaFormatParamBytes = 256;
    private const int SpaBufferParamBytes = 256;

    private const uint SpaTypeId = 3;
    private const uint SpaTypeInt = 4;
    private const uint SpaTypeRectangle = 10;
    private const uint SpaTypeFraction = 11;
    private const uint SpaTypeObject = 15;
    private const uint SpaTypeChoice = 19;
    private const uint SpaTypeObjectFormat = 0x40003;
    private const uint SpaTypeObjectParamBuffers = 0x40004;
    private const uint SpaChoiceEnum = 3;
    private const uint SpaParamEnumFormat = 3;
    private const uint SpaParamFormat = 4;
    private const uint SpaParamBuffers = 5;
    private const uint SpaParamBuffersBuffers = 1;
    private const uint SpaParamBuffersBlocks = 2;
    private const uint SpaParamBuffersSize = 3;
    private const uint SpaParamBuffersStride = 4;
    private const uint SpaParamBuffersAlign = 5;
    private const uint SpaParamBuffersDataType = 6;
    private const uint SpaFormatMediaType = 1;
    private const uint SpaFormatMediaSubtype = 2;
    private const uint SpaFormatVideoFormat = 0x20001;
    private const uint SpaFormatVideoSize = 0x20003;
    private const uint SpaFormatVideoFramerate = 0x20004;
    private const uint SpaMediaTypeVideo = 2;
    private const uint SpaMediaSubtypeRaw = 1;
    private const uint SpaVideoFormatRgbx = 7;
    private const uint SpaVideoFormatBgrx = 8;
    private const uint SpaVideoFormatXrgb = 9;
    private const uint SpaVideoFormatXbgr = 10;
    private const uint SpaVideoFormatRgba = 11;
    private const uint SpaVideoFormatBgra = 12;
    private const uint SpaVideoFormatArgb = 13;
    private const uint SpaVideoFormatAbgr = 14;
    private const uint SpaDataMemPtr = 1;
    private const uint SpaDataMemFd = 2;

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
    private IntPtr _bufferParam;
    private IntPtr _bufferParamArray;
    private byte[] _latestFrame = [];
    private volatile bool _hasFrame;
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private int _sourceDownscaleLevel;
    private PortalPipeWirePixelFormat _framePixelFormat = PortalPipeWirePixelFormat.Bgrx;
    private int _targetFrameWidth;
    private int _targetFrameHeight;
    private int _targetDownscaleLevel;
    private int _fpsLimit = 30;
    private long _nextFrameTicks;
    private int _receivedFrameLogCount;
    private int _unsupportedFrameLogCount;
    private int _missingFrameLogCount;
    private int _droppedFrameLogCount;
    private int _addBufferLogCount;
    private int _removeBufferLogCount;
    private int _negotiatedFrameWidth;
    private int _negotiatedFrameHeight;
    private uint _negotiatedVideoFormat;
    private uint _activeVideoFormat = SpaVideoFormatBgrx;
    private int _ownedRemoteFd = -1;

    public DirectPipeWireFrameReader(PortalPipeWireOutput output, int pipeWireRemoteFd)
    {
        _output = output;
        _portalRemoteFd = pipeWireRemoteFd;
        Configure(0, _fpsLimit);
        Start();
    }

    public string CaptureBackendName => "Direct PipeWire";

    public string CaptureBackendDetails =>
        $"Direct PipeWire active; node={_output.NodeId}, requested={Math.Max(1, Volatile.Read(ref _targetFrameWidth))}x{Math.Max(1, Volatile.Read(ref _targetFrameHeight))}, fps={FormatFpsLimit(Volatile.Read(ref _fpsLimit))}";

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
        fpsLimit = fpsLimit <= 0 ? 0 : Math.Clamp(fpsLimit, 1, PortalPipeWireFrameReader.MaxFpsLimit);

        int targetWidth = Math.Max(1, _output.Display.Width);
        int targetHeight = Math.Max(1, _output.Display.Height);

        Interlocked.Exchange(ref _targetFrameWidth, targetWidth);
        Interlocked.Exchange(ref _targetFrameHeight, targetHeight);
        Interlocked.Exchange(ref _targetDownscaleLevel, 0);
        Interlocked.Exchange(ref _fpsLimit, fpsLimit);
        Interlocked.Exchange(ref _nextFrameTicks, 0);

        AmbilightLinuxDiagnostics.Write(Logger,
            $"configured direct PipeWire reader display={_output.StableId} requested={_targetFrameWidth}x{_targetFrameHeight} downscale=0 fps={fpsLimit}");
    }

    public bool TryUseLatestFrame(PortalPipeWireFrameConsumer consumer)
    {
        if (!_hasFrame)
        {
            if (_missingFrameLogCount < 5)
            {
                _missingFrameLogCount++;
                AmbilightLinuxDiagnostics.Write(Logger, $"direct PipeWire does not have a frame yet for {_output.StableId}");
            }

            return false;
        }

        lock (_frameLock)
            consumer(_latestFrame, _frameWidth, _frameHeight, _sourceDownscaleLevel, _frameStride, _framePixelFormat);

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
        IntPtr streamProperties = pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen");
        if (streamProperties == IntPtr.Zero)
            throw new InvalidOperationException("pw_properties_new_string failed.");

        _stream = pw_stream_new(_core, "Artemis Ambilight Direct PipeWire", streamProperties);
        if (_stream == IntPtr.Zero)
            throw new InvalidOperationException("pw_stream_new failed.");

        _listenerHook = Marshal.AllocHGlobal(128);
        ZeroMemory(_listenerHook, 128);
        _events = Marshal.AllocHGlobal(Marshal.SizeOf<PwStreamEvents>());
        Marshal.StructureToPtr(new PwStreamEvents
        {
            Version = PwStreamEventsVersion,
            StateChanged = Marshal.GetFunctionPointerForDelegate(OnStateChanged),
            ParamChanged = Marshal.GetFunctionPointerForDelegate(OnParamChanged),
            AddBuffer = Marshal.GetFunctionPointerForDelegate(OnAddBuffer),
            RemoveBuffer = Marshal.GetFunctionPointerForDelegate(OnRemoveBuffer),
            Process = Marshal.GetFunctionPointerForDelegate(OnProcess)
        }, _events, false);

        int targetFrameWidth = Math.Max(1, Volatile.Read(ref _targetFrameWidth));
        int targetFrameHeight = Math.Max(1, Volatile.Read(ref _targetFrameHeight));
        int fpsLimit = Volatile.Read(ref _fpsLimit);
        BuildFormatParams(targetFrameWidth, targetFrameHeight, fpsLimit);

        pw_stream_add_listener(_stream, _listenerHook, _events, GCHandle.ToIntPtr(_selfHandle));

        int result = pw_stream_connect(
            _stream,
            PwDirectionInput,
            _output.NodeId,
            PwStreamFlagAutoconnect | PwStreamFlagMapBuffers,
            _formatParamArray,
            1);

        if (result < 0)
            throw new InvalidOperationException($"pw_stream_connect failed with result {result}.");

        int activateResult = pw_stream_set_active(_stream, true);
        if (activateResult < 0)
            throw new InvalidOperationException($"pw_stream_set_active failed with result {activateResult}.");

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

        if (_bufferParamArray != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bufferParamArray);
            _bufferParamArray = IntPtr.Zero;
        }

        if (_bufferParam != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bufferParam);
            _bufferParam = IntPtr.Zero;
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
            {
                LogDroppedFrame("PipeWire buffer pointer was null");
                return;
            }

            var spaBuffer = Marshal.PtrToStructure<SpaBuffer>(pwBuffer.Buffer);
            if (spaBuffer.NDatas == 0 || spaBuffer.Datas == IntPtr.Zero)
            {
                LogDroppedFrame($"SPA buffer had no data entries (n_datas={spaBuffer.NDatas}, datas=0x{spaBuffer.Datas.ToInt64():x})");
                return;
            }

            var spaData = Marshal.PtrToStructure<SpaData>(spaBuffer.Datas);
            if (spaData.Data == IntPtr.Zero || spaData.Chunk == IntPtr.Zero)
            {
                LogDroppedFrame(
                    $"SPA data was not CPU-mapped (type={spaData.Type}, flags=0x{spaData.Flags:x}, fd={spaData.Fd}, mapOffset={spaData.MapOffset}, maxSize={spaData.MaxSize}, data=0x{spaData.Data.ToInt64():x}, chunk=0x{spaData.Chunk.ToInt64():x})");
                return;
            }

            var chunk = Marshal.PtrToStructure<SpaChunk>(spaData.Chunk);
            if (!TryGetFrameLayout(chunk, out int width, out int height, out int downscaleLevel, out int stride, out int frameBytes))
                return;

            IntPtr source = IntPtr.Add(spaData.Data, checked((int)chunk.Offset));
            int rowBytes = checked(width * 4);
            uint videoFormat = Volatile.Read(ref _activeVideoFormat);
            PortalPipeWirePixelFormat pixelFormat = ToPortalPixelFormat(videoFormat);
            lock (_frameLock)
            {
                if (_latestFrame.Length != frameBytes)
                    _latestFrame = new byte[frameBytes];

                CopyFrameBytes(source, stride, _latestFrame, rowBytes, width, height);

                _frameWidth = width;
                _frameHeight = height;
                _frameStride = rowBytes;
                _sourceDownscaleLevel = downscaleLevel;
                _framePixelFormat = pixelFormat;
                _hasFrame = true;
            }

            if (_receivedFrameLogCount < 3)
            {
                _receivedFrameLogCount++;
                AmbilightLinuxDiagnostics.Write(Logger,
                    $"received direct PipeWire frame {_receivedFrameLogCount} for {_output.StableId} ({width}x{height}, downscale={downscaleLevel}, stride={stride}, format={VideoFormatName(videoFormat)})");
            }
        }
        finally
        {
            pw_stream_queue_buffer(_stream, pwBufferPtr);
        }
    }

    private void LogDroppedFrame(string reason)
    {
        if (_droppedFrameLogCount >= 10)
            return;

        _droppedFrameLogCount++;
        AmbilightLinuxDiagnostics.Write(Logger, $"dropped direct PipeWire frame {_droppedFrameLogCount} for {_output.StableId}: {reason}");
    }

    private static void CopyFrameBytes(IntPtr source, int sourceStride, byte[] destination, int destinationStride, int width, int height)
    {
        int rowBytes = checked(width * 4);
        if (sourceStride == rowBytes && destinationStride == rowBytes)
        {
            Marshal.Copy(source, destination, 0, checked(rowBytes * height));
            return;
        }

        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(source, y * sourceStride), destination, y * destinationStride, rowBytes);
    }

    private bool TryGetFrameLayout(SpaChunk chunk, out int width, out int height, out int downscaleLevel, out int stride, out int frameBytes)
    {
        int nativeWidth = _output.Display.Width;
        int nativeHeight = _output.Display.Height;
        int targetWidth = Math.Max(1, Volatile.Read(ref _targetFrameWidth));
        int targetHeight = Math.Max(1, Volatile.Read(ref _targetFrameHeight));
        int targetDownscale = Math.Clamp(Volatile.Read(ref _targetDownscaleLevel), 0, 8);
        int negotiatedWidth = Volatile.Read(ref _negotiatedFrameWidth);
        int negotiatedHeight = Volatile.Read(ref _negotiatedFrameHeight);
        uint negotiatedFormat = Volatile.Read(ref _negotiatedVideoFormat);
        int providedBytes = checked((int)Math.Min(chunk.Size, int.MaxValue));
        int chunkStride = Math.Abs(chunk.Stride);

        if (negotiatedFormat != 0 && !IsSupportedDirectVideoFormat(negotiatedFormat))
        {
            LogDroppedFrame($"negotiated format {VideoFormatName(negotiatedFormat)} is not supported by the direct converter");
            width = 0;
            height = 0;
            downscaleLevel = 0;
            stride = 0;
            frameBytes = 0;
            return false;
        }

        if (negotiatedWidth > 0 &&
            negotiatedHeight > 0 &&
            TryMatchLayout(providedBytes, chunkStride, negotiatedWidth, negotiatedHeight, 0, out width, out height, out downscaleLevel, out stride, out frameBytes))
            return true;

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
        writer.AddChoiceEnumId(
            SpaFormatVideoFormat,
            SpaVideoFormatBgrx,
            SpaVideoFormatBgra,
            SpaVideoFormatRgbx,
            SpaVideoFormatRgba,
            SpaVideoFormatXrgb,
            SpaVideoFormatArgb,
            SpaVideoFormatXbgr,
            SpaVideoFormatAbgr);
        writer.AddRectangle(SpaFormatVideoSize, (uint)width, (uint)height);
        writer.AddFraction(SpaFormatVideoFramerate, 0, 1);
        writer.EndObject();

        _formatParamArray = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_formatParamArray, _formatParam);

        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire requested SPA format for {_output.StableId}: 32-bit RGB/BGR raw {width}x{height} variable fps (plugin throttle={FormatFpsLimit(fpsLimit)})");
    }

    private static string FormatFpsLimit(int fpsLimit) => fpsLimit <= 0 ? "unlimited" : fpsLimit.ToString();

    private unsafe void UpdateBufferParams(int width, int height, uint videoFormat)
    {
        if (!IsSupportedDirectVideoFormat(videoFormat))
        {
            AmbilightLinuxDiagnostics.Write(Logger,
                $"direct PipeWire refused unsupported negotiated format for {_output.StableId}: {VideoFormatName(videoFormat)} ({videoFormat})");
            return;
        }

        if (_bufferParam != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bufferParam);
            _bufferParam = IntPtr.Zero;
        }

        if (_bufferParamArray != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_bufferParamArray);
            _bufferParamArray = IntPtr.Zero;
        }

        int stride = checked(width * 4);
        int frameBytes = checked(stride * height);
        uint dataTypes = (1u << (int)SpaDataMemPtr) | (1u << (int)SpaDataMemFd);

        _bufferParam = Marshal.AllocHGlobal(SpaBufferParamBytes);
        ZeroMemory(_bufferParam, SpaBufferParamBytes);

        var writer = new SpaPodWriter((byte*)_bufferParam.ToPointer(), SpaBufferParamBytes);
        writer.BeginObject(SpaTypeObjectParamBuffers, SpaParamBuffers);
        writer.AddInt(SpaParamBuffersBuffers, 3);
        writer.AddInt(SpaParamBuffersBlocks, 1);
        writer.AddInt(SpaParamBuffersSize, frameBytes);
        writer.AddInt(SpaParamBuffersStride, stride);
        writer.AddInt(SpaParamBuffersAlign, 16);
        writer.AddInt(SpaParamBuffersDataType, checked((int)dataTypes));
        writer.EndObject();

        _bufferParamArray = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_bufferParamArray, _bufferParam);

        int result = pw_stream_update_params(_stream, _bufferParamArray, 1);
        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire updated buffer params for {_output.StableId}: format={VideoFormatName(videoFormat)} size={width}x{height} stride={stride} bytes={frameBytes} dataTypes=MemPtr|MemFd result={result}");
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
            $"direct PipeWire state changed for {reader._output.StableId}: {StreamStateName(oldState)}->{StreamStateName(state)}{(string.IsNullOrWhiteSpace(errorText) ? string.Empty : $" error={errorText}")}");
    }

    private static void ParamChanged(IntPtr data, uint id, IntPtr param)
    {
        if (data == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is not DirectPipeWireFrameReader reader)
            return;

        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire parameter changed for {reader._output.StableId}: {ParamName(id)} ({id}), param=0x{param.ToInt64():x}");

        if (id != SpaParamFormat || param == IntPtr.Zero)
            return;

        if (!reader.TryReadNegotiatedFormat(param, out uint videoFormat, out int width, out int height, out int fpsNumerator, out int fpsDenominator))
        {
            AmbilightLinuxDiagnostics.Write(Logger,
                $"direct PipeWire could not parse negotiated format for {reader._output.StableId}; leaving stream untouched");
            return;
        }

        Interlocked.Exchange(ref reader._negotiatedFrameWidth, width);
        Interlocked.Exchange(ref reader._negotiatedFrameHeight, height);
        Volatile.Write(ref reader._negotiatedVideoFormat, videoFormat);
        Volatile.Write(ref reader._activeVideoFormat, videoFormat);

        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire negotiated format for {reader._output.StableId}: {VideoFormatName(videoFormat)} {width}x{height} {fpsNumerator}/{fpsDenominator} fps");
        reader.UpdateBufferParams(width, height, videoFormat);
    }

    private static void AddBuffer(IntPtr data, IntPtr buffer)
    {
        if (data == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is not DirectPipeWireFrameReader reader || reader._addBufferLogCount >= 5)
            return;

        reader._addBufferLogCount++;
        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire add_buffer {reader._addBufferLogCount} for {reader._output.StableId}: buffer=0x{buffer.ToInt64():x}");
    }

    private static void RemoveBuffer(IntPtr data, IntPtr buffer)
    {
        if (data == IntPtr.Zero)
            return;

        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is not DirectPipeWireFrameReader reader || reader._removeBufferLogCount >= 5)
            return;

        reader._removeBufferLogCount++;
        AmbilightLinuxDiagnostics.Write(Logger,
            $"direct PipeWire remove_buffer {reader._removeBufferLogCount} for {reader._output.StableId}: buffer=0x{buffer.ToInt64():x}");
    }

    private unsafe bool TryReadNegotiatedFormat(IntPtr param, out uint videoFormat, out int width, out int height, out int fpsNumerator, out int fpsDenominator)
    {
        byte* basePtr = (byte*)param.ToPointer();
        uint podSize = ReadUInt(basePtr, 0);
        uint podType = ReadUInt(basePtr, 4);
        int end = checked((int)(8 + podSize));
        int offset = 16;

        videoFormat = 0;
        width = 0;
        height = 0;
        fpsNumerator = Volatile.Read(ref _fpsLimit);
        fpsDenominator = 1;

        if (podType != SpaTypeObject || podSize < 8)
            return false;

        while (offset + 16 <= end)
        {
            offset = Align8(offset);
            if (offset + 16 > end)
                break;

            uint key = ReadUInt(basePtr, offset);
            uint valueSize = ReadUInt(basePtr, offset + 8);
            uint valueType = ReadUInt(basePtr, offset + 12);
            int valueBody = offset + 16;
            int valueEnd = checked(valueBody + (int)valueSize);
            if (valueEnd > end)
                break;

            switch (key)
            {
                case SpaFormatVideoFormat:
                    videoFormat = ReadIdValue(basePtr, valueBody, valueSize, valueType);
                    break;
                case SpaFormatVideoSize when valueType == SpaTypeRectangle && valueSize >= 8:
                    width = checked((int)ReadUInt(basePtr, valueBody));
                    height = checked((int)ReadUInt(basePtr, valueBody + 4));
                    break;
                case SpaFormatVideoFramerate when valueType == SpaTypeFraction && valueSize >= 8:
                    fpsNumerator = checked((int)ReadUInt(basePtr, valueBody));
                    fpsDenominator = Math.Max(1, checked((int)ReadUInt(basePtr, valueBody + 4)));
                    break;
            }

            offset = Align8(valueEnd);
        }

        return IsSupportedDirectVideoFormat(videoFormat) && width > 0 && height > 0;
    }

    private static unsafe uint ReadIdValue(byte* basePtr, int valueBody, uint valueSize, uint valueType)
    {
        if (valueType == SpaTypeId && valueSize >= 4)
            return ReadUInt(basePtr, valueBody);

        if (valueType != SpaTypeChoice || valueSize < 20)
            return 0;

        uint childSize = ReadUInt(basePtr, valueBody + 8);
        uint childType = ReadUInt(basePtr, valueBody + 12);
        if (childType != SpaTypeId || childSize < 4)
            return 0;

        return ReadUInt(basePtr, valueBody + 16);
    }

    private static unsafe uint ReadUInt(byte* basePtr, int offset)
    {
        return *(uint*)(basePtr + offset);
    }

    private static int Align8(int value)
    {
        return (value + 7) & ~7;
    }

    private static string ParamName(uint id)
    {
        return id switch
        {
            0 => "Invalid",
            1 => "PropInfo",
            2 => "Props",
            SpaParamEnumFormat => "EnumFormat",
            SpaParamFormat => "Format",
            SpaParamBuffers => "Buffers",
            6 => "Meta",
            7 => "IO",
            _ => "Unknown"
        };
    }

    private static string StreamStateName(int state)
    {
        return state switch
        {
            -1 => "Error",
            0 => "Unconnected",
            1 => "Connecting",
            2 => "Paused",
            3 => "Streaming",
            _ => state.ToString()
        };
    }

    private static string VideoFormatName(uint videoFormat)
    {
        return videoFormat switch
        {
            SpaVideoFormatRgbx => "RGBx",
            SpaVideoFormatBgrx => "BGRx",
            SpaVideoFormatXrgb => "xRGB",
            SpaVideoFormatXbgr => "xBGR",
            SpaVideoFormatRgba => "RGBA",
            SpaVideoFormatBgra => "BGRA",
            SpaVideoFormatArgb => "ARGB",
            SpaVideoFormatAbgr => "ABGR",
            _ => $"Unknown({videoFormat})"
        };
    }

    private static PortalPipeWirePixelFormat ToPortalPixelFormat(uint videoFormat)
    {
        return videoFormat switch
        {
            SpaVideoFormatRgbx => PortalPipeWirePixelFormat.Rgbx,
            SpaVideoFormatBgrx => PortalPipeWirePixelFormat.Bgrx,
            SpaVideoFormatXrgb => PortalPipeWirePixelFormat.Xrgb,
            SpaVideoFormatXbgr => PortalPipeWirePixelFormat.Xbgr,
            SpaVideoFormatRgba => PortalPipeWirePixelFormat.Rgba,
            SpaVideoFormatBgra => PortalPipeWirePixelFormat.Bgra,
            SpaVideoFormatArgb => PortalPipeWirePixelFormat.Argb,
            SpaVideoFormatAbgr => PortalPipeWirePixelFormat.Abgr,
            _ => PortalPipeWirePixelFormat.Bgrx
        };
    }

    private static bool IsSupportedDirectVideoFormat(uint videoFormat)
    {
        return videoFormat is
            SpaVideoFormatRgbx or
            SpaVideoFormatBgrx or
            SpaVideoFormatXrgb or
            SpaVideoFormatXbgr or
            SpaVideoFormatRgba or
            SpaVideoFormatBgra or
            SpaVideoFormatArgb or
            SpaVideoFormatAbgr;
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

        public void AddInt(uint key, int value)
        {
            BeginProperty(key);
            WriteUInt(4);
            WriteUInt(SpaTypeInt);
            WriteInt(value);
            WriteUInt(0);
        }

        public void AddChoiceEnumId(uint key, params uint[] values)
        {
            if (values.Length == 0)
                throw new ArgumentException("At least one choice value is required.", nameof(values));

            BeginProperty(key);
            WriteUInt(checked((uint)(16 + values.Length * 4)));
            WriteUInt(SpaTypeChoice);
            WriteUInt(SpaChoiceEnum);
            WriteUInt(0);
            WriteUInt(4);
            WriteUInt(SpaTypeId);
            foreach (uint value in values)
                WriteUInt(value);
            Align();
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

        private void WriteInt(int value)
        {
            if (_offset + 4 > _capacity)
                throw new InvalidOperationException("SPA POD parameter buffer is too small.");

            *(int*)(_buffer + _offset) = value;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ParamChangedCallback(IntPtr data, uint id, IntPtr param);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferChangedCallback(IntPtr data, IntPtr buffer);

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

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr pw_properties_new_string(string properties);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_stream_destroy(IntPtr stream);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pw_stream_add_listener(IntPtr stream, IntPtr listener, IntPtr events, IntPtr data);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_connect(IntPtr stream, int direction, uint targetId, uint flags, IntPtr parameters, uint parameterCount);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_set_active(IntPtr stream, [MarshalAs(UnmanagedType.I1)] bool active);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_update_params(IntPtr stream, IntPtr parameters, uint parameterCount);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pw_stream_dequeue_buffer(IntPtr stream);

    [DllImport(PipeWireLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
