using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DshNgDesktop;

internal static partial class Program
{
	private static async Task Main()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			await DshOrchestrator.InitializeAsync();
		}
		catch (NodeEnvironmentException)
		{
			ShowNodeEnvironmentError();
			return;
		}

		Configuration.Load();
		using var trayApp = new TrayApp();
		trayApp.Run();
	}

	private static void ShowNodeEnvironmentError()
	{
		const string message = "未检测到可用的 Node.js 或 npm。\n\n"
			+ "请安装 Node.js LTS（安装程序会包含 npm）。dsh-ng-desktop 不会修改系统全局 npm 包。\n\n"
			+ "安装完成后，请关闭并重新打开 dsh-ng-desktop。\n\n"
			+ "是否现在打开 Node.js 官方下载页？";

		if (MessageBoxW(nint.Zero, message, "需要 Node.js", 0x00000014) == 6)
		{
			Process.Start(new ProcessStartInfo("https://nodejs.org/en/download") { UseShellExecute = true });
		}
	}

	private sealed class TrayApp : IDisposable
	{
		private const string _windowClassName = "DshNgDesktop.TrayWindow";
		private const uint _callbackMessage = 0x8001;
		private const uint _commandMount = 1001;
		private const uint _commandExit = 1002;
		private const uint _wmCommand = 0x0111;
		private const uint _wmContextMenu = 0x007B;
		private const uint _wmRightButtonUp = 0x0205;
		private const uint _nimAdd = 0x00000000;
		private const uint _nimDelete = 0x00000002;
		private const uint _nimSetVersion = 0x00000004;
		private const uint _notifyIconVersion4 = 4;
		private const uint _nifMessage = 0x00000001;
		private const uint _nifIcon = 0x00000002;
		private const uint _nifTip = 0x00000004;
		private const uint _mfString = 0x00000000;
		private const uint _tpmRightButton = 0x0002;

		private static SafeMenuHandle? _menu;
		private readonly nint _instance;
		private readonly SafeWindowHandle _window;
		private bool _iconAdded;
		private bool _disposed;

		public unsafe TrayApp()
		{
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

			_menu = CreatePopupMenu();
			if (_menu.IsInvalid ||
				!AppendMenuW(_menu, _mfString, _commandMount, "挂载 DSH") ||
				!AppendMenuW(_menu, _mfString, _commandExit, "退出"))
			{
				throw new Win32Exception(Marshal.GetLastPInvokeError());
			}
		}

		public void Run()
		{
			AddTrayIcon();

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

			_menu?.Dispose();
			_menu = null;
			_window.Dispose();
			UnregisterClassW(_windowClassName, _instance);
		}

		private void AddTrayIcon()
		{
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
				hIcon = LoadIconW(nint.Zero, 32512)
			};

			char* tip = icon.szTip;
			"dsh-ng-desktop".AsSpan().CopyTo(new Span<char>(tip, 128));

			return icon;
		}

		[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
		private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
		{
			if (message == _callbackMessage && ((uint)lParam == _wmContextMenu || (uint)lParam == _wmRightButtonUp))
			{
				ShowContextMenu(window);
				return nint.Zero;
			}

			if (message == _wmCommand)
			{
				switch ((uint)wParam & 0xFFFF)
				{
					case _commandMount:
						return nint.Zero;
					case _commandExit:
						PostQuitMessage(0);
						return nint.Zero;
				}
			}

			return DefWindowProcW(window, message, wParam, lParam);
		}

		private static void ShowContextMenu(nint window)
		{
			if (_menu is null || !GetCursorPos(out var point))
			{
				return;
			}

			SetForegroundWindow(window);
			TrackPopupMenu(_menu, _tpmRightButton, point.x, point.y, 0, window, nint.Zero);
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

	[LibraryImport("shell32", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData iconData);

	[LibraryImport("user32", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial int MessageBoxW(nint window, string text, string caption, uint type);
}
