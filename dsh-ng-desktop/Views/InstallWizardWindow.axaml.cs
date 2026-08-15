using System;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DshNgDesktop.Views;

internal sealed partial class InstallWizardWindow : Window
{
    private readonly DshOrchestrator _orchestrator;
    private readonly int _port;
    private readonly StringBuilder _logText = new();

    public InstallWizardWindow(DshOrchestrator orchestrator, int port)
    {
        _orchestrator = orchestrator;
        _port = port;
        InitializeComponent();

        _orchestrator.StageChanged += OnStageChanged;
        _orchestrator.LogAppended += OnLogAppended;
        Closing += OnClosing;

        RefreshInitialState();
    }

    private void RefreshInitialState()
    {
        if (_orchestrator.IsInstalled)
        {
            StatusText.Text = _orchestrator.IsRunning
                ? $"DSH 已安装并运行中：http://localhost:{_port}/"
                : "DSH 已安装。";
            InstallButton.Content = "重新安装";
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 关闭窗口只是隐藏，应用继续在托盘驻留。
        e.Cancel = true;
        Hide();
    }

    private async void OnInstallButtonClick(object? sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        CancelButton.IsVisible = true;
        CancelButton.IsEnabled = true;
        ErrorText.Text = string.Empty;
        _logText.Clear();
        LogOutput.Clear();

        try
        {
            await _orchestrator.InstallOrUpdateAsync();
            StatusText.Text = "安装完成，正在启动 DSH…";
            await _orchestrator.EnsureRunningAsync();
            StatusText.Text = $"DSH 已启动：http://localhost:{_port}/";
            Process.Start(new ProcessStartInfo($"http://localhost:{_port}/") { UseShellExecute = true });
            InstallButton.Content = "已完成";
            CancelButton.IsVisible = false;
        }
        catch (DshOperationException exception)
        {
            if (_orchestrator.WasInstallCanceled)
            {
                StatusText.Text = "安装已取消，临时文件已清理。";
            }
            else
            {
                ErrorText.Text = exception.Message;
            }

            InstallButton.IsEnabled = true;
            CancelButton.IsVisible = false;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            InstallButton.IsEnabled = true;
            CancelButton.IsVisible = false;
        }
    }

    private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator.CancelInstall())
        {
            StatusText.Text = "正在停止安装并清理临时文件…";
            CancelButton.IsEnabled = false;
        }
    }

    private void OnLogAppended(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            var selectionStart = LogOutput.SelectionStart;
            var selectionEnd = LogOutput.SelectionEnd;
            var shouldFollowOutput = selectionStart == selectionEnd
                && LogOutput.CaretIndex == (LogOutput.Text?.Length ?? 0);
            _logText.AppendLine(line);
            LogOutput.Text = _logText.ToString();
            if (shouldFollowOutput)
            {
                LogOutput.CaretIndex = LogOutput.Text?.Length ?? 0;
            }
            else
            {
                LogOutput.SelectionStart = selectionStart;
                LogOutput.SelectionEnd = selectionEnd;
            }
        });

    private void OnStageChanged(DshStage stage) =>
        Dispatcher.UIThread.Post(() => StatusText.Text = DescribeStage(stage));

    private static string DescribeStage(DshStage stage) => stage switch
    {
        DshStage.Installing => "正在安装 DSH…",
        DshStage.Starting => "正在启动 DSH…",
        DshStage.Running => "DSH 正在运行。",
        DshStage.Failed => "操作失败，请查看下方日志。",
        _ => "尚未安装 DSH。"
    };
}
