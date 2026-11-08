using Artemis.Core.Services;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Artemis.Core;
using RazerSdkReader;
using RazerSdkReader.Structures;
using Serilog;

namespace Artemis.Plugins.LayerBrushes.Chroma.Services;

public record MatrixUpdatedEventArgs(RzDeviceType DeviceType, SKColor[] Matrix);

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ChromaService : IPluginService, IDisposable
{
    // Razer signals an app change (a game starting or exiting) once, and the reader sometimes misses
    // that signal. CurrentApp then stays empty and the game's lighting never shows, even though the
    // app data in shared memory is up to date. So the app data is also polled from shared memory.
    private const string AppDataMapName = @"Global\{D4E1A960-872F-4BF8-B09A-9E54F646D7CE}";
    private static readonly TimeSpan AppDataPollInterval = TimeSpan.FromSeconds(1);

    private readonly SKColor[][] _matrices;
    private readonly ChromaReader _reader;
    private readonly ILogger _logger;
    private readonly object _appLock = new();
    private readonly MemoryMappedFile _appDataFile;
    private readonly MemoryMappedViewAccessor _appDataView;
    private readonly System.Threading.Timer _appDataPollTimer;
    private readonly Profiler _profiler;
    private readonly object _lock;
    private readonly MatrixUpdatedEventArgs[] _matrixUpdatedEventArgs;

    public event EventHandler<MatrixUpdatedEventArgs>? MatrixUpdated;
    public event EventHandler? AppListUpdated;

    public bool IsActive => !string.IsNullOrWhiteSpace(CurrentApp) && CurrentApp != "Artemis.UI.Windows.exe";
    public string? CurrentApp { get; private set; }
    public uint? CurrentAppId { get; private set; }
    public string[] AppNames { get; }
    public uint[] AppIds { get; }

    public ChromaService(Plugin plugin, ILogger logger)
    {
        _logger = logger;
        _profiler = plugin.GetProfiler("Chroma Service");
        _lock = new object();
        AppNames = new string[50];
        AppIds = new uint[50];

        var deviceTypes = Enum.GetValues<RzDeviceType>();
        _matrices = deviceTypes.Select(deviceType => new SKColor[deviceType.GetLength()]).ToArray();
        _matrixUpdatedEventArgs = deviceTypes.Select(deviceType => new MatrixUpdatedEventArgs(deviceType, _matrices[(int)deviceType])).ToArray();

        _reader = new ChromaReader();
        _reader.KeyboardUpdated += RazerEmulatorReaderOnKeyboardUpdated;
        _reader.MouseUpdated += RazerEmulatorReaderOnMouseUpdated;
        _reader.MousepadUpdated += RazerEmulatorReaderOnMousepadUpdated;
        _reader.KeypadUpdated += RazerEmulatorReaderOnKeypadUpdated;
        _reader.HeadsetUpdated += RazerEmulatorReaderOnHeadsetUpdated;
        _reader.ChromaLinkUpdated += RazerEmulatorReaderOnChromaLinkUpdated;
        _reader.AppDataUpdated += RazerEmulatorReaderOnAppDataUpdated;
        _reader.Start();

        _appDataFile = MemoryMappedFile.OpenExisting(AppDataMapName, MemoryMappedFileRights.Read);
        _appDataView = _appDataFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _appDataPollTimer = new System.Threading.Timer(_ => PollAppData(), null, AppDataPollInterval, AppDataPollInterval);
    }

    private void PollAppData()
    {
        try
        {
            _appDataView.Read(0, out ChromaAppData app);
            if (app.CurrentAppId == CurrentAppId)
                return;

            _logger.Information("Chroma app change missed by the reader, picked up by polling: {App}", app.CurrentAppName);
            UpdateAppListData(in app);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to poll Chroma app data");
        }
    }

    private void RazerEmulatorReaderOnAppDataUpdated(object? sender, in ChromaAppData e) => UpdateAppListData(in e);

    private void RazerEmulatorReaderOnChromaLinkUpdated(object? sender, in ChromaLink e) => UpdateMatrix(RzDeviceType.ChromaLink, in e);

    private void RazerEmulatorReaderOnHeadsetUpdated(object? sender, in ChromaHeadset e) => UpdateMatrix(RzDeviceType.Headset, in e);

    private void RazerEmulatorReaderOnKeypadUpdated(object? sender, in ChromaKeypad e) => UpdateMatrix(RzDeviceType.Keypad, in e);

    private void RazerEmulatorReaderOnMousepadUpdated(object? sender, in ChromaMousepad e) => UpdateMatrix(RzDeviceType.Mousepad, in e);

    private void RazerEmulatorReaderOnMouseUpdated(object? sender, in ChromaMouse e) => UpdateMatrix(RzDeviceType.Mouse, in e);

    private void RazerEmulatorReaderOnKeyboardUpdated(object? sender, in ChromaKeyboard e) => UpdateMatrix(RzDeviceType.Keyboard, in e);

    private void UpdateMatrix<T>(RzDeviceType deviceType, in T data) where T : IColorProvider
    {
        var profilerName = deviceType.ToStringFast();

        _profiler.StartMeasurement(profilerName);
        
        lock (_lock)
        {
            var matrix = _matrices[(int)deviceType];
            //SKColor and ChromaColor are the same size and layout,
            //so we can just write to it directly.
            var colors = MemoryMarshal.Cast<SKColor, ChromaColor>(matrix.AsSpan());
            data.GetColors(colors);

            MatrixUpdated?.Invoke(this, _matrixUpdatedEventArgs[(int)deviceType]);
        }

        _profiler.StopMeasurement(profilerName);
    }

    private void UpdateAppListData(in ChromaAppData app)
    {
        // Called by both the reader and the poll timer.
        lock (_appLock)
            UpdateAppListDataLocked(in app);
    }

    private void UpdateAppListDataLocked(in ChromaAppData app)
    {
        _profiler.StartMeasurement("AppList");
        
        CurrentAppId = app.CurrentAppId;
        CurrentApp = app.CurrentAppName;

        ReadOnlySpan<ChromaAppInfo> apps = app.AppInfo;
        for (var i = 0; i < apps.Length; i++)
        {
            ref readonly var appInfo = ref apps[i];
            AppNames[i] = appInfo.AppName;
            AppIds[i] = appInfo.AppId;
        }

        AppListUpdated?.Invoke(this, EventArgs.Empty);
        
        _profiler.StopMeasurement("AppList");
    }

    public void Dispose()
    {
        // Wait for a running poll before closing the view it reads.
        using (var pollStopped = new System.Threading.ManualResetEvent(false))
            if (_appDataPollTimer.Dispose(pollStopped))
                pollStopped.WaitOne();
        _appDataView.Dispose();
        _appDataFile.Dispose();
        _reader.KeyboardUpdated -= RazerEmulatorReaderOnKeyboardUpdated;
        _reader.MouseUpdated -= RazerEmulatorReaderOnMouseUpdated;
        _reader.MousepadUpdated -= RazerEmulatorReaderOnMousepadUpdated;
        _reader.KeypadUpdated -= RazerEmulatorReaderOnKeypadUpdated;
        _reader.HeadsetUpdated -= RazerEmulatorReaderOnHeadsetUpdated;
        _reader.ChromaLinkUpdated -= RazerEmulatorReaderOnChromaLinkUpdated;
        _reader.AppDataUpdated -= RazerEmulatorReaderOnAppDataUpdated;
        _reader.Dispose();
    }
}