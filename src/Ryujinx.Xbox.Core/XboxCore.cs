using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Cpu;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.Xbox.Core.Graphics;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Switch = Ryujinx.HLE.Switch;

namespace Ryujinx.Xbox.Core
{
    /// <summary>
    /// C ABI of the core. The Xbox UWP shell (and the CI test host) load this NativeAOT library and drive emulation
    /// through these exports. Strings are UTF-8, results are 0 on success and negative on failure.
    /// </summary>
    public static unsafe class XboxCore
    {
        private static readonly Stopwatch _uptime = new();
        private static readonly LogRing _logRing = new(512);

        private static VirtualFileSystem _virtualFileSystem;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static Switch _device;
        private static Thread _gpuThread;
        private static volatile bool _gpuRunning;

        [StructLayout(LayoutKind.Sequential)]
        public struct XbcStats
        {
            public long PrivateBytes;
            public long WorkingSet;
            public long PeakWorkingSet;
            public long ManagedHeap;
            public long UptimeMs;
            public int Running;
            public int GuestMarkers;
            public long FramesProcessed;
            public long FramesPresented;
        }

        // GPU work the guest submitted and frames it presented: a game that got as far as drawing.
        private static long _framesProcessed;
        private static long _framesPresented;

        [UnmanagedCallersOnly(EntryPoint = "xbc_init", CallConvs = [typeof(CallConvCdecl)])]
        public static int Init(byte* baseDirUtf8)
        {
            try
            {
                _uptime.Restart();

                string baseDir = Marshal.PtrToStringUTF8((nint)baseDirUtf8);
                Directory.CreateDirectory(baseDir);
                AppDataManager.Initialize(baseDir);

                // XBC_TRACE=1 logs every IPC call and stub: enough to see where a guest is stuck.
                bool trace = Environment.GetEnvironmentVariable("XBC_TRACE") == "1";
                Logger.SetEnable(LogLevel.Debug, trace);
                Logger.SetEnable(LogLevel.Stub, trace);
                Logger.SetEnable(LogLevel.Info, true);
                Logger.SetEnable(LogLevel.Warning, true);
                Logger.SetEnable(LogLevel.Error, true);
                Logger.SetEnable(LogLevel.Guest, true);
                Logger.SetEnable(LogLevel.Trace, trace);
                Logger.AddTarget(_logRing);

                FileStream logFile = FileLogTarget.PrepareLogFile(Path.Combine(AppDataManager.BaseDirPath, "Logs"));
                if (logFile != null)
                {
                    Logger.AddTarget(new AsyncLogTargetWrapper(new FileLogTarget("file", logFile), 1000, AsyncLogTargetOverflowAction.Block));
                }

                _virtualFileSystem = VirtualFileSystem.CreateInstance();
                _libHacHorizonManager = new LibHacHorizonManager();
                _libHacHorizonManager.InitializeFsServer(_virtualFileSystem);
                _libHacHorizonManager.InitializeArpServer();
                _libHacHorizonManager.InitializeBcatServer();
                _libHacHorizonManager.InitializeSystemClients();

                _contentManager = new ContentManager(_virtualFileSystem);
                // Horizon services (e.g. ngc) mount system data while the Switch is constructed, before the
                // loader would populate the content storages; the desktop UI loads them early for the same reason.
                _contentManager.LoadEntries();
                _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient);
                _userChannelPersistence = new UserChannelPersistence();

                Logger.Notice.Print(LogClass.Application, $"xbc: core initialized, base dir {AppDataManager.BaseDirPath}");

                return 0;
            }
            catch (Exception ex)
            {
                Report("xbc_init", ex);
                return -1;
            }
        }

        /// <summary>Installs the system firmware from a dump (.zip or folder) of the user's own console.</summary>
        /// <returns>0, or -1 when it failed (the log says why).</returns>
        [UnmanagedCallersOnly(EntryPoint = "xbc_install_firmware", CallConvs = [typeof(CallConvCdecl)])]
        public static int InstallFirmware(byte* pathUtf8)
        {
            try
            {
                string path = Marshal.PtrToStringUTF8((nint)pathUtf8);
                _contentManager.InstallFirmware(path);
                _contentManager.LoadEntries();
                Logger.Notice.Print(LogClass.Application, $"xbc: firmware {_contentManager.GetCurrentFirmwareVersion()?.VersionString ?? "?"} installed from {path}");
                return 0;
            }
            catch (Exception ex)
            {
                Report("xbc_install_firmware", ex);
                return -1;
            }
        }

        /// <param name="memoryManagerMode">0 = software page table, 1 = host mapped, 2 = host mapped unsafe.</param>
        [UnmanagedCallersOnly(EntryPoint = "xbc_load", CallConvs = [typeof(CallConvCdecl)])]
        public static int Load(byte* pathUtf8, int memoryManagerMode)
        {
            try
            {
                string path = Marshal.PtrToStringUTF8((nint)pathUtf8);

                HleConfiguration configuration = new HleConfiguration(
                        MemoryConfiguration.MemoryConfiguration4GiB,
                        SystemLanguage.AmericanEnglish,
                        RegionCode.USA,
                        VSyncMode.Switch,
                        enableDockedMode: true,
                        enablePtc: false,
                        ITickSource.RealityTickScalar,
                        enableInternetAccess: false,
                        IntegrityCheckLevel.None,
                        fsGlobalAccessLogMode: 0,
                        systemTimeOffset: 0,
                        timeZone: "UTC",
                        (MemoryManagerMode)memoryManagerMode,
                        ignoreMissingServices: true,
                        AspectRatio.Fixed16x9,
                        audioVolume: 0f,
                        useHypervisor: false,
                        multiplayerLanInterfaceId: "0",
                        MultiplayerMode.Disabled,
                        multiplayerDisableP2p: false,
                        multiplayerLdnPassphrase: string.Empty,
                        multiplayerLdnServer: string.Empty,
                        enableGdbStub: false,
                        gdbStubPort: 0,
                        debuggerSuspendOnStart: false,
                        customVSyncInterval: 60)
                    .Configure(
                        _virtualFileSystem,
                        _libHacHorizonManager,
                        _contentManager,
                        _accountManager,
                        _userChannelPersistence,
                        new NullRenderer(),
                        new DummyHardwareDeviceDriver(),
                        new NullHostUiHandler());

                _device = new Switch(configuration);

                Logger.Notice.Print(LogClass.Application, $"xbc: loading {path} (memory manager {(MemoryManagerMode)memoryManagerMode})");

                // Games by their container; anything else (.nro/.nso, the probe) as an executable.
                // Application id 0: the first application in the file.
                bool loaded = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".nsp" => _device.Processes.LoadNsp(path, 0),
                    ".xci" => _device.Processes.LoadXci(path, 0),
                    ".nca" => _device.Processes.LoadNca(path),
                    _ => _device.LoadProgram(path),
                };

                if (!loaded)
                {
                    Logger.Error?.Print(LogClass.Application, "xbc: LoadProgram failed");
                    return -2;
                }

                // Guest CPU threads block in WaitUntilGpuReady() until a GPU thread initializes the shader cache,
                // exactly like the desktop render loop does.
                _gpuRunning = true;
                _gpuThread = new Thread(GpuLoop) { Name = "XBC.GpuThread", IsBackground = true };
                _gpuThread.Start();

                return 0;
            }
            catch (Exception ex)
            {
                Report("xbc_load", ex);
                return -1;
            }
        }

        /// <summary>Headless stand-in for the desktop render loop: no presentation, but the GPU keeps processing commands.</summary>
        private static void GpuLoop()
        {
            try
            {
                _device.Gpu.Renderer.Initialize(GraphicsDebugLevel.None);

                _device.Gpu.Renderer.RunLoop(() =>
                {
                    _device.Gpu.SetGpuThread();
                    _device.Gpu.InitializeShaderCache(CancellationToken.None);

                    Logger.Notice.Print(LogClass.Application, "xbc: GPU ready");

                    while (_gpuRunning)
                    {
                        if (_device.WaitFifo())
                        {
                            _device.ProcessFrame();
                            Interlocked.Increment(ref _framesProcessed);
                        }

                        while (_device.ConsumeFrameAvailable())
                        {
                            _device.PresentFrame(static () => { });
                            Interlocked.Increment(ref _framesPresented);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Report("gpu thread", ex);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "xbc_get_stats", CallConvs = [typeof(CallConvCdecl)])]
        public static void GetStats(XbcStats* stats)
        {
            *stats = default;
            stats->UptimeMs = _uptime.ElapsedMilliseconds;
            stats->ManagedHeap = GC.GetTotalMemory(false);
            stats->Running = _device != null ? 1 : 0;
            stats->GuestMarkers = _logRing.GuestMarkers;
            stats->FramesProcessed = Interlocked.Read(ref _framesProcessed);
            stats->FramesPresented = Interlocked.Read(ref _framesPresented);

            try
            {
                using Process process = Process.GetCurrentProcess();
                stats->PrivateBytes = process.PrivateMemorySize64;
                stats->WorkingSet = process.WorkingSet64;
                stats->PeakWorkingSet = process.PeakWorkingSet64;
            }
            catch (Exception)
            {
                // Process APIs may be unavailable inside the UWP sandbox; the shell reports app memory itself.
            }
        }

        /// <summary>Copies the newest log lines (UTF-8, newline separated) into <paramref name="buffer"/>.</summary>
        /// <returns>Bytes written.</returns>
        [UnmanagedCallersOnly(EntryPoint = "xbc_read_log", CallConvs = [typeof(CallConvCdecl)])]
        public static int ReadLog(byte* buffer, int capacity)
        {
            byte[] text = Encoding.UTF8.GetBytes(_logRing.Snapshot());
            int start = Math.Max(0, text.Length - capacity);
            int length = text.Length - start;
            new ReadOnlySpan<byte>(text, start, length).CopyTo(new Span<byte>(buffer, capacity));
            return length;
        }

        /// <summary>Logs every process and guest thread (state, PC, run time) to see where emulation is stuck.</summary>
        [UnmanagedCallersOnly(EntryPoint = "xbc_debug_dump", CallConvs = [typeof(CallConvCdecl)])]
        public static void DebugDump()
        {
            try
            {
                if (_device == null)
                {
                    _logRing.Add("xbc: debug dump: no device");
                    return;
                }

                foreach (string line in _device.DescribeKernelState())
                {
                    _logRing.Add($"xbc: {line}");
                }
            }
            catch (Exception ex)
            {
                Report("xbc_debug_dump", ex);
            }
        }

        [UnmanagedCallersOnly(EntryPoint = "xbc_shutdown", CallConvs = [typeof(CallConvCdecl)])]
        public static void Shutdown()
        {
            try
            {
                _gpuRunning = false;
                _gpuThread?.Join(TimeSpan.FromSeconds(5));
                _device?.Dispose();
                _device = null;
                Logger.Shutdown();
            }
            catch (Exception ex)
            {
                Report("xbc_shutdown", ex);
            }
        }

        private static void Report(string where, Exception ex)
        {
            string message = $"xbc: {where} failed: {ex}";
            _logRing.Add(message);
            Logger.Error?.Print(LogClass.Application, message);
        }
    }
}
