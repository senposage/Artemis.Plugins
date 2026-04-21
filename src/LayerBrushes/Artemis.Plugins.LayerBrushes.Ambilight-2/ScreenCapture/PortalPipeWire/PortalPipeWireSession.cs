using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Plugins.LayerBrushes.Ambilight;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Tmds.DBus;

namespace Artemis.Plugins.LayerBrushes.Ambilight.ScreenCapture.PortalPipeWire;

internal sealed class PortalPipeWireSession : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<PortalPipeWireSession>();

    private const string PortalDestination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const uint SourceTypeMonitor = 1;
    private const uint CursorModeHidden = 1;
    private const uint PersistUntilRevoked = 2;
    private const string GeneratedDesktopAppId = "org.artemisrgb.Artemis";
    private static readonly string[] FallbackPortalAppIds =
    [
        GeneratedDesktopAppId,
        "artemis"
    ];

    private readonly Connection _connection;
    private readonly IScreenCastPortal _screenCast;
    private readonly ISessionPortal _session;
    private readonly SafeHandle _pipeWireRemote;

    public IReadOnlyList<PortalStream> Streams { get; }
    public int PipeWireRemoteFd => (int)_pipeWireRemote.DangerousGetHandle();

    private PortalPipeWireSession(Connection connection, ObjectPath sessionHandle, SafeHandle pipeWireRemote, IReadOnlyList<PortalStream> streams)
    {
        _connection = connection;
        _screenCast = connection.CreateProxy<IScreenCastPortal>(PortalDestination, PortalPath);
        _session = connection.CreateProxy<ISessionPortal>(PortalDestination, sessionHandle);
        _pipeWireRemote = pipeWireRemote;
        Streams = streams;
    }

    public static PortalPipeWireSession Create(CancellationToken cancellationToken = default)
    {
        return CreateAsync(cancellationToken).GetAwaiter().GetResult();
    }

    public static void ResetStoredRestoreToken()
    {
        PortalRestoreTokenStore.Delete();
    }

    private static async Task<PortalPipeWireSession> CreateAsync(CancellationToken cancellationToken)
    {
        AllowPortalProcessInspection();

        Logger.Information("Connecting to D-Bus session bus for XDG ScreenCast portal");
        var connection = new Connection(Address.Session);
        ConnectionInfo connectionInfo = await connection.ConnectAsync().ConfigureAwait(false);
        Logger.Information("Connected to D-Bus session bus as {LocalName}", connectionInfo.LocalName);
        string registrationSummary = "XDG portal app id registration was not attempted.";
        try
        {
            registrationSummary = await RegisterPortalAppId(connection).ConfigureAwait(false);
            IScreenCastPortal screenCast = connection.CreateProxy<IScreenCastPortal>(PortalDestination, PortalPath);

            string token = CreateToken();
            Logger.Information("Creating XDG ScreenCast session with token {Token}", token);
            string createToken = $"{token}_create";
            string sessionToken = $"{token}_session";
            PortalResponse createResponse = await PortalResponse.WaitAsync(connection, connectionInfo.LocalName, createToken, () => screenCast.CreateSessionAsync(new Dictionary<string, object>
            {
                ["handle_token"] = createToken,
                ["session_handle_token"] = sessionToken
            }), cancellationToken).ConfigureAwait(false);

            var sessionHandle = new ObjectPath(ReadString(createResponse.Results, "session_handle"));
            Logger.Information("XDG ScreenCast session created: {SessionHandle}", sessionHandle);

            Logger.Information("Selecting monitor sources through XDG ScreenCast portal");
            string sourcesToken = $"{token}_sources";
            Dictionary<string, object> selectSourcesOptions = new()
            {
                ["handle_token"] = sourcesToken,
                ["types"] = SourceTypeMonitor,
                ["multiple"] = true,
                ["cursor_mode"] = CursorModeHidden,
                ["persist_mode"] = PersistUntilRevoked
            };

            string? restoreToken = PortalRestoreTokenStore.Load();
            if (!string.IsNullOrWhiteSpace(restoreToken))
            {
                Logger.Information("Using stored XDG ScreenCast restore token");
                selectSourcesOptions["restore_token"] = restoreToken;
            }
            else
            {
                Logger.Information("No stored XDG ScreenCast restore token found; portal may prompt for monitor sharing");
            }

            await PortalResponse.WaitAsync(connection, connectionInfo.LocalName, sourcesToken, () => screenCast.SelectSourcesAsync(sessionHandle, selectSourcesOptions), cancellationToken).ConfigureAwait(false);

            Logger.Information("Starting XDG ScreenCast portal session");
            string startToken = $"{token}_start";
            PortalResponse startResponse = await PortalResponse.WaitAsync(connection, connectionInfo.LocalName, startToken, () => screenCast.StartAsync(sessionHandle, "", new Dictionary<string, object>
            {
                ["handle_token"] = startToken
            }), cancellationToken).ConfigureAwait(false);

            PortalRestoreTokenStore.Save(ReadString(startResponse.Results, "restore_token"));

            IReadOnlyList<PortalStream> streams = ReadStreams(startResponse.Results);
            if (streams.Count == 0)
                throw new InvalidOperationException("The portal did not return any PipeWire monitor streams.");

            foreach (PortalStream stream in streams)
            {
                AmbilightLinuxDiagnostics.Write(Logger,
                    $"portal stream node={stream.NodeId} stableId={stream.StableId} pos={stream.X},{stream.Y} size={stream.Width}x{stream.Height}");
                Logger.Information(
                    "Portal stream: node={NodeId} stableId={StableId} pos={X},{Y} size={Width}x{Height}",
                    stream.NodeId, stream.StableId, stream.X, stream.Y, stream.Width, stream.Height);
            }

            Logger.Information("Opening PipeWire remote through XDG ScreenCast portal");
            SafeHandle pipeWireRemote = await screenCast.OpenPipeWireRemoteAsync(sessionHandle, new Dictionary<string, object>()).ConfigureAwait(false);
            if (pipeWireRemote.IsInvalid)
                throw new InvalidOperationException("The portal returned an invalid PipeWire remote descriptor.");

            AmbilightLinuxDiagnostics.Write(Logger, $"PipeWire remote opened fd={pipeWireRemote.DangerousGetHandle()}");
            Logger.Information("PipeWire remote opened: fd={Fd}", pipeWireRemote.DangerousGetHandle());
            return new PortalPipeWireSession(connection, sessionHandle, pipeWireRemote, streams);
        }
        catch (DBusException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.AccessDenied" &&
                                       ex.Message.Contains("/proc/", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                "XDG ScreenCast portal denied access while identifying Artemis. " +
                $"{registrationSummary} " +
                "This usually means xdg-desktop-portal could not identify the unsandboxed application from its desktop file and then failed its /proc/<pid>/root fallback. " +
                $"Original portal error: {ex.ErrorName}: {ex.Message}",
                ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task<string> RegisterPortalAppId(Connection connection)
    {
        string[] appIds = GetPortalAppIds();
        if (appIds.Length == 0)
        {
            Logger.Warning("No candidate XDG portal app ids were found; continuing without app id registration");
            return "No candidate XDG portal app ids were found.";
        }

        try
        {
            List<string> failures = [];
            IPortalRegistry registry = connection.CreateProxy<IPortalRegistry>(PortalDestination, PortalPath);
            foreach (string appId in appIds)
            {
                try
                {
                    Logger.Information("Registering XDG portal app id {AppId}", appId);
                    await registry.RegisterAsync(appId, new Dictionary<string, object>()).ConfigureAwait(false);
                    Logger.Information("Registered XDG portal app id {AppId}", appId);
                    return $"XDG portal Registry accepted app id '{appId}'.";
                }
                catch (DBusException ex) when (IsAlreadyRegisteredFailure(ex))
                {
                    Logger.Information("XDG portal app id was already registered for this D-Bus peer: {Message}", ex.Message);
                    return $"XDG portal Registry reported this D-Bus peer was already registered: {ex.ErrorName}: {ex.Message}";
                }
                catch (DBusException ex) when (IsRetryableAppIdFailure(ex))
                {
                    Logger.Warning("XDG portal rejected app id {AppId}: {ErrorName}: {Message}", appId, ex.ErrorName, ex.Message);
                    failures.Add($"{appId} => {ex.ErrorName}: {ex.Message}");
                }
            }

            string failureSummary = failures.Count == 0
                ? string.Join(", ", appIds)
                : string.Join("; ", failures);
            Logger.Warning("XDG portal rejected every candidate app id: {Failures}", failureSummary);
            return $"XDG portal Registry rejected every candidate app id: {failureSummary}.";
        }
        catch (DBusException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.UnknownInterface" ||
                                       ex.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod")
        {
            Logger.Information("XDG portal registry is unavailable, continuing without app id registration: {Message}", ex.Message);
            return $"XDG portal Registry is unavailable: {ex.ErrorName}: {ex.Message}";
        }
        catch (DBusException ex) when (IsAlreadyRegisteredFailure(ex))
        {
            Logger.Information("XDG portal app id was already registered for this D-Bus peer: {Message}", ex.Message);
            return $"XDG portal Registry reported this D-Bus peer was already registered: {ex.ErrorName}: {ex.Message}";
        }
    }

    private static bool IsAlreadyRegisteredFailure(DBusException ex)
    {
        return ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("associated", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);

    private const int PR_SET_DUMPABLE = 4;
    private const int PR_SET_PTRACER = 0x59616d61;
    private static readonly nint PR_SET_PTRACER_ANY = -1;

    private static void AllowPortalProcessInspection()
    {
        if (!OperatingSystem.IsLinux())
            return;

        int dumpableResult = prctl(PR_SET_DUMPABLE, 1, 0, 0, 0);
        int dumpableErrno = Marshal.GetLastWin32Error();
        int ptracerResult = prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY, 0, 0, 0);
        int ptracerErrno = Marshal.GetLastWin32Error();

        string message = $"[Ambilight] portal proc access setup: PR_SET_DUMPABLE result={dumpableResult} errno={dumpableErrno}; PR_SET_PTRACER_ANY result={ptracerResult} errno={ptracerErrno}";
        Logger.Information(message);
        Console.Error.WriteLine(message);

        try
        {
            string status = string.Join(
                " ",
                File.ReadLines("/proc/self/status")
                    .Where(line => line.StartsWith("Uid:", StringComparison.Ordinal) ||
                                   line.StartsWith("Gid:", StringComparison.Ordinal) ||
                                   line.StartsWith("NoNewPrivs:", StringComparison.Ordinal) ||
                                   line.StartsWith("CapEff:", StringComparison.Ordinal)));
            Logger.Information("[Ambilight] portal proc status: {Status}", status);
            Console.Error.WriteLine($"[Ambilight] portal proc status: {status}");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read /proc/self/status for portal diagnostics");
        }
    }

    private static bool IsRetryableAppIdFailure(DBusException ex)
    {
        return ex.ErrorName == "org.freedesktop.portal.Error.InvalidArgument" ||
               ex.ErrorName == "org.freedesktop.DBus.Error.InvalidArgs" ||
               ex.Message.Contains("desktop", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("app", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetPortalAppIds()
    {
        string? overrideAppId = Environment.GetEnvironmentVariable("ARTEMIS_PORTAL_APP_ID");
        if (!string.IsNullOrWhiteSpace(overrideAppId))
        {
            Logger.Information("Using XDG portal app id override from ARTEMIS_PORTAL_APP_ID={AppId}", overrideAppId);
            return [overrideAppId.Trim()];
        }

        List<string> appIds = [];
        string? desktopAppId = FindInstalledArtemisDesktopAppId();
        if (!string.IsNullOrWhiteSpace(desktopAppId))
            appIds.Add(desktopAppId);

        if (TryEnsureArtemisDesktopFile(GeneratedDesktopAppId))
            appIds.Add(GeneratedDesktopAppId);

        appIds.AddRange(FallbackPortalAppIds);

        return appIds
            .Where(appId => !string.IsNullOrWhiteSpace(appId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? FindInstalledArtemisDesktopAppId()
    {
        foreach (string applicationsDirectory in GetApplicationsDirectories())
        {
            if (!Directory.Exists(applicationsDirectory))
                continue;

            IEnumerable<string> desktopFiles;
            try
            {
                desktopFiles = Directory.EnumerateFiles(applicationsDirectory, "*.desktop", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not enumerate desktop files in {Directory}", applicationsDirectory);
                continue;
            }

            foreach (string desktopFile in desktopFiles)
            {
                if (!IsArtemisDesktopFile(desktopFile))
                    continue;

                string appId = Path.GetFileNameWithoutExtension(desktopFile);
                Logger.Information(
                    "Found Artemis desktop file {DesktopFile}; using portal app id {AppId}. {DesktopSummary}",
                    desktopFile,
                    appId,
                    ReadDesktopFileSummary(desktopFile));
                return appId;
            }
        }

        Logger.Information("No installed Artemis desktop file was found; falling back to candidate app ids {AppIds}", string.Join(", ", FallbackPortalAppIds));
        return null;
    }

    private static bool TryEnsureArtemisDesktopFile(string appId)
    {
        string? disabled = Environment.GetEnvironmentVariable("ARTEMIS_PORTAL_AUTO_DESKTOP_FILE");
        if (string.Equals(disabled, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(disabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Information("Automatic Artemis desktop file creation is disabled by ARTEMIS_PORTAL_AUTO_DESKTOP_FILE={Value}", disabled);
            return false;
        }

        try
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                Logger.Warning("Cannot create Artemis desktop file because Environment.ProcessPath is empty");
                return false;
            }

            string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dataHome = !string.IsNullOrWhiteSpace(xdgDataHome)
                ? xdgDataHome
                : Path.Combine(home, ".local", "share");

            string applicationsDirectory = Path.Combine(dataHome, "applications");
            string desktopFile = Path.Combine(applicationsDirectory, $"{appId}.desktop");
            Directory.CreateDirectory(applicationsDirectory);

            if (File.Exists(desktopFile))
            {
                Logger.Information(
                    "Generated Artemis desktop file already exists at {DesktopFile}. {DesktopSummary}",
                    desktopFile,
                    ReadDesktopFileSummary(desktopFile));
                return true;
            }

            string escapedProcessPath = processPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string contents =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Artemis\n" +
                $"Exec=\"{escapedProcessPath}\"\n" +
                "Terminal=false\n" +
                "StartupWMClass=Artemis\n" +
                "Categories=Utility;\n";

            File.WriteAllText(desktopFile, contents);
            Logger.Information("Created Artemis desktop file {DesktopFile} for XDG portal app id {AppId}", desktopFile, appId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not create Artemis desktop file for XDG portal app id registration");
            return false;
        }
    }

    private static string ReadDesktopFileSummary(string desktopFile)
    {
        try
        {
            string? name = null;
            string? exec = null;
            string? startupWmClass = null;

            foreach (string line in File.ReadLines(desktopFile))
            {
                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                    name = line;
                else if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                    exec = line;
                else if (line.StartsWith("StartupWMClass=", StringComparison.OrdinalIgnoreCase))
                    startupWmClass = line;
            }

            return string.Join(" ", new[] { name, exec, startupWmClass }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read desktop file summary for {DesktopFile}", desktopFile);
            return "Could not read desktop file summary.";
        }
    }

    private static IEnumerable<string> GetApplicationsDirectories()
    {
        string? xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            yield return Path.Combine(xdgDataHome, "applications");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".local", "share", "applications");

        string? xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        string[] dataDirs = string.IsNullOrWhiteSpace(xdgDataDirs)
            ? ["/usr/local/share", "/usr/share"]
            : xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string dataDir in dataDirs)
            yield return Path.Combine(dataDir, "applications");
    }

    private static bool IsArtemisDesktopFile(string desktopFile)
    {
        string fileName = Path.GetFileNameWithoutExtension(desktopFile);
        if (fileName.Contains("artemis", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            foreach (string line in File.ReadLines(desktopFile))
            {
                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Artemis", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Artemis", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (line.StartsWith("StartupWMClass=", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("Artemis", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read desktop file {DesktopFile}", desktopFile);
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _session.CloseAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _pipeWireRemote.Dispose();
        _connection.Dispose();
    }

    private static string CreateToken()
    {
        return $"artemis_ambilight_{Environment.ProcessId}_{Guid.NewGuid():N}";
    }

    private static string ReadString(IDictionary<string, object> results, string key)
    {
        return results.TryGetValue(key, out object? value) ? value.ToString() ?? "" : "";
    }

    private static IReadOnlyList<PortalStream> ReadStreams(IDictionary<string, object> results)
    {
        if (!results.TryGetValue("streams", out object? value) || value is not IEnumerable streamObjects)
        {
            Logger.Warning("Portal Start response did not contain a streams array. Results: {Results}", DumpDictionary(results));
            return [];
        }

        var streams = new List<PortalStream>();
        int index = 0;
        foreach (object streamObject in streamObjects)
        {
            if (!TryReadStreamTuple(streamObject, out uint nodeId, out IDictionary<string, object>? properties))
            {
                Logger.Warning("Ignoring unexpected portal stream entry: {Entry}", DumpPortalValue(streamObject));
                continue;
            }

            string id = ReadString(properties, "id");
            string mappingId = ReadString(properties, "mapping_id");
            (int x, int y) = ReadIntTuple(properties, "position");
            (int width, int height) = ReadIntTuple(properties, "size");
            if (width <= 0 || height <= 0)
            {
                Logger.Warning(
                    "Ignoring portal stream node={NodeId} because size was missing or invalid. Properties: {Properties}",
                    nodeId,
                    DumpDictionary(properties));
                continue;
            }

            string stableId = !string.IsNullOrWhiteSpace(id)
                ? $"portal:id:{id}"
                : !string.IsNullOrWhiteSpace(mappingId)
                    ? $"portal:mapping:{mappingId}"
                    : $"portal:geometry:{x}:{y}:{width}:{height}:stream{index}";

            streams.Add(new PortalStream(nodeId, stableId, x, y, width, height));
            index++;
        }

        return streams;
    }

    private static bool TryReadStreamTuple(object streamObject, out uint nodeId, out IDictionary<string, object>? properties)
    {
        nodeId = 0;
        properties = null;

        if (streamObject is ValueTuple<uint, IDictionary<string, object>> typedTuple)
        {
            nodeId = typedTuple.Item1;
            properties = typedTuple.Item2;
            return true;
        }

        if (streamObject is not ITuple tuple || tuple.Length < 2)
            return false;

        if (!TryReadUInt(tuple[0], out nodeId))
            return false;

        properties = ReadObjectDictionary(tuple[1]);
        return properties != null;
    }

    private static bool TryReadUInt(object? value, out uint result)
    {
        switch (value)
        {
            case uint uintValue:
                result = uintValue;
                return true;
            case int intValue when intValue >= 0:
                result = (uint)intValue;
                return true;
            case ulong ulongValue when ulongValue <= uint.MaxValue:
                result = (uint)ulongValue;
                return true;
            case long longValue when longValue >= 0 && longValue <= uint.MaxValue:
                result = (uint)longValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static IDictionary<string, object>? ReadObjectDictionary(object? value)
    {
        if (value is IDictionary<string, object> typed)
            return typed;

        if (value is not IDictionary dictionary)
            return null;

        var result = new Dictionary<string, object>();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key && entry.Value != null)
                result[key] = entry.Value;
        }

        return result;
    }

    private static (int X, int Y) ReadIntTuple(IDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value))
            return (0, 0);

        return value switch
        {
            ValueTuple<int, int> tuple => tuple,
            ValueTuple<uint, uint> tuple => ((int)tuple.Item1, (int)tuple.Item2),
            ITuple tuple when tuple.Length >= 2 && TryReadInt(tuple[0], out int first) && TryReadInt(tuple[1], out int second) => (first, second),
            _ => (0, 0)
        };
    }

    private static bool TryReadInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case uint uintValue when uintValue <= int.MaxValue:
                result = (int)uintValue;
                return true;
            case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                result = (int)longValue;
                return true;
            case ulong ulongValue when ulongValue <= int.MaxValue:
                result = (int)ulongValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string DumpDictionary(IDictionary<string, object> dictionary)
    {
        return string.Join("; ", dictionary.Select(pair => $"{pair.Key}={DumpPortalValue(pair.Value)}"));
    }

    private static string DumpPortalValue(object? value)
    {
        if (value == null)
            return "(null)";

        if (value is string text)
            return $"\"{text}\"";

        if (value is IDictionary<string, object> typedDictionary)
            return "{" + DumpDictionary(typedDictionary) + "}";

        if (value is IDictionary dictionary)
        {
            List<string> entries = [];
            foreach (DictionaryEntry entry in dictionary)
                entries.Add($"{entry.Key}={DumpPortalValue(entry.Value)}");
            return "{" + string.Join("; ", entries) + "}";
        }

        if (value is ITuple tuple)
        {
            List<string> items = [];
            for (int i = 0; i < tuple.Length; i++)
                items.Add(DumpPortalValue(tuple[i]));
            return $"{value.GetType().FullName}({string.Join(", ", items)})";
        }

        if (value is IEnumerable enumerable and not byte[])
        {
            List<string> items = [];
            foreach (object? item in enumerable)
                items.Add(DumpPortalValue(item));
            return $"{value.GetType().FullName}[{string.Join(", ", items)}]";
        }

        return $"{value} ({value.GetType().FullName})";
    }

    private sealed class PortalResponse
    {
        public required IDictionary<string, object> Results { get; init; }

        public static async Task<PortalResponse> WaitAsync(Connection connection, string localName, string handleToken, Func<Task<ObjectPath>> request, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            ObjectPath expectedRequestPath = GetExpectedRequestPath(localName, handleToken);
            Logger.Information("Subscribing to portal request response before call: {RequestPath}", expectedRequestPath);
            IRequestPortal requestPortal = connection.CreateProxy<IRequestPortal>(PortalDestination, expectedRequestPath);
            IDisposable? subscription = null;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));

            subscription = await requestPortal.WatchResponseAsync(response =>
            {
                Logger.Information("Portal request {RequestPath} returned response={Response} results={Results}", expectedRequestPath, response.Response, DumpDictionary(response.Results));
                if (response.Response == 0)
                    source.TrySetResult(new PortalResponse { Results = response.Results });
                else
                    source.TrySetException(new InvalidOperationException($"Portal request failed with response code {response.Response}."));
            }).ConfigureAwait(false);

            try
            {
                ObjectPath actualRequestPath = await request().ConfigureAwait(false);
                if (!actualRequestPath.Equals(expectedRequestPath))
                    Logger.Warning("Portal returned request path {ActualRequestPath}, expected {ExpectedRequestPath}", actualRequestPath, expectedRequestPath);
                else
                    Logger.Information("Portal returned expected request path {RequestPath}", actualRequestPath);

                return await source.Task.ConfigureAwait(false);
            }
            finally
            {
                subscription.Dispose();
            }
        }

        private static ObjectPath GetExpectedRequestPath(string localName, string handleToken)
        {
            if (string.IsNullOrWhiteSpace(localName))
                throw new InvalidOperationException("D-Bus connection does not have a local name.");

            string sender = localName.TrimStart(':').Replace('.', '_');
            return new ObjectPath($"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}");
        }
    }

    private static class PortalRestoreTokenStore
    {
        private const string FileName = "portal-screencast-restore-token.txt";

        public static string? Load()
        {
            try
            {
                string path = GetPath();
                if (!File.Exists(path))
                    return null;

                string token = File.ReadAllText(path).Trim();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not read stored XDG ScreenCast restore token");
                return null;
            }
        }

        public static void Save(string? restoreToken)
        {
            if (string.IsNullOrWhiteSpace(restoreToken))
            {
                Logger.Information("XDG ScreenCast portal did not return a restore token; monitor sharing may prompt again next startup");
                return;
            }

            try
            {
                string path = GetPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, restoreToken.Trim());
                Logger.Information("Stored XDG ScreenCast restore token at {Path}", path);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not store XDG ScreenCast restore token; monitor sharing may prompt again next startup");
            }
        }

        public static void Delete()
        {
            try
            {
                string path = GetPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logger.Information("Deleted stored XDG ScreenCast restore token at {Path}", path);
                }
                else
                {
                    Logger.Information("No stored XDG ScreenCast restore token exists to delete at {Path}", path);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Could not delete stored XDG ScreenCast restore token");
            }
        }

        private static string GetPath()
        {
            string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                configHome = Path.Combine(home, ".config");
            }

            return Path.Combine(configHome, "Artemis", "Ambilight", FileName);
        }
    }

    public readonly record struct PortalStream(uint NodeId, string StableId, int X, int Y, int Width, int Height);
}

[DBusInterface("org.freedesktop.portal.ScreenCast")]
public interface IScreenCastPortal : IDBusObject
{
    Task<ObjectPath> CreateSessionAsync(IDictionary<string, object> options);
    Task<ObjectPath> SelectSourcesAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
    Task<ObjectPath> StartAsync(ObjectPath sessionHandle, string parentWindow, IDictionary<string, object> options);
    Task<SafeFileHandle> OpenPipeWireRemoteAsync(ObjectPath sessionHandle, IDictionary<string, object> options);
}

[DBusInterface("org.freedesktop.portal.Request")]
public interface IRequestPortal : IDBusObject
{
    Task<IDisposable> WatchResponseAsync(Action<(uint Response, IDictionary<string, object> Results)> handler, Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.portal.Session")]
public interface ISessionPortal : IDBusObject
{
    Task CloseAsync();
}

[DBusInterface("org.freedesktop.host.portal.Registry")]
public interface IPortalRegistry : IDBusObject
{
    Task RegisterAsync(string appId, IDictionary<string, object> options);
}
