using Microsoft.Web.WebView2.Core;
using Windows.Win32.Foundation;
using static Windows.Win32.PInvoke;

internal sealed class BrowserTab(BrowserWindow browserWindow, HWND parentHwnd, int tabId)
{
    private CoreWebView2DevToolsProtocolEventReceiver? _securityStateChangedReceiver;

    internal CoreWebView2Controller? Controller { get; private set; }

    internal CoreWebView2? WebView { get; private set; }

    internal async Task InitializeAsync(CoreWebView2Environment environment, bool shouldBeActive)
    {
        try
        {
            Controller = await browserWindow.CreateControllerAsync(environment);
            WebView = Controller.CoreWebView2;

            WebView.WebMessageReceived += (_, args) => browserWindow.HandleTabMessageReceived(tabId, WebView, args);
            WebView.HistoryChanged += (_, _) => browserWindow.HandleTabHistoryUpdate(tabId, WebView);
            WebView.SourceChanged += (_, _) => browserWindow.HandleTabUriUpdate(tabId, WebView);
            WebView.NavigationStarting += (_, _) => browserWindow.HandleTabNavigationStarting(tabId, WebView);
            WebView.NavigationCompleted += (_, args) => browserWindow.HandleTabNavigationCompleted(tabId, WebView, args);

            await WebView.CallDevToolsProtocolMethodAsync("Security.enable", "{}");
            _securityStateChangedReceiver = WebView.GetDevToolsProtocolEventReceiver("Security.securityStateChanged");
            _securityStateChangedReceiver.DevToolsProtocolEventReceived += (_, args) =>
                browserWindow.HandleTabSecurityUpdate(tabId, WebView, args);

            WebView.Navigate("https://www.bing.com");
            browserWindow.HandleTabCreated(tabId, shouldBeActive);
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
            browserWindow.ShowError("Tab WebView creation failed.");
        }
    }

    internal void Resize()
    {
        if (Controller is null)
        {
            return;
        }

        if (!GetClientRect(parentHwnd, out RECT bounds))
        {
            return;
        }

        bounds.top += browserWindow.GetDpiAwareBound(BrowserWindow.UiBarHeight);
        Controller.Bounds = BrowserWindow.ToFoundationRect(bounds);
    }

    internal void Close()
    {
        Controller?.Close();
        Controller = null;
        WebView = null;
        _securityStateChangedReceiver = null;
    }
}
