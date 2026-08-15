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

internal sealed class DshOrchestrator : IDisposable
{
    private const string _packageName = "@deepseek-ai/dsh";
    private readonly int _port;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Queue<string> _output = new();
    private Process? _dshProcess;
    private bool _disposed;

    public DshOrchestrator(int port)
    {
        _port = port;
    }

    public bool IsInstalled => File.Exists(PackageManifestPath);

    public bool IsRunning => _dshProcess is { HasExited: false };

    public string? InstalledVersion => TryReadInstalledVersion();

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
            await InstallAsync(cancellationToken);
        }
        finally
        {
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
            StartDsh();
            await WaitForPanelAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Stop() => StopCore();

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

        _disposed = true;
        Stop();
        _operationLock.Dispose();
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo("npm");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--no-save");
        startInfo.ArgumentList.Add("--no-package-lock");
        startInfo.ArgumentList.Add(_packageName);
        startInfo.ArgumentList.Add("--loglevel");
        startInfo.ArgumentList.Add("warn");

        using var process = StartMonitoredProcess(startInfo);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new DshOperationException($"DSH 安装失败。\n\n{GetRecentOutput()}");
        }

        if (!IsInstalled)
        {
            throw new DshOperationException("DSH 安装完成，但未找到本地包文件。");
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
        Configuration.DataDirectory,
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

    private static ProcessStartInfo CreateProcessStartInfo(string command) => new()
    {
        FileName = ResolveCommandPath(command),
        WorkingDirectory = Configuration.DataDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

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
