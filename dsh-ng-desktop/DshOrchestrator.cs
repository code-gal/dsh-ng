using System.ComponentModel;
using System.Diagnostics;

namespace DshNgDesktop;

internal sealed class NodeEnvironmentException : Exception
{
    public NodeEnvironmentException() : base("未检测到可用的 Node.js 或 npm 环境。请安装 Node.js LTS 后重试。")
    {
    }
}

internal static class DshOrchestrator
{
    public static async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Configuration.DataDirectory);
        await AssertCommandAvailableAsync("node", cancellationToken);
        await AssertCommandAvailableAsync("npm", cancellationToken);
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
