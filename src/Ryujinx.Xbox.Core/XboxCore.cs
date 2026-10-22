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
        }

        [UnmanagedCallersOnly(EntryPoint = "xbc_init", CallConvs = [typeof(CallConvCdecl)])]
        public static int Init(byte* baseDirUtf8)
        {
            try
            {
                _uptime.Restart();

                string baseDir = Marshal.PtrToStringUTF8((nint)baseDirUtf8);
                Directory.CreateDirectory(baseDir);
                AppDataManager.Initialize(baseDir);

                Logger.SetEnable(LogLevel.Debug, false);
                Logger.SetEnable(LogLevel.Stub, false);
                Logger.SetEnable(LogLevel.Info, true);
                Logger.SetEnable(LogLevel.Warning, true);
                Logger.SetEnable(LogLevel.Error, true);
                Logger.SetEnable(LogLevel.Guest, true);
                Logger.SetEnable(LogLevel.Trace, false);
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

                if (!_device.LoadProgram(path))
                {
                    Logger.Error?.Print(LogClass.Application, "xbc: LoadProgram failed");
                    return -2;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Report("xbc_load", ex);
                return -1;
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

        [UnmanagedCallersOnly(EntryPoint = "xbc_shutdown", CallConvs = [typeof(CallConvCdecl)])]
        public static void Shutdown()
        {
            try
            {
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
