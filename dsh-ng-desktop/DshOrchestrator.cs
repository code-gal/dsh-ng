using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace DshNgDesktop;

internal sealed class NodeEnvironmentException : Exception
{
    public NodeEnvironmentException() : base("未检测到可用的 Node.js 或 npm 环境。请安装 Node.js LTS 后重试。")
    {
    }
}

internal sealed class DshOperationException(string message) : Exception(message);

internal sealed record UpdateCheckResult(string? InstalledVersion, string? LatestVersion, bool UpdateAvailable);

// 供安装向导窗口订阅，实时展示当前所处阶段。
internal enum DshStage
{
    Idle,
    Installing,
    Starting,
    Running,
    Failed
}

internal sealed class DshOrchestrator : IDisposable
{
    private const string _packageName = "@deepseek-ai/dsh";
    private readonly int _port;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Queue<string> _output = new();
    private Process? _dshProcess;
    private Process? _installProcess;
    private bool _disposed;
    private int _installCancellationRequested;
    private bool _installationHadExistingPackage;
    private DshStage _stage = DshStage.Idle;

    public DshOrchestrator(int port)
    {
        _port = port;
    }

    public event Action<DshStage>? StageChanged;

    public event Action<string>? LogAppended;

    public bool IsInstalled => File.Exists(PackageManifestPath);

    public bool IsRunning => _dshProcess is { HasExited: false };

    public bool IsInstalling => _installProcess is { HasExited: false };

    public bool WasInstallCanceled => Volatile.Read(ref _installCancellationRequested) != 0;

    public string? InstalledVersion => TryReadInstalledVersion();

    public DshStage Stage => _stage;

    public static async Task InitializeEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Configuration.DataDirectory);
        await AssertCommandAvailableAsync("node", cancellationToken);
        await AssertCommandAvailableAsync("npm", cancellationToken);
    }

    public async Task InstallOrUpdateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            StopCore();
            Volatile.Write(ref _installCancellationRequested, 0);
            _installationHadExistingPackage = IsInstalled;
            PrepareInstallationStaging();
            SetStage(DshStage.Installing);
            await InstallAsync(cancellationToken);
            CommitInstallationStaging();
        }
        catch
        {
            RollbackInstallationStaging();
            SetStage(WasInstallCanceled ? DshStage.Idle : DshStage.Failed);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _installProcess, null)?.Dispose();
            _operationLock.Release();
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var installedVersion = TryReadInstalledVersion();
        var latestVersion = await ReadLatestVersionAsync(cancellationToken);
        var updateAvailable = installedVersion is not null
            && IsNewerVersion(latestVersion, installedVersion);

        return new UpdateCheckResult(installedVersion, latestVersion, updateAvailable);
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInstalled)
        {
            throw new DshOperationException("尚未安装 DSH。请先从托盘菜单安装。");
        }

        await _operationLock.WaitAsync(cancellationToken);

        try
        {
            if (_dshProcess is { HasExited: false })
            {
                return;
            }

            _dshProcess?.Dispose();
            _dshProcess = null;
            SetStage(DshStage.Starting);
            StartDsh();
            await WaitForPanelAsync(cancellationToken);
            SetStage(DshStage.Running);
        }
        catch (DshOperationException)
        {
            SetStage(DshStage.Failed);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Stop() => StopCore();

    public bool CancelInstall()
    {
        var process = Volatile.Read(ref _installProcess);
        if (process is null || process.HasExited)
        {
            return false;
        }

        Volatile.Write(ref _installCancellationRequested, 1);
        AddOutput("正在停止 npm 安装进程…");
        try
        {
            process.Kill(true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private void StopCore()
    {
        var process = Interlocked.Exchange(ref _dshProcess, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CancelInstall();
        _disposed = true;
        Stop();
        _operationLock.Dispose();
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo("npm", Configuration.InstallationStagingDirectory);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--no-save");
        startInfo.ArgumentList.Add("--no-package-lock");
        startInfo.ArgumentList.Add(_packageName);
        startInfo.ArgumentList.Add("--foreground-scripts");
        startInfo.ArgumentList.Add("--loglevel");
        startInfo.ArgumentList.Add("warn");

        using var process = StartMonitoredProcess(startInfo);
        _installProcess = process;
        AddOutput("正在下载并安装 DSH 依赖。");
        using var monitor = new InstallationMonitor(process, Configuration.InstallationStagingDirectory, AddOutput);
        await process.WaitForExitAsync(cancellationToken);
        if (WasInstallCanceled)
        {
            AddOutput("安装已停止，正在回滚临时文件。");
            throw new DshOperationException("安装已取消。");
        }

        if (process.ExitCode != 0)
        {
            throw new DshOperationException($"DSH 安装失败。\n\n{GetRecentOutput()}");
        }

        if (!File.Exists(StagedPackageManifestPath))
        {
            throw new DshOperationException("DSH 安装完成，但未找到临时包文件。");
        }

        AddOutput("DSH 已下载完成，正在替换本地安装。");
    }

    private sealed class InstallationMonitor : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public InstallationMonitor(Process process, string installationDirectory, Action<string> emit)
        {
            _ = MonitorAsync(process, installationDirectory, emit, _cts.Token);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        private static async Task MonitorAsync(Process process, string installationDirectory, Action<string> emit, CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            var previousPackageCount = -1;

            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    if (process.HasExited)
                    {
                        return;
                    }

                    var packageCount = CountPackageDirectories(installationDirectory);
                    var duration = $"{elapsed.Elapsed.Minutes} 分 {elapsed.Elapsed.Seconds} 秒";
                    if (packageCount > previousPackageCount)
                    {
                        emit($"npm 正在写入依赖目录：已发现 {packageCount} 个包目录（已用时 {duration}）。");
                    }
                    else
                    {
                        emit($"npm 仍在执行下载或安装脚本（已用时 {duration}）。");
                    }

                    previousPackageCount = packageCount;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static int CountPackageDirectories(string installationDirectory)
        {
            var nodeModulesPath = Path.Combine(installationDirectory, "node_modules");
            try
            {
                return Directory.EnumerateDirectories(nodeModulesPath).Count();
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    private static void PrepareInstallationStaging()
    {
        if (Directory.Exists(Configuration.InstallationStagingDirectory))
        {
            Directory.Delete(Configuration.InstallationStagingDirectory, true);
        }

        Directory.CreateDirectory(Configuration.InstallationStagingDirectory);
    }

    private void CommitInstallationStaging()
    {
        var stagedModulesDirectory = Path.Combine(Configuration.InstallationStagingDirectory, "node_modules");
        var backupDirectory = Configuration.NodeModulesDirectory + ".rollback";

        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, true);
        }

        try
        {
            if (Directory.Exists(Configuration.NodeModulesDirectory))
            {
                Directory.Move(Configuration.NodeModulesDirectory, backupDirectory);
            }

            Directory.Move(stagedModulesDirectory, Configuration.NodeModulesDirectory);
            Directory.Delete(Configuration.InstallationStagingDirectory, true);

            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, true);
            }

            AddOutput("DSH 安装完成，正在准备启动服务。");
        }
        catch
        {
            if (Directory.Exists(Configuration.NodeModulesDirectory) && Directory.Exists(backupDirectory))
            {
                Directory.Delete(Configuration.NodeModulesDirectory, true);
            }

            if (Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, Configuration.NodeModulesDirectory);
            }

            throw;
        }
    }

    private void RollbackInstallationStaging()
    {
        try
        {
            if (Directory.Exists(Configuration.InstallationStagingDirectory))
            {
                Directory.Delete(Configuration.InstallationStagingDirectory, true);
            }

            if (!_installationHadExistingPackage && Directory.Exists(Configuration.NodeModulesDirectory))
            {
                Directory.Delete(Configuration.NodeModulesDirectory, true);
            }

            AddOutput(WasInstallCanceled
                ? "已取消安装并清理临时文件。"
                : "安装失败，已清理临时文件。");
        }
        catch (IOException)
        {
            AddOutput("临时文件清理失败，可稍后重新安装。 ");
        }
        catch (UnauthorizedAccessException)
        {
            AddOutput("临时文件清理失败，可稍后重新安装。 ");
        }
    }

    private async Task<string> ReadLatestVersionAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo("npm");
        startInfo.ArgumentList.Add("view");
        startInfo.ArgumentList.Add(_packageName);
        startInfo.ArgumentList.Add("version");
        startInfo.ArgumentList.Add("--loglevel");
        startInfo.ArgumentList.Add("error");

        using var process = Process.Start(startInfo) ?? throw new DshOperationException("无法启动 npm 以检测更新。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();

        if (!string.IsNullOrWhiteSpace(output))
        {
            AddOutput(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            AddOutput(error);
        }

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new DshOperationException($"无法检测 DSH 更新。\n\n{GetRecentOutput()}");
        }

        return output;
    }

    private static string PackageManifestPath => Path.Combine(
        Configuration.NodeModulesDirectory,
        "@deepseek-ai",
        "dsh",
        "package.json");

    private static string StagedPackageManifestPath => Path.Combine(
        Configuration.InstallationStagingDirectory,
        "node_modules",
        "@deepseek-ai",
        "dsh",
        "package.json");

    private static string? TryReadInstalledVersion()
    {
        if (!File.Exists(PackageManifestPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(PackageManifestPath);
            var manifest = JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.PackageManifest);
            return string.IsNullOrWhiteSpace(manifest?.Version) ? null : manifest.Version;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsNewerVersion(string latestVersion, string installedVersion)
    {
        if (Version.TryParse(NormalizeVersion(latestVersion), out var latest)
            && Version.TryParse(NormalizeVersion(installedVersion), out var installed))
        {
            return latest > installed;
        }

        return !string.Equals(latestVersion, installedVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string version)
    {
        var normalized = version.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var plus = normalized.IndexOf('+');
        if (plus >= 0)
        {
            normalized = normalized[..plus];
        }

        var dash = normalized.IndexOf('-');
        return dash >= 0 ? normalized[..dash] : normalized;
    }

    private void StartDsh()
    {
        var startInfo = CreateProcessStartInfo("npm");
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--no");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("dsh");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_port.ToString(CultureInfo.InvariantCulture));

        var process = StartMonitoredProcess(startInfo);
        process.EnableRaisingEvents = true;
        process.Exited += OnDshExited;
        _dshProcess = process;
    }

    private async Task WaitForPanelAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (!linkedCancellation.IsCancellationRequested)
            {
                if (_dshProcess is null || _dshProcess.HasExited)
                {
                    throw new DshOperationException($"DSH 启动失败。\n\n{GetRecentOutput()}");
                }

                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync("127.0.0.1", _port, linkedCancellation.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(250, linkedCancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new DshOperationException("DSH 启动超时。请稍后重试。");
        }

        throw new DshOperationException("DSH 启动超时。请稍后重试。");
    }

    private void OnDshExited(object? sender, EventArgs eventArgs)
    {
        var process = (Process)sender!;
        AddOutput($"DSH 已退出，退出代码：{process.ExitCode}。");
    }

    private Process StartMonitoredProcess(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo) ?? throw new DshOperationException("无法启动 DSH 进程。");
        process.OutputDataReceived += OnOutputReceived;
        process.ErrorDataReceived += OnOutputReceived;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void OnOutputReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            AddOutput(eventArgs.Data);
        }
    }

    private void AddOutput(string line)
    {
        lock (_output)
        {
            if (_output.Count == 200)
            {
                _output.Dequeue();
            }

            _output.Enqueue(line);
        }

        LogAppended?.Invoke(line);
    }

    private void SetStage(DshStage stage)
    {
        _stage = stage;
        StageChanged?.Invoke(stage);
    }

    private string GetRecentOutput()
    {
        lock (_output)
        {
            return _output.Count == 0
                ? "请检查网络连接和 Node.js 安装。"
                : string.Join(Environment.NewLine, _output.TakeLast(8));
        }
    }

    private static async Task AssertCommandAvailableAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommandPath(command),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(startInfo) ?? throw new NodeEnvironmentException();
            Task output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);

            if (process.ExitCode != 0)
            {
                throw new NodeEnvironmentException();
            }
        }
        catch (Win32Exception)
        {
            throw new NodeEnvironmentException();
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string command, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommandPath(command),
            WorkingDirectory = workingDirectory ?? Configuration.DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            var nodeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs");
            startInfo.Environment["PATH"] = nodeDirectory + Path.PathSeparator
                + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        }

        return startInfo;
    }

    private static string ResolveCommandPath(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return command;
        }

        var executableName = command == "npm" ? "npm.cmd" : command + ".exe";
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs",
            executableName);

        return File.Exists(defaultPath) ? defaultPath : command;
    }
}
