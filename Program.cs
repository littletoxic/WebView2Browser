using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

internal static class Program
{
    private const string WindowClassName = "WebView2Browser.NativeAot";
    private const string IconFileName = "WebView2Browser.ico";
    private static BrowserWindow? s_window;

    [STAThread]
    private static unsafe int Main()
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            using FreeLibrarySafeHandle instanceHandle = GetModuleHandle();
            if (instanceHandle.IsInvalid)
            {
                throw new InvalidOperationException("GetModuleHandle failed.");
            }

            HINSTANCE instance = (HINSTANCE)instanceHandle.DangerousGetHandle();
            RegisterWindowClass(instance);

            if (!TryLaunchWindow(instanceHandle))
            {
                return 1;
            }

            _ = s_window!.InitializeWebViewsAsync();

            MSG message;
            while (GetMessage(out message, HWND.Null, 0, 0))
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }

            return (int)message.wParam.Value;
        }
        catch (Exception ex)
        {
            LogFailure(ex.ToString());
            ShowError(ex.Message);
            return ex.HResult;
        }
        finally
        {
            s_window?.Dispose();
            s_window = null;
        }
    }

    private static unsafe void RegisterWindowClass(HINSTANCE instance)
    {
        HICON appIcon = LoadApplicationIcon();

        fixed (char* className = WindowClassName)
        {
            WNDCLASSEXW windowClass = new()
            {
                cbSize = (uint)Unsafe.SizeOf<WNDCLASSEXW>(),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hIcon = appIcon,
                hCursor = LoadCursor(HINSTANCE.Null, IDC_ARROW),
                hbrBackground = (HBRUSH)new IntPtr((int)SYS_COLOR_INDEX.COLOR_WINDOW + 1),
                lpszClassName = className,
                hIconSm = appIcon,
            };

            ushort atom = RegisterClassEx(in windowClass);
            if (atom == 0)
            {
                throw new InvalidOperationException("RegisterClassEx failed.");
            }
        }
    }

    private static unsafe HICON LoadApplicationIcon()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, IconFileName);
        if (File.Exists(iconPath))
        {
            fixed (char* iconPathPointer = iconPath)
            {
                HANDLE iconHandle = LoadImage(
                    HINSTANCE.Null,
                    iconPathPointer,
                    GDI_IMAGE_TYPE.IMAGE_ICON,
                    0,
                    0,
                    IMAGE_FLAGS.LR_LOADFROMFILE | IMAGE_FLAGS.LR_DEFAULTSIZE | IMAGE_FLAGS.LR_SHARED);

                if (!iconHandle.IsNull)
                {
                    return (HICON)(IntPtr)iconHandle;
                }
            }
        }

        return LoadIcon(HINSTANCE.Null, IDI_APPLICATION);
    }

    private static bool TryLaunchWindow(SafeHandle instanceHandle)
    {
        while (true)
        {
            s_window = new BrowserWindow(instanceHandle, WindowClassName);
            if (s_window.Create(SHOW_WINDOW_CMD.SW_SHOW))
            {
                return true;
            }

            s_window.Dispose();
            s_window = null;

            MESSAGEBOX_RESULT result = MessageBox(
                HWND.Null,
                "Could not launch the browser.",
                "Error",
                MESSAGEBOX_STYLE.MB_RETRYCANCEL | MESSAGEBOX_STYLE.MB_ICONERROR);

            if (result != MESSAGEBOX_RESULT.IDRETRY)
            {
                return false;
            }
        }
    }

    private static void ShowError(string message)
    {
        MessageBox(
            HWND.Null,
            message,
            BrowserWindow.WindowTitle,
            MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);
    }

    internal static void LogFailure(string message)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "WebView2Browser.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        return s_window?.HandleWindowMessage(hwnd, message, wParam, lParam)
            ?? DefWindowProc(hwnd, message, wParam, lParam);
    }
}
