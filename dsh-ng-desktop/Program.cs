using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Threading;
using DshNgDesktop.Views;
using Microsoft.Win32;

namespace DshNgDesktop;

internal static partial class Program
{
	// 供安装向导窗口使用的 Avalonia 应用入口，不启动经典桌面生命周期。
	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

	private static async Task Main()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			await DshOrchestrator.InitializeEnvironmentAsync();
		}
		catch (NodeEnvironmentException)
		{
			ShowNodeEnvironmentError();
			return;
		}

		var configuration = Configuration.Load();
		using var orchestrator = new DshOrchestrator(configuration.Port);
		using var trayApp = new TrayApp(orchestrator, configuration.Port);
		trayApp.Run();
	}

	private static void ShowNodeEnvironmentError()
	{
		const string message = "未检测到可用的 Node.js 或 npm。\n\n"
			+ "请安装 Node.js LTS（安装程序会包含 npm）。dsh-ng-desktop 不会修改系统全局 npm 包。\n\n"
			+ "安装完成后，请关闭并重新打开 DSH Desktop。";

		if (ShowTaskDialog("需要 Node.js", message, new nint(-3), 0x00000006) == 6)
		{
			Process.Start(new ProcessStartInfo("https://nodejs.org/en/download") { UseShellExecute = true });
		}
	}

	private static void ShowDshOperationError(string heading, string message) =>
		ShowTaskDialog(heading, message, new nint(-2), 0x00000001);

	private static void ShowDshOperationError(string message) =>
		ShowDshOperationError("无法完成操作", message);

	private static void ShowInformation(string heading, string message) =>
		ShowTaskDialog(heading, message, new nint(-3), 0x00000001);

	private static bool Confirm(string heading, string message) =>
		ShowTaskDialog(heading, message, new nint(-3), 0x00000006) == 6;

	private static int ShowTaskDialog(string heading, string message, nint icon, uint buttons)
	{
		var configuration = new TaskDialogConfig
		{
			cbSize = (uint)Marshal.SizeOf<TaskDialogConfig>(),
			dwCommonButtons = buttons,
			pszWindowTitle = "DSH Desktop",
			hMainIcon = icon,
			pszMainInstruction = heading,
			pszContent = message
		};

		return TaskDialogIndirect(in configuration, out var button, out _, out _) == 0 ? button : 0;
	}

	[SupportedOSPlatform("windows")]
	private sealed class TrayApp : IDisposable
	{
		private const string _windowClassName = "DshNgDesktop.TrayWindow";
		private const uint _callbackMessage = 0x8001;
		private const uint _commandMount = 1001;
		private const uint _commandOpenPanel = 1002;
		private const uint _commandStartup = 1003;
		private const uint _commandExit = 1004;
		private const uint _commandInstall = 1005;
		private const uint _commandCheckUpdate = 1006;
		private const uint _commandUpdate = 1007;
		private const uint _wmCommand = 0x0111;
		private const uint _wmContextMenu = 0x007B;
		private const uint _wmLeftButtonDoubleClick = 0x0203;
		private const uint _wmRightButtonUp = 0x0205;
		private const uint _wmInvoke = 0x8002;
		private const uint _nimAdd = 0x00000000;
		private const uint _nimDelete = 0x00000002;
		private const uint _nimSetVersion = 0x00000004;
		private const uint _notifyIconVersion4 = 4;
		private const uint _nimModify = 0x00000001;
		private const uint _nifMessage = 0x00000001;
		private const uint _nifIcon = 0x00000002;
		private const uint _nifTip = 0x00000004;
		private const uint _nifInfo = 0x00000010;
		private const uint _niifInfo = 0x00000001;
		private const uint _mfString = 0x00000000;
		private const uint _mfGrayed = 0x00000001;
		private const uint _mfSeparator = 0x00000800;
		private const uint _tpmRightButton = 0x0002;
		private const uint _imageIcon = 1;
		private const uint _lrLoadFromFile = 0x0010;
		private const uint _lrDefaultSize = 0x0040;

		private static readonly ConcurrentQueue<Action> _uiActions = new();
		private static TrayApp? _current;
		private static DshOrchestrator? _orchestrator;
		private static int _port;
		private static int _uiThreadId;
		private static bool _busy;
		private static bool _updateAvailable;
		private static Thread? _avaloniaThread;
		private static InstallWizardWindow? _wizardWindow;
		private readonly nint _instance;
		private readonly SafeWindowHandle _window;
		private nint _icon;
		private bool _ownsIcon;
		private bool _iconAdded;
		private bool _disposed;

		public unsafe TrayApp(DshOrchestrator orchestrator, int port)
		{
			_current = this;
			_orchestrator = orchestrator;
			_port = port;
			MigrateLegacyStartupEntry();
			_instance = GetModuleHandleW(null);
			if (_instance == nint.Zero)
			{
				throw new Win32Exception(Marshal.GetLastPInvokeError());
			}

			var windowClass = new WindowClass
			{
				hInstance = _instance,
				lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure
			};

			fixed (char* className = _windowClassName)
			{
				windowClass.lpszClassName = (nint)className;
				if (RegisterClassW(in windowClass) == 0)
				{
					throw new Win32Exception(Marshal.GetLastPInvokeError());
				}
			}

			_window = CreateWindowExW(
				0,
				_windowClassName,
				null,
				0,
				0,
				0,
				0,
				0,
				nint.Zero,
				nint.Zero,
				_instance,
				nint.Zero);

			if (_window.IsInvalid)
			{
				throw new Win32Exception(Marshal.GetLastPInvokeError());
			}
		}

		public void Run()
		{
			_uiThreadId = Environment.CurrentManagedThreadId;
			AddTrayIcon();
			_ = InitializeSessionAsync();

			int result;
			while ((result = GetMessageW(out var message, nint.Zero, 0, 0)) > 0)
			{
				TranslateMessage(in message);
				DispatchMessageW(in message);
			}

			if (result < 0)
			{
				throw new Win32Exception(Marshal.GetLastPInvokeError());
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			if (_iconAdded)
			{
				var icon = CreateNotificationData();
				ShellNotifyIconW(_nimDelete, ref icon);
			}

			if (_ownsIcon)
			{
				DestroyIcon(_icon);
			}

			_orchestrator = null;
			_current = null;
			_window.Dispose();
			UnregisterClassW(_windowClassName, _instance);
		}

		private void AddTrayIcon()
		{
			_icon = LoadImageW(
				nint.Zero,
				Path.Combine(AppContext.BaseDirectory, "Assets", "dsh.ico"),
				_imageIcon,
				0,
				0,
				_lrLoadFromFile | _lrDefaultSize);
			_ownsIcon = _icon != nint.Zero;
			if (_icon == nint.Zero)
			{
				_icon = LoadIconW(nint.Zero, 32512);
			}

			var icon = CreateNotificationData();
			if (!ShellNotifyIconW(_nimAdd, ref icon))
			{
				throw new Win32Exception(Marshal.GetLastPInvokeError());
			}

			_iconAdded = true;
			icon.uVersion = _notifyIconVersion4;
			ShellNotifyIconW(_nimSetVersion, ref icon);
		}

		private unsafe NotifyIconData CreateNotificationData()
		{
			var icon = new NotifyIconData
			{
				cbSize = (uint)sizeof(NotifyIconData),
				hWnd = _window.DangerousGetHandle(),
				uID = 1,
				uFlags = _nifMessage | _nifIcon | _nifTip,
				uCallbackMessage = _callbackMessage,
				hIcon = _icon
			};

			CopyFixed(icon.szTip, 128, GetTipText());
			return icon;
		}

		private void RefreshTrayIcon()
		{
			if (!_iconAdded)
			{
				return;
			}

			var icon = CreateNotificationData();
			ShellNotifyIconW(_nimModify, ref icon);
		}

		private void ShowBalloon(string title, string message)
		{
			if (!_iconAdded)
			{
				return;
			}

			var icon = CreateNotificationData();
			icon.uFlags |= _nifInfo;
			icon.dwInfoFlags = _niifInfo;
			unsafe
			{
				CopyFixed(icon.szInfoTitle, 64, title);
				CopyFixed(icon.szInfo, 256, message);
			}

			ShellNotifyIconW(_nimModify, ref icon);
		}

		private static unsafe void CopyFixed(char* destination, int length, string value)
		{
			var span = new Span<char>(destination, length);
			span.Clear();
			var text = value.Length >= length ? value[..(length - 1)] : value;
			text.AsSpan().CopyTo(span);
		}

		private static string GetTipText()
		{
			if (_orchestrator is null)
			{
				return "DSH Desktop";
			}

			if (!_orchestrator.IsInstalled)
			{
				return "DSH Desktop · 未安装";
			}

			if (_updateAvailable)
			{
				return "DSH Desktop · 有新版本";
			}

			return _orchestrator.IsRunning ? "DSH Desktop · 运行中" : "DSH Desktop · 已安装";
		}

		[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
		private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
		{
			if (message == _callbackMessage)
			{
				switch ((uint)lParam & 0xFFFF)
				{
					case _wmContextMenu:
					case _wmRightButtonUp:
						ShowContextMenu(window);
						return nint.Zero;
					case _wmLeftButtonDoubleClick:
						_ = HandlePrimaryActionAsync();
						return nint.Zero;
				}
			}

			if (message == _wmInvoke)
			{
				DrainUiActions();
				return nint.Zero;
			}

			if (message == _wmCommand)
			{
				switch ((uint)wParam & 0xFFFF)
				{
					case _commandInstall:
						ShowInstallWizard();
						return nint.Zero;
					case _commandMount:
						_ = StartDshAsync(false);
						return nint.Zero;
					case _commandOpenPanel:
						_ = StartDshAsync(true);
						return nint.Zero;
					case _commandCheckUpdate:
						_ = CheckForUpdateAsync(true);
						return nint.Zero;
					case _commandUpdate:
						_ = PromptAndUpdateAsync();
						return nint.Zero;
					case _commandStartup:
						ToggleStartup();
						return nint.Zero;
					case _commandExit:
						PostQuitMessage(0);
						return nint.Zero;
				}
			}

			return DefWindowProcW(window, message, wParam, lParam);
		}

		private static async Task InitializeSessionAsync()
		{
			if (_orchestrator is null)
			{
				return;
			}

			_current?.RefreshTrayIcon();
			if (!_orchestrator.IsInstalled)
			{
				ShowInstallWizard();
				return;
			}

			await StartDshAsync(false);
			await CheckForUpdateAsync(false);
		}

		private static async Task HandlePrimaryActionAsync()
		{
			if (_orchestrator is null)
			{
				return;
			}

			if (_orchestrator.IsInstalled)
			{
				await StartDshAsync(true);
				return;
			}

			ShowInstallWizard();
		}

		// M4.2/M4.3: 首次未安装时改为展示 Avalonia 安装向导窗口，而非 TaskDialog 确认。
		private static void ShowInstallWizard()
		{
			if (_orchestrator is null)
			{
				return;
			}

			EnsureAvaloniaStarted();
			var orchestrator = _orchestrator;
			var port = _port;
			Dispatcher.UIThread.Post(() =>
			{
				_wizardWindow ??= new InstallWizardWindow(orchestrator, port);
				_wizardWindow.Show();
				_wizardWindow.Activate();
			});
		}

		// Avalonia 以 SetupWithoutStarting 在专用后台线程运行，与现有的 Win32 托盘消息循环并存而不互相干扰。
		private static void EnsureAvaloniaStarted()
		{
			if (_avaloniaThread is not null)
			{
				return;
			}

			using var ready = new ManualResetEventSlim();
			var thread = new Thread(() =>
			{
				BuildAvaloniaApp().SetupWithoutStarting();
				ready.Set();
				Dispatcher.UIThread.MainLoop(CancellationToken.None);
			})
			{
				IsBackground = true,
				Name = "Avalonia UI"
			};
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			ready.Wait();
			_avaloniaThread = thread;
		}

		private static async Task PromptAndUpdateAsync()
		{
			if (_orchestrator is null)
			{
				return;
			}

			if (!_orchestrator.IsInstalled)
			{
				ShowInstallWizard();
				return;
			}

			var currentVersion = _orchestrator.InstalledVersion ?? "未知";
			if (!await ConfirmAsync(
				"更新 DSH",
				$"当前版本：{currentVersion}\n\n将下载并安装 npm 上的最新版本。更新期间会暂时停止正在运行的 DSH。是否继续？"))
			{
				return;
			}

			await InstallOrUpdateCoreAsync(true);
		}

		private static async Task StartDshAfterInstallAsync()
		{
			if (_orchestrator is null)
			{
				return;
			}

			try
			{
				await ShowBalloonAsync("正在启动 DSH", "安装已完成，正在拉起后台服务。");
				await _orchestrator.EnsureRunningAsync();
				await RefreshTrayIconAsync();
				await ShowBalloonAsync("DSH 已启动", $"面板地址：http://localhost:{_port}/");
			}
			catch (DshOperationException exception)
			{
				await ShowErrorAsync("无法启动 DSH", exception.Message);
			}
		}

		private static async Task StartDshAsync(bool openPanel)
		{
			if (_orchestrator is null)
			{
				return;
			}

			if (!_orchestrator.IsInstalled)
			{
				await ShowInformationAsync("尚未安装 DSH", "打开面板或启动服务前，请先从托盘菜单安装 DSH。");
				return;
			}

			if (!TryBeginOperation())
			{
				return;
			}

			try
			{
				if (!_orchestrator.IsRunning)
				{
					await ShowBalloonAsync("正在启动 DSH", "正在拉起后台服务，请稍候。");
				}

				await _orchestrator.EnsureRunningAsync();
				await RefreshTrayIconAsync();
				if (openPanel)
				{
					Process.Start(new ProcessStartInfo($"http://localhost:{_port}/") { UseShellExecute = true });
				}
				else
				{
					await ShowBalloonAsync("DSH 已启动", $"面板地址：http://localhost:{_port}/");
				}
			}
			catch (DshOperationException exception)
			{
				await ShowErrorAsync("无法启动 DSH", exception.Message);
			}
			catch (Win32Exception)
			{
				await ShowErrorAsync("无法打开默认浏览器或启动 DSH。");
			}
			finally
			{
				EndOperation();
			}
		}

		private static async Task CheckForUpdateAsync(bool prompted)
		{
			if (_orchestrator is null)
			{
				return;
			}

			if (!_orchestrator.IsInstalled)
			{
				if (prompted)
				{
					await ShowInformationAsync("尚未安装 DSH", "请先安装 DSH，然后再检测更新。");
				}

				return;
			}

			if (!TryBeginOperation())
			{
				return;
			}

			try
			{
				if (prompted)
				{
					await ShowBalloonAsync("正在检测更新", "正在查询 npm 上的最新版本。");
				}

				var result = await _orchestrator.CheckForUpdateAsync();
				_updateAvailable = result.UpdateAvailable;
				await RefreshTrayIconAsync();

				if (result.UpdateAvailable)
				{
					if (await ConfirmAsync(
						"发现新版本",
						$"当前版本：{result.InstalledVersion}\n最新版本：{result.LatestVersion}\n\n不会自动更新。是否现在手动更新？"))
					{
						await InstallOrUpdateCoreAsync(true, true);
					}
					else
					{
						await ShowInformationAsync("已保留当前版本", "需要时请从托盘菜单选择“更新 DSH”。");
					}

					return;
				}

				if (prompted)
				{
					await ShowInformationAsync("已是最新版本", $"当前 DSH 版本：{result.InstalledVersion ?? "未知"}");
				}
			}
			catch (DshOperationException exception)
			{
				if (prompted)
				{
					await ShowErrorAsync("无法检测更新", exception.Message);
				}
				else
				{
					await ShowBalloonAsync("无法检测更新", "可稍后从托盘菜单重试。");
				}
			}
			finally
			{
				EndOperation();
			}
		}

		private static async Task InstallOrUpdateCoreAsync(bool isUpdate, bool alreadyBusy = false)
		{
			if (_orchestrator is null || (!alreadyBusy && !TryBeginOperation()))
			{
				return;
			}

			try
			{
				await ShowBalloonAsync(
					isUpdate ? "正在更新 DSH" : "正在安装 DSH",
					"这可能需要一两分钟，请稍候。");
				await _orchestrator.InstallOrUpdateAsync();
				_updateAvailable = false;
				await RefreshTrayIconAsync();
				await ShowInformationAsync(
					isUpdate ? "更新完成" : "安装完成",
					isUpdate
						? $"DSH 已更新到 {_orchestrator.InstalledVersion ?? "最新版本"}。"
						: "DSH 已安装到本地私有目录。现在可以从托盘打开面板。");
				await StartDshAfterInstallAsync();
			}
			catch (DshOperationException exception)
			{
				await ShowErrorAsync(isUpdate ? "更新失败" : "安装失败", exception.Message);
			}
			finally
			{
				if (!alreadyBusy)
				{
					EndOperation();
				}
			}
		}

		private static bool TryBeginOperation()
		{
			if (_busy)
			{
				_ = ShowInformationAsync("请稍候", "当前已有安装、更新或启动正在进行。");
				return false;
			}

			_busy = true;
			return true;
		}

		private static void EndOperation()
		{
			_busy = false;
			_ = RefreshTrayIconAsync();
		}

		private static Task ShowBalloonAsync(string title, string message) =>
			InvokeOnUiAsync(() => _current?.ShowBalloon(title, message));

		private static Task RefreshTrayIconAsync() =>
			InvokeOnUiAsync(() => _current?.RefreshTrayIcon());

		private static Task ShowInformationAsync(string heading, string message) =>
			InvokeOnUiAsync(() => ShowInformation(heading, message));

		private static Task ShowErrorAsync(string heading, string message) =>
			InvokeOnUiAsync(() => ShowDshOperationError(heading, message));

		private static Task ShowErrorAsync(string message) =>
			ShowErrorAsync("无法完成操作", message);

		private static Task<bool> ConfirmAsync(string heading, string message) =>
			InvokeOnUiAsync(() => Confirm(heading, message));

		private static Task InvokeOnUiAsync(Action action)
		{
			if (Environment.CurrentManagedThreadId == _uiThreadId || _current is null)
			{
				action();
				return Task.CompletedTask;
			}

			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_uiActions.Enqueue(() =>
			{
				try
				{
					action();
					completion.TrySetResult();
				}
				catch (Exception exception)
				{
					completion.TrySetException(exception);
				}
			});

			PostMessageW(_current._window.DangerousGetHandle(), _wmInvoke, 0, nint.Zero);
			return completion.Task;
		}

		private static Task<T> InvokeOnUiAsync<T>(Func<T> action)
		{
			if (Environment.CurrentManagedThreadId == _uiThreadId || _current is null)
			{
				return Task.FromResult(action());
			}

			var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			_uiActions.Enqueue(() =>
			{
				try
				{
					completion.TrySetResult(action());
				}
				catch (Exception exception)
				{
					completion.TrySetException(exception);
				}
			});

			PostMessageW(_current._window.DangerousGetHandle(), _wmInvoke, 0, nint.Zero);
			return completion.Task;
		}

		private static void DrainUiActions()
		{
			while (_uiActions.TryDequeue(out var action))
			{
				action();
			}
		}

		private static void ToggleStartup()
		{
			try
			{
				using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
				using var approvalKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
				if (!IsStartupEnabled(runKey, approvalKey))
				{
					runKey.SetValue("DSH Desktop", GetStartupCommand(), RegistryValueKind.String);
					approvalKey.DeleteValue("DSH Desktop", false);
					ShowInformation("已启用开机启动", "DSH Desktop 将在登录 Windows 后自动运行。");
				}
				else
				{
					runKey.DeleteValue("DSH Desktop", false);
					approvalKey.DeleteValue("DSH Desktop", false);
					ShowInformation("已关闭开机启动", "DSH Desktop 不会再随 Windows 自动运行。");
				}
			}
			catch (UnauthorizedAccessException)
			{
				ShowDshOperationError("无法修改当前用户的开机启动设置。");
			}
		}

		private static void MigrateLegacyStartupEntry()
		{
			try
			{
				using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
				if (runKey.GetValue("DSH Desktop") is not null || runKey.GetValue("dsh-ng-desktop") is null)
				{
					return;
				}

				using var approvalKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
				var approvalState = approvalKey.GetValue("dsh-ng-desktop") as byte[];
				runKey.SetValue("DSH Desktop", GetStartupCommand(), RegistryValueKind.String);
				if (approvalState is not null)
				{
					approvalKey.SetValue("DSH Desktop", approvalState, RegistryValueKind.Binary);
				}

				runKey.DeleteValue("dsh-ng-desktop", false);
				approvalKey.DeleteValue("dsh-ng-desktop", false);
			}
			catch (UnauthorizedAccessException)
			{
			}
		}

		private static bool IsStartupEnabled(RegistryKey? runKey, RegistryKey? approvalKey)
		{
			if (runKey?.GetValue("DSH Desktop") is not string)
			{
				return false;
			}

			return approvalKey?.GetValue("DSH Desktop") is not byte[] { Length: > 0 } state || state[0] != 3;
		}

		private static string GetStartupCommand()
		{
			var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序路径。");
			var applicationHost = Path.Combine(AppContext.BaseDirectory, "DshNgDesktop.exe");
			if (File.Exists(applicationHost))
			{
				return $"\"{applicationHost}\"";
			}

			return $"\"{executablePath}\"";
		}

		private static void ShowContextMenu(nint window)
		{
			if (!GetCursorPos(out var point))
			{
				return;
			}

			using var menu = CreatePopupMenu();
			if (menu.IsInvalid)
			{
				return;
			}

			var installed = _orchestrator?.IsInstalled == true;
			var running = _orchestrator?.IsRunning == true;
			using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
			using var approvalKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

			if (_busy)
			{
				AppendMenuW(menu, _mfString | _mfGrayed, 0, "正在处理…");
			}
			else if (!installed)
			{
				AppendMenuW(menu, _mfString, _commandInstall, "安装 DSH");
			}
			else
			{
				AppendMenuW(
					menu,
					running ? _mfString | _mfGrayed : _mfString,
					_commandMount,
					running ? "DSH 运行中" : "启动 DSH");
				AppendMenuW(menu, _mfString, _commandOpenPanel, "打开面板");
				AppendMenuW(menu, _mfString, _commandCheckUpdate, "检测更新");
				AppendMenuW(menu, _mfString, _commandUpdate, _updateAvailable ? "更新 DSH（有新版本）" : "更新 DSH");
			}

			AppendMenuW(menu, _mfSeparator, 0, string.Empty);
			AppendMenuW(
				menu,
				_mfString,
				_commandStartup,
				IsStartupEnabled(runKey, approvalKey) ? "禁用开机启动" : "开机启动");
			AppendMenuW(menu, _mfString, _commandExit, "退出");

			SetForegroundWindow(window);
			TrackPopupMenu(menu, _tpmRightButton, point.x, point.y, 0, window, nint.Zero);
			PostMessageW(window, 0, 0, nint.Zero);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WindowClass
	{
		public uint style;
		public nint lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public nint hInstance;
		public nint hIcon;
		public nint hCursor;
		public nint hbrBackground;
		public nint lpszMenuName;
		public nint lpszClassName;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Message
	{
		public nint hwnd;
		public uint message;
		public nuint wParam;
		public nint lParam;
		public uint time;
		public Point pt;
		public uint lPrivate;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int x;
		public int y;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private unsafe struct NotifyIconData
	{
		public uint cbSize;
		public nint hWnd;
		public uint uID;
		public uint uFlags;
		public uint uCallbackMessage;
		public nint hIcon;
		public fixed char szTip[128];
		public uint dwState;
		public uint dwStateMask;
		public fixed char szInfo[256];
		public uint uVersion;
		public fixed char szInfoTitle[64];
		public uint dwInfoFlags;
		public Guid guidItem;
		public nint hBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct TaskDialogConfig
	{
		public uint cbSize;
		public nint hwndParent;
		public nint hInstance;
		public uint dwFlags;
		public uint dwCommonButtons;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszWindowTitle;
		public nint hMainIcon;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszMainInstruction;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszContent;
		public uint cButtons;
		public nint pButtons;
		public int nDefaultButton;
		public uint cRadioButtons;
		public nint pRadioButtons;
		public int nDefaultRadioButton;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszVerificationText;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszExpandedInformation;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszExpandedControlText;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszCollapsedControlText;
		public nint hFooterIcon;
		[MarshalAs(UnmanagedType.LPWStr)] public string? pszFooter;
		public nint pfCallback;
		public nint lpCallbackData;
		public uint cxWidth;
	}

	private sealed class SafeWindowHandle : SafeHandle
	{
		public SafeWindowHandle() : base(nint.Zero, true)
		{
		}

		public override bool IsInvalid => handle == nint.Zero;

		protected override bool ReleaseHandle() => DestroyWindow(handle);
	}

	private sealed class SafeMenuHandle : SafeHandle
	{
		public SafeMenuHandle() : base(nint.Zero, true)
		{
		}

		public override bool IsInvalid => handle == nint.Zero;

		protected override bool ReleaseHandle() => DestroyMenu(handle);
	}

	[LibraryImport("kernel32", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint GetModuleHandleW(string? moduleName);

	[LibraryImport("user32", EntryPoint = "RegisterClassW", SetLastError = true)]
	private static partial ushort RegisterClassW(in WindowClass windowClass);

	[LibraryImport("user32", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool UnregisterClassW(string className, nint instance);

	[LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial SafeWindowHandle CreateWindowExW(
		uint extendedStyle,
		string className,
		string? windowName,
		uint style,
		int x,
		int y,
		int width,
		int height,
		nint parent,
		nint menu,
		nint instance,
		nint parameter);

	[LibraryImport("user32", EntryPoint = "DefWindowProcW")]
	private static partial nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

	[LibraryImport("user32", EntryPoint = "DestroyWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyWindow(nint window);

	[LibraryImport("user32", EntryPoint = "GetMessageW", SetLastError = true)]
	private static partial int GetMessageW(out Message message, nint window, uint minimumFilter, uint maximumFilter);

	[LibraryImport("user32", EntryPoint = "TranslateMessage")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TranslateMessage(in Message message);

	[LibraryImport("user32", EntryPoint = "DispatchMessageW")]
	private static partial nint DispatchMessageW(in Message message);

	[LibraryImport("user32", EntryPoint = "CreatePopupMenu", SetLastError = true)]
	private static partial SafeMenuHandle CreatePopupMenu();

	[LibraryImport("user32", EntryPoint = "DestroyMenu")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyMenu(nint menu);

	[LibraryImport("user32", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AppendMenuW(SafeMenuHandle menu, uint flags, nuint itemId, string itemText);

	[LibraryImport("user32", EntryPoint = "GetCursorPos", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetCursorPos(out Point point);

	[LibraryImport("user32", EntryPoint = "SetForegroundWindow")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow(nint window);

	[LibraryImport("user32", EntryPoint = "TrackPopupMenu")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool TrackPopupMenu(SafeMenuHandle menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

	[LibraryImport("user32", EntryPoint = "PostMessageW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

	[LibraryImport("user32", EntryPoint = "PostQuitMessage")]
	private static partial void PostQuitMessage(int exitCode);

	[LibraryImport("user32", EntryPoint = "LoadIconW")]
	private static partial nint LoadIconW(nint instance, nuint iconName);

	[LibraryImport("user32", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
	private static partial nint LoadImageW(nint instance, string name, uint type, int desiredWidth, int desiredHeight, uint loadFlags);

	[LibraryImport("user32", EntryPoint = "DestroyIcon")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyIcon(nint icon);

	[LibraryImport("shell32", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData iconData);

	[DllImport("comctl32", EntryPoint = "TaskDialogIndirect", CharSet = CharSet.Unicode)]
	private static extern int TaskDialogIndirect(in TaskDialogConfig configuration, out int button, out int radioButton, out int verificationFlag);
}
