﻿using Microsoft.Win32;
using NTMiner.AppSetting;
using NTMiner.Bus;
using NTMiner.Core;
using NTMiner.Core.Gpus;
using NTMiner.Core.Gpus.Impl;
using NTMiner.Core.Impl;
using NTMiner.Core.Kernels;
using NTMiner.Core.Kernels.Impl;
using NTMiner.Core.MinerServer;
using NTMiner.Core.MinerServer.Impl;
using NTMiner.Core.Profiles;
using NTMiner.Core.Profiles.Impl;
using NTMiner.KernelOutputKeyword;
using NTMiner.Profile;
using NTMiner.ServerMessage;
using NTMiner.User;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NTMiner {
    public partial class NTMinerRoot : INTMinerRoot {
        #region ServerContext Class
        public class ServerContextImpl : IServerContext {
            private readonly List<IMessagePathId> _serverContextHandlers = new List<IMessagePathId>();

            public ServerContextImpl() {
                ReInit();
            }

            public void ReInit() {
                foreach (var handler in _serverContextHandlers) {
                    VirtualRoot.DeletePath(handler);
                }
                _serverContextHandlers.Clear();
                this.CoinGroupSet = new CoinGroupSet(this);
                this.CoinSet = new CoinSet(this);
                this.FileWriterSet = new FileWriterSet(this);
                this.FragmentWriterSet = new FragmentWriterSet(this);
                this.GroupSet = new GroupSet(this);
                this.PoolSet = new PoolSet(this);
                this.SysDicItemSet = new SysDicItemSet(this);
                this.SysDicSet = new SysDicSet(this);
                this.CoinKernelSet = new CoinKernelSet(this);
                this.KernelInputSet = new KernelInputSet(this);
                this.KernelOutputSet = new KernelOutputSet(this);
                this.KernelOutputTranslaterSet = new KernelOutputTranslaterSet(this);
                this.KernelSet = new KernelSet(this);
                this.PackageSet = new PackageSet(this);
                this.PoolKernelSet = new PoolKernelSet(this);
            }

            /// <summary>
            /// 命令窗口。使用该方法的代码行应将前两个参数放在第一行以方便vs查找引用时展示出参数信息
            /// </summary>
            public void BuildCmdPath<TCmd>(string description, LogEnum logType, Action<TCmd> action)
                where TCmd : ICmd {
                var messagePathId = VirtualRoot.BuildPath(description, logType, action);
                _serverContextHandlers.Add(messagePathId);
            }

            /// <summary>
            /// 事件响应
            /// </summary>
            public void BuildEventPath<TEvent>(string description, LogEnum logType, Action<TEvent> action)
                where TEvent : IEvent {
                var messagePathId = VirtualRoot.BuildPath(description, logType, action);
                _serverContextHandlers.Add(messagePathId);
            }

            public ICoinGroupSet CoinGroupSet { get; private set; }

            public ICoinSet CoinSet { get; private set; }

            public IFileWriterSet FileWriterSet { get; private set; }

            public IFragmentWriterSet FragmentWriterSet { get; private set; }

            public IGroupSet GroupSet { get; private set; }

            public IPoolSet PoolSet { get; private set; }

            public ISysDicItemSet SysDicItemSet { get; private set; }

            public ISysDicSet SysDicSet { get; private set; }

            public ICoinKernelSet CoinKernelSet { get; private set; }

            public IKernelInputSet KernelInputSet { get; private set; }

            public IKernelOutputSet KernelOutputSet { get; private set; }

            public IKernelOutputTranslaterSet KernelOutputTranslaterSet { get; private set; }

            public IKernelSet KernelSet { get; private set; }

            public IPackageSet PackageSet { get; private set; }

            public IPoolKernelSet PoolKernelSet { get; private set; }
        }
        #endregion

        public IUserSet UserSet { get; private set; }

        public DateTime CreatedOn { get; private set; }

        public IAppSettingSet ServerAppSettingSet { get; private set; }

        #region cotr
        private NTMinerRoot() {
            CreatedOn = DateTime.Now;
        }
        #endregion

        #region Init
        public void Init(Action callback) {
            Task.Factory.StartNew(() => {
                bool isWork = Environment.GetCommandLineArgs().Contains("--work", StringComparer.OrdinalIgnoreCase);
                if (isWork) { // 是作业
                    DoInit(isWork, callback);
                    if (VirtualRoot.IsMinerClient) {
                        NTMinerRegistry.SetIsLastIsWork(true);
                    }
                }
                else { // 不是作业
                    if (VirtualRoot.IsMinerClient) {
                        NTMinerRegistry.SetIsLastIsWork(false);
                    }
                    // 如果是Debug模式且不是群控客户端则使用本地数据库初始化
                    bool useLocalDb = DevMode.IsDebugMode && !VirtualRoot.IsMinerStudio;
                    if (useLocalDb) {
                        DoInit(isWork: false, callback: callback);
                    }
                    else {
                        Logger.InfoDebugLine("开始下载server.json");
                        GetAliyunServerJson((data) => {
                            // 如果server.json未下载成功则不覆写本地server.json
                            if (data != null && data.Length != 0) {
                                Logger.InfoDebugLine("GetAliyunServerJson下载成功");
                                var serverJson = Encoding.UTF8.GetString(data);
                                if (!string.IsNullOrEmpty(serverJson)) {
                                    SpecialPath.WriteServerJsonFile(serverJson);
                                }
                                OfficialServer.GetJsonFileVersionAsync(MainAssemblyInfo.ServerJsonFileName, serverState => {
                                    SetServerJsonVersion(serverState.JsonFileVersion);
                                    AppVersionChangedEvent.PublishIfNewVersion(serverState.MinerClientVersion);
                                    if (Math.Abs((long)Timestamp.GetTimestamp() - (long)serverState.Time) < Timestamp.DesyncSeconds) {
                                        Logger.OkDebugLine("时间同步");
                                    }
                                    else {
                                        Write.UserWarn($"本机时间和服务器时间不同步，请调整，本地：{DateTime.Now}，服务器：{Timestamp.FromTimestamp(serverState.Time)}");
                                    }
                                });
                            }
                            else {
                                if (!File.Exists(SpecialPath.ServerJsonFileFullName)) {
                                    VirtualRoot.ThisLocalError(nameof(NTMinerRoot), "配置文件下载失败，这是第一次运行开源矿工，配置文件至少需要成功下载一次，请检查网络是否可用", OutEnum.Warn);
                                }
                                else {
                                    VirtualRoot.ThisLocalWarn(nameof(NTMinerRoot), "配置文件下载失败，使用最后一次成功下载的配置文件", OutEnum.Warn);
                                }
                            }
                            DoInit(isWork, callback);
                        });
                        #region 发生了用户活动时检查serverJson是否有新版本
                        VirtualRoot.BuildEventPath<UserActionEvent>("发生了用户活动时检查serverJson是否有新版本", LogEnum.DevConsole,
                            action: message => {
                                RefreshServerJsonFile();
                            });
                        #endregion
                    }
                }
                VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), $"启动{VirtualRoot.AppName}");
            });
        }

        // MinerProfile对应local.litedb或local.json
        // 群控客户端管理作业时调用该方法切换MinerProfile上下文
        public void ReInitMinerProfile() {
            ReInitLocalJson();
            this._minerProfile.ReInit(this);
            // 本地数据集已刷新，此时刷新本地数据集的视图模型集
            VirtualRoot.RaiseEvent(new LocalContextReInitedEvent());
            // 本地数据集的视图模型已刷新，此时刷新本地数据集的视图界面
            VirtualRoot.RaiseEvent(new LocalContextVmsReInitedEvent());
            RefreshArgsAssembly();
        }

        private void RefreshServerJsonFile() {
            OfficialServer.GetJsonFileVersionAsync(MainAssemblyInfo.ServerJsonFileName, serverState => {
                AppVersionChangedEvent.PublishIfNewVersion(serverState.MinerClientVersion);
                string localServerJsonFileVersion = GetServerJsonVersion();
                if (!string.IsNullOrEmpty(serverState.JsonFileVersion) && localServerJsonFileVersion != serverState.JsonFileVersion) {
                    GetAliyunServerJson((data) => {
                        string rawJson = Encoding.UTF8.GetString(data);
                        SpecialPath.WriteServerJsonFile(rawJson);
                        SetServerJsonVersion(serverState.JsonFileVersion);
                        ReInitServerJson();
                        // 作业模式下界面是禁用的，所以这里的初始化isWork必然是false
                        ContextReInit(isWork: VirtualRoot.IsMinerStudio);
                        VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), $"刷新server.json配置", toConsole: true);
                    });
                }
                else {
                    Write.DevDebug("server.json没有新版本");
                }
            });
        }

        #endregion

        #region private methods
        private static void GetAliyunServerJson(Action<byte[]> callback) {
            string serverJsonFileUrl = $"{OfficialServer.MinerJsonBucket}{MainAssemblyInfo.ServerJsonFileName}";
            string fileUrl = serverJsonFileUrl + "?t=" + DateTime.Now.Ticks;
            Task.Factory.StartNew(() => {
                try {
                    var webRequest = WebRequest.Create(fileUrl);
                    webRequest.Timeout = 20 * 1000;
                    webRequest.Method = "GET";
                    webRequest.Headers.Add("Accept-Encoding", "gzip, deflate, br");
                    var response = webRequest.GetResponse();
                    using (MemoryStream ms = new MemoryStream())
                    using (Stream stream = response.GetResponseStream()) {
                        byte[] buffer = new byte[1024];
                        int n = stream.Read(buffer, 0, buffer.Length);
                        while (n > 0) {
                            ms.Write(buffer, 0, n);
                            n = stream.Read(buffer, 0, buffer.Length);
                        }
                        byte[] data = new byte[ms.Length];
                        ms.Position = 0;
                        ms.Read(data, 0, data.Length);
                        data = ZipDecompress(data);
                        callback?.Invoke(data);
                    }
                    Logger.InfoDebugLine($"下载完成：{fileUrl}");
                }
                catch (Exception e) {
                    Logger.ErrorDebugLine(e);
                    callback?.Invoke(new byte[0]);
                }
            });
        }

        private static byte[] ZipDecompress(byte[] zippedData) {
            MemoryStream ms = new MemoryStream(zippedData);
            GZipStream compressedzipStream = new GZipStream(ms, CompressionMode.Decompress);
            using (MemoryStream outBuffer = new MemoryStream()) {
                byte[] block = new byte[1024];
                while (true) {
                    int bytesRead = compressedzipStream.Read(block, 0, block.Length);
                    if (bytesRead <= 0) {
                        break;
                    }
                    else {
                        outBuffer.Write(block, 0, bytesRead);
                    }
                }
                compressedzipStream.Close();
                return outBuffer.ToArray();
            }
        }

        public string GetServerJsonVersion() {
            string serverJsonVersion = string.Empty;
            if (VirtualRoot.LocalAppSettingSet.TryGetAppSetting(NTKeyword.ServerJsonVersionAppSettingKey, out IAppSetting setting) && setting.Value != null) {
                serverJsonVersion = setting.Value.ToString();
            }
            return serverJsonVersion;
        }

        private void SetServerJsonVersion(string serverJsonVersion) {
            AppSettingData appSettingData = new AppSettingData() {
                Key = NTKeyword.ServerJsonVersionAppSettingKey,
                Value = serverJsonVersion
            };
            string oldVersion = GetServerJsonVersion();
            VirtualRoot.Execute(new SetLocalAppSettingCommand(appSettingData));
            VirtualRoot.RaiseEvent(new ServerJsonVersionChangedEvent(oldVersion, serverJsonVersion));
        }

        private MinerProfile _minerProfile;
        private void DoInit(bool isWork, Action callback) {
            IsJsonServer = !DevMode.IsDebugMode || VirtualRoot.IsMinerStudio || isWork;
            this.ServerAppSettingSet = new ServerAppSettingSet();
            this.CalcConfigSet = new CalcConfigSet(this);
            this.ServerContext = new ServerContextImpl();
            this.GpuProfileSet = new GpuProfileSet(this);
            this.UserSet = new UserSet();
            this.KernelProfileSet = new KernelProfileSet(this);
            this.GpusSpeed = new GpusSpeed(this);
            this.CoinShareSet = new CoinShareSet(this);
            this.MineWorkSet = new MineWorkSet();
            this.MinerGroupSet = new MinerGroupSet();
            this.NTMinerWalletSet = new NTMinerWalletSet();
            this.OverClockDataSet = new OverClockDataSet(this);
            this.ColumnsShowSet = new ColumnsShowSet(this);
            this.ServerMessageSet = new ServerMessageSet(VirtualRoot.LocalDbFileFullName, isServer: false);
            // 作业和在群控客户端管理作业时
            IsJsonLocal = isWork || VirtualRoot.IsMinerStudio;
            this._minerProfile = new MinerProfile(this);

            // 这几个注册表内部区分挖矿端和群控客户端
            NTMinerRegistry.SetLocation(VirtualRoot.AppFileFullName);
            NTMinerRegistry.SetArguments(string.Join(" ", CommandLineArgs.Args));
            NTMinerRegistry.SetCurrentVersion(MainAssemblyInfo.CurrentVersion.ToString());
            NTMinerRegistry.SetCurrentVersionTag(MainAssemblyInfo.CurrentVersionTag);

            if (VirtualRoot.IsMinerClient) {
                Report.Init();
                Link();
                // 当显卡温度变更时守卫温度防线
                TempGruarder.Instance.Init(this);
                // 因为这里耗时500毫秒左右
                Task.Factory.StartNew(() => {
                    Windows.Error.DisableWindowsErrorUI();
                    Windows.UAC.DisableUAC();
                    Windows.WAU.DisableWAUAsync();
                    Windows.Defender.DisableAntiSpyware();
                    Windows.Power.PowerCfgOff();
                    Windows.BcdEdit.IgnoreAllFailures();
                });
            }

            callback?.Invoke();
        }

        private void ContextReInit(bool isWork) {
            if (isWork) {
                ReInitServerJson();
            }
            IsJsonServer = !DevMode.IsDebugMode || VirtualRoot.IsMinerStudio || isWork;
            this.ServerContext.ReInit();
            // CoreContext的视图模型集此时刷新
            VirtualRoot.RaiseEvent(new ServerContextReInitedEvent());
            // CoreContext的视图模型集已全部刷新，此时刷新视图界面
            VirtualRoot.RaiseEvent(new ServerContextVmsReInitedEvent());
            if (isWork) {
                // 有可能是由非作业切换为作业，所以需要对IsJsonLocal赋值
                IsJsonLocal = true;
                ReInitMinerProfile();
            }
        }

        private void Link() {
            VirtualRoot.BuildCmdPath<RegCmdHereCommand>(action: message => {
                try {
                    RegCmdHere(); VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), "windows右键命令行添加成功", OutEnum.Success);
                }
                catch (Exception e) {
                    Logger.ErrorDebugLine(e);
                    RegCmdHere(); VirtualRoot.ThisLocalError(nameof(NTMinerRoot), "windows右键命令行添加失败", OutEnum.Warn);
                }
            });
            VirtualRoot.BuildEventPath<Per1MinuteEvent>("每1分钟阻止系统休眠", LogEnum.None,
                action: message => {
                    Windows.Power.PreventSleep();
                });
            #region 挖矿开始时将无份额内核重启份额计数置0
            int shareCount = 0;
            DateTime shareOn = DateTime.Now;
            VirtualRoot.BuildEventPath<MineStartedEvent>("挖矿开始后将无份额内核重启份额计数置0", LogEnum.DevConsole,
                action: message => {
                    // 将无份额内核重启份额计数置0
                    shareCount = 0;
                    if (!message.MineContext.IsRestart) {
                        shareOn = DateTime.Now;
                    }
                });
            #endregion
            #region 每20秒钟检查是否需要重启
            VirtualRoot.BuildEventPath<Per20SecondEvent>("每20秒钟检查是否需要重启", LogEnum.None,
                action: message => {
                    #region 重启电脑
                    try {
                        if (MinerProfile.IsPeriodicRestartComputer) {
                            if ((DateTime.Now - this.CreatedOn).TotalMinutes > 60 * MinerProfile.PeriodicRestartComputerHours + MinerProfile.PeriodicRestartComputerMinutes) {
                                Logger.WarnWriteLine($"每运行{MinerProfile.PeriodicRestartKernelHours}小时{MinerProfile.PeriodicRestartComputerMinutes}分钟重启电脑");
                                Windows.Power.Restart(60);
                                VirtualRoot.Execute(new CloseNTMinerCommand());
                                return;// 退出
                            }
                        }
                    }
                    catch (Exception e) {
                        Logger.ErrorDebugLine(e);
                    }
                    #endregion

                    #region 周期重启内核
                    try {
                        if (IsMining && MinerProfile.IsPeriodicRestartKernel) {
                            if ((DateTime.Now - CurrentMineContext.CreatedOn).TotalMinutes > 60 * MinerProfile.PeriodicRestartKernelHours + MinerProfile.PeriodicRestartKernelMinutes) {
                                Logger.WarnWriteLine($"每运行{MinerProfile.PeriodicRestartKernelHours}小时{MinerProfile.PeriodicRestartKernelMinutes}分钟重启内核");
                                RestartMine();
                                return;// 退出
                            }
                        }
                    }
                    catch (Exception e) {
                        Logger.ErrorDebugLine(e);
                    }
                    #endregion

                    #region 无份额重启内核
                    try {
                        if (IsMining && this.CurrentMineContext.MainCoin != null) {
                            int totalShare = 0;
                            bool restartComputer = MinerProfile.IsNoShareRestartComputer && (DateTime.Now - shareOn).TotalMinutes > MinerProfile.NoShareRestartComputerMinutes;
                            bool restartKernel = MinerProfile.IsNoShareRestartKernel && (DateTime.Now - shareOn).TotalMinutes > MinerProfile.NoShareRestartKernelMinutes;
                            if (restartComputer || restartKernel) {
                                ICoinShare mainCoinShare = this.CoinShareSet.GetOrCreate(this.CurrentMineContext.MainCoin.GetId());
                                totalShare = mainCoinShare.TotalShareCount;
                                if ((this.CurrentMineContext is IDualMineContext dualMineContext) && dualMineContext.DualCoin != null) {
                                    ICoinShare dualCoinShare = this.CoinShareSet.GetOrCreate(dualMineContext.DualCoin.GetId());
                                    totalShare += dualCoinShare.TotalShareCount;
                                }
                                // 如果份额没有增加
                                if (shareCount == totalShare) {
                                    if (restartComputer) {
                                        if (!MinerProfile.IsAutoBoot || !MinerProfile.IsAutoStart) {
                                            VirtualRoot.Execute(new SetAutoStartCommand(true, true));
                                        }
                                        Logger.WarnWriteLine($"{MinerProfile.NoShareRestartComputerMinutes}分钟无份额重启电脑");
                                        Windows.Power.Restart(60);
                                        VirtualRoot.Execute(new CloseNTMinerCommand());
                                        return;// 退出
                                    }
                                    // 产生过份额或者已经两倍重启内核时间了
                                    if (restartKernel && (totalShare > 0 || (DateTime.Now - shareOn).TotalMinutes > 2 * MinerProfile.NoShareRestartKernelMinutes)) {
                                        Logger.WarnWriteLine($"{MinerProfile.NoShareRestartKernelMinutes}分钟无份额重启内核");
                                        RestartMine();
                                        return;// 退出
                                    }
                                }
                                if (totalShare > shareCount) {
                                    shareCount = totalShare;
                                    shareOn = DateTime.Now;
                                }
                            }
                        }
                    }
                    catch (Exception e) {
                        Logger.ErrorDebugLine(e);
                    }
                    #endregion
                });
            #endregion
            VirtualRoot.BuildEventPath<Per10SecondEvent>("周期刷新显卡状态", LogEnum.None,
                action: message => {
                    // 因为遇到显卡系统状态变更时可能费时
                    Task.Factory.StartNew(() => {
                        GpuSet.LoadGpuState();
                    });
                });
        }

        // 在Windows右键上下文菜单中添加“命令行”菜单
        private static void RegCmdHere() {
            string cmdHere = "SOFTWARE\\Classes\\Directory\\background\\shell\\cmd_here";
            string cmdHereCommand = cmdHere + "\\command";
            string cmdPrompt = "SOFTWARE\\Classes\\Folder\\shell\\cmdPrompt";
            string cmdPromptCommand = cmdPrompt + "\\command";
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHere, "", "命令行");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHere, "Icon", "cmd.exe");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHereCommand, "", "\"cmd.exe\"");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdPrompt, "", "命令行");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdPromptCommand, "", "\"cmd.exe\" \"cd %1\"");
            cmdHere = "SOFTWARE\\Classes\\Directory\\shell\\cmd_here";
            cmdHereCommand = cmdHere + "\\command";
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHere, "", "命令行");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHere, "Icon", "cmd.exe");
            Windows.WinRegistry.SetValue(Registry.LocalMachine, cmdHereCommand, "", "\"cmd.exe\"");
        }
        #endregion

        #region Exit
        public void Exit() {
            if (_currentMineContext != null) {
                StopMine(StopMineReason.ApplicationExit);
            }
        }
        #endregion

        public StopMineReason StopReason { get; private set; }
        #region StopMine
        public void StopMineAsync(StopMineReason stopReason, Action callback = null) {
            if (!IsMining) {
                callback?.Invoke();
                return;
            }
            Task.Factory.StartNew(() => {
                StopMine(stopReason);
                callback?.Invoke();
            });
        }
        private void StopMine(StopMineReason stopReason) {
            this.StopReason = stopReason;
            if (!IsMining) {
                return;
            }
            try {
                if (_currentMineContext != null && _currentMineContext.Kernel != null) {
                    string processName = _currentMineContext.Kernel.GetProcessName();
                    Task.Factory.StartNew(() => {
                        Windows.TaskKill.Kill(processName, waitForExit: true);
                    });
                }
                var mineContext = _currentMineContext;
                _currentMineContext = null;
                VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), "挖矿停止", toConsole: true);
                VirtualRoot.RaiseEvent(new MineStopedEvent(mineContext, stopReason));
            }
            catch (Exception e) {
                Logger.ErrorDebugLine(e);
            }
        }
        #endregion

        #region RestartMine
        public void RestartMine(bool isWork = false) {
            if (!IsMining) {
                if (isWork) {
                    ContextReInit(true);
                }
                StartMine(isRestart: true);
            }
            else {
                this.StopMineAsync(StopMineReason.RestartMine, () => {
                    if (isWork) {
                        ContextReInit(true);
                    }
                    StartMine(isRestart: true);
                });
            }
            NTMinerRegistry.SetIsLastIsWork(isWork);
        }
        #endregion

        #region StartMine
        public void StartMine(bool isRestart = false) {
            try {
                IWorkProfile minerProfile = this.MinerProfile;
                if (!ServerContext.CoinSet.TryGetCoin(minerProfile.CoinId, out ICoin mainCoin)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有选择主挖币种。"));
                    return;
                }
                ICoinProfile coinProfile = minerProfile.GetCoinProfile(minerProfile.CoinId);
                if (!ServerContext.PoolSet.TryGetPool(coinProfile.PoolId, out IPool mainCoinPool)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有选择主币矿池。"));
                    return;
                }
                if (!ServerContext.CoinKernelSet.TryGetCoinKernel(coinProfile.CoinKernelId, out ICoinKernel coinKernel)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有选择挖矿内核。"));
                    return;
                }
                if (!ServerContext.KernelSet.TryGetKernel(coinKernel.KernelId, out IKernel kernel)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("无效的挖矿内核。"));
                    return;
                }
                if (!kernel.IsSupported(mainCoin)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent($"该内核不支持{GpuSet.GpuType.GetDescription()}卡。"));
                    return;
                }
                if (!ServerContext.KernelInputSet.TryGetKernelInput(kernel.KernelInputId, out IKernelInput kernelInput)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("未设置内核输入。"));
                    return;
                }
                if (!ServerContext.KernelOutputSet.TryGetKernelOutput(kernel.KernelOutputId, out IKernelOutput kernelOutput)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("未设置内核输出。"));
                    return;
                }
                if (string.IsNullOrEmpty(coinProfile.Wallet)) {
                    MinerProfile.SetCoinProfileProperty(mainCoin.GetId(), nameof(coinProfile.Wallet), mainCoin.TestWallet);
                }
                if (mainCoinPool.IsUserMode) {
                    IPoolProfile poolProfile = minerProfile.GetPoolProfile(mainCoinPool.GetId());
                    string userName = poolProfile.UserName;
                    if (string.IsNullOrEmpty(userName)) {
                        VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有填写矿池用户名。"));
                        return;
                    }
                }
                if (string.IsNullOrEmpty(coinProfile.Wallet) && !mainCoinPool.IsUserMode) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有填写钱包地址。"));
                    return;
                }
                ICoinKernelProfile coinKernelProfile = minerProfile.GetCoinKernelProfile(coinKernel.GetId());
                ICoin dualCoin = null;
                IPool dualCoinPool = null;
                if (coinKernelProfile.IsDualCoinEnabled) {
                    if (!ServerContext.CoinSet.TryGetCoin(coinKernelProfile.DualCoinId, out dualCoin)) {
                        VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有选择双挖币种。"));
                        return;
                    }
                    coinProfile = minerProfile.GetCoinProfile(coinKernelProfile.DualCoinId);
                    if (!ServerContext.PoolSet.TryGetPool(coinProfile.DualCoinPoolId, out dualCoinPool)) {
                        VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有选择双挖矿池。"));
                        return;
                    }
                    if (string.IsNullOrEmpty(coinProfile.DualCoinWallet)) {
                        MinerProfile.SetCoinProfileProperty(dualCoin.GetId(), nameof(coinProfile.DualCoinWallet), dualCoin.TestWallet);
                    }
                    if (string.IsNullOrEmpty(coinProfile.DualCoinWallet)) {
                        VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有填写双挖钱包。"));
                        return;
                    }
                }
                if (string.IsNullOrEmpty(kernel.Package)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent(kernel.GetFullName() + "没有内核包"));
                    return;
                }
                if (string.IsNullOrEmpty(kernelInput.Args)) {
                    VirtualRoot.RaiseEvent(new StartingMineFailedEvent("没有配置运行参数。"));
                    return;
                }
                if (IsMining) {
                    this.StopMine(StopMineReason.InStartMine);
                }
                string packageZipFileFullName = Path.Combine(SpecialPath.PackagesDirFullName, kernel.Package);
                if (!File.Exists(packageZipFileFullName)) {
                    VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), kernel.GetFullName() + "本地内核包不存在，开始自动下载", toConsole: true);
                    VirtualRoot.Execute(new ShowKernelDownloaderCommand(kernel.GetId(), downloadComplete: (isSuccess, message) => {
                        if (isSuccess) {
                            StartMine(isRestart);
                        }
                        else {
                            VirtualRoot.RaiseEvent(new StartingMineFailedEvent("内核下载：" + message));
                        }
                    }));
                }
                else {
                    string commandLine = BuildAssembleArgs(out Dictionary<string, string> parameters, out Dictionary<Guid, string> fileWriters, out Dictionary<Guid, string> fragments);
                    if (commandLine != UserKernelCommandLine) {
                        Logger.WarnDebugLine("意外：MineContext.CommandLine和UserKernelCommandLine不等了");
                        Logger.WarnDebugLine("UserKernelCommandLine  :" + UserKernelCommandLine);
                        Logger.WarnDebugLine("MineContext.CommandLine:" + commandLine);
                    }
                    IMineContext mineContext = new MineContext(
                        isRestart,
                        this.MinerProfile.MinerName, mainCoin,
                        mainCoinPool, kernel, kernelInput, kernelOutput, coinKernel,
                        coinProfile.Wallet, commandLine,
                        parameters, fragments, fileWriters, GpuSet.GetUseDevices());
                    if (coinKernelProfile.IsDualCoinEnabled && coinKernel.GetIsSupportDualMine()) {
                        mineContext = new DualMineContext(
                            mineContext, dualCoin, dualCoinPool,
                            coinProfile.DualCoinWallet,
                            coinKernelProfile.DualCoinWeight,
                            parameters, fragments, fileWriters, GpuSet.GetUseDevices());
                    }
                    _currentMineContext = mineContext;
                    MinerProcess.CreateProcessAsync(mineContext);
                    VirtualRoot.ThisLocalInfo(nameof(NTMinerRoot), "开始挖矿", toConsole: true);
                    if (mineContext.UseDevices.Length != GpuSet.Count) {
                        VirtualRoot.ThisLocalWarn(nameof(NTMinerRoot), "未启用全部显卡挖矿", toConsole: true);
                    }
                }
            }
            catch (Exception e) {
                Logger.ErrorDebugLine(e);
            }
        }
        #endregion

        private IMineContext _currentMineContext;
        public IMineContext CurrentMineContext {
            get {
                return _currentMineContext;
            }
        }

        public bool IsMining {
            get {
                return CurrentMineContext != null;
            }
        }

        public IServerContext ServerContext { get; private set; }

        public IGpuProfileSet GpuProfileSet { get; private set; }

        public IWorkProfile MinerProfile {
            get { return _minerProfile; }
        }

        public IMineWorkSet MineWorkSet { get; private set; }

        public IMinerGroupSet MinerGroupSet { get; private set; }

        public INTMinerWalletSet NTMinerWalletSet { get; private set; }

        public IColumnsShowSet ColumnsShowSet { get; private set; }

        public IOverClockDataSet OverClockDataSet { get; private set; }

        public ICalcConfigSet CalcConfigSet { get; private set; }

        #region GpuSetInfo
        private string _gpuSetInfo = null;
        public string GpuSetInfo {
            get {
                if (_gpuSetInfo == null) {
                    StringBuilder sb = new StringBuilder();
                    int len = sb.Length;
                    foreach (var g in GpuSet.Where(a => a.Index != GpuAllId).GroupBy(a => a.Name)) {
                        if (sb.Length != len) {
                            sb.Append("/");
                        }
                        int gCount = g.Count();
                        if (gCount > 1) {
                            sb.Append(g.Key).Append(" x ").Append(gCount);
                        }
                        else {
                            sb.Append(g.Key);
                        }
                    }
                    _gpuSetInfo = sb.ToString();
                    if (_gpuSetInfo.Length == 0) {
                        _gpuSetInfo = "无";
                    }
                }
                return _gpuSetInfo;
            }
        }
        #endregion

        #region GpuSet
        private static bool IsNCard {
            get {
                try {
                    using (var mos = new ManagementObjectSearcher("SELECT Caption FROM Win32_VideoController")) {
                        foreach (ManagementBaseObject item in mos.Get()) {
                            foreach (var property in item.Properties) {
                                if ((property.Value ?? string.Empty).ToString().Contains("NVIDIA")) {
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch (Exception e) {
                    Logger.ErrorDebugLine(e);
                }
                return false;
            }
        }

        private IGpuSet _gpuSet;
        private readonly object _gpuSetLocker = new object();
        public IGpuSet GpuSet {
            get {
                if (_gpuSet == null) {
                    lock (_gpuSetLocker) {
                        if (_gpuSet == null) {
                            if (VirtualRoot.IsMinerStudio) {
                                _gpuSet = EmptyGpuSet.Instance;
                            }
                            else {
                                try {
                                    if (IsNCard) {
                                        _gpuSet = new NVIDIAGpuSet(this);
                                    }
                                    else {
                                        _gpuSet = new AMDGpuSet(this);
                                    }
                                }
                                catch (Exception ex) {
                                    _gpuSet = EmptyGpuSet.Instance;
                                    Logger.ErrorDebugLine(ex);
                                }
                            }
                            if (_gpuSet == null || (_gpuSet != EmptyGpuSet.Instance && _gpuSet.Count == 0)) {
                                _gpuSet = EmptyGpuSet.Instance;
                            }
                        }
                    }
                }
                return _gpuSet;
            }
        }
        #endregion

        public IKernelProfileSet KernelProfileSet { get; private set; }

        public IGpusSpeed GpusSpeed { get; private set; }

        public ICoinShareSet CoinShareSet { get; private set; }

        private IKernelOutputKeywordSet _localKernelOutputKeywordSet;
        public IKernelOutputKeywordSet LocalKernelOutputKeywordSet {
            get {
                if (_localKernelOutputKeywordSet == null) {
                    _localKernelOutputKeywordSet = new LocalKernelOutputKeywordSet(VirtualRoot.LocalDbFileFullName);
                }
                return _localKernelOutputKeywordSet;
            }
        }

        private IKernelOutputKeywordSet _serverKernelOutputKeywordSet;
        public IKernelOutputKeywordSet ServerKernelOutputKeywordSet {
            get {
                if (_serverKernelOutputKeywordSet == null) {
                    _serverKernelOutputKeywordSet = new ServerKernelOutputKeywordSet();
                }
                return _serverKernelOutputKeywordSet;
            }
        }

        public IServerMessageSet ServerMessageSet { get; private set; }
    }
}
