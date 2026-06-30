using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using static Windows.Win32.PInvoke;

internal sealed class BrowserWindow(SafeHandle instanceHandle, string windowClassName) : IDisposable
{
    internal const string WindowTitle = "WebView2Browser";
    internal const int UiBarHeight = 70;

    private const int OptionsDropdownHeight = 108;
    private const int OptionsDropdownWidth = 200;
    private const int DefaultDpi = 96;
    private const int MinWindowWidth = 510;
    private const int MinWindowHeight = 75;
    private const int InitialWidth = 1200;
    private const int InitialHeight = 900;
    private const string MissingRuntimeMessage =
        "Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen WebView2 Runtime, then restart WebView2Browser.";

    private readonly Dictionary<int, BrowserTab> _tabs = [];
    private HWND _hwnd;
    private Win32SynchronizationContext? _syncContext;
    private CoreWebView2Environment? _uiEnvironment;
    private CoreWebView2Environment? _contentEnvironment;
    private CoreWebView2Controller? _controlsController;
    private CoreWebView2Controller? _optionsController;
    private CoreWebView2? _controlsWebView;
    private CoreWebView2? _optionsWebView;
    private int _activeTabId;
    private int _minWindowWidth;
    private int _minWindowHeight;

    internal unsafe bool Create(SHOW_WINDOW_CMD showCommand)
    {
        _hwnd = CreateWindowEx(
            default,
            windowClassName,
            WindowTitle,
            WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT,
            0,
            InitialWidth,
            InitialHeight,
            HWND.Null,
            null,
            instanceHandle,
            null);

        if (_hwnd == HWND.Null)
        {
            return false;
        }

        _syncContext = new Win32SynchronizationContext(_hwnd);
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        UpdateMinWindowSize();
        ShowWindow(_hwnd, showCommand);
        UpdateWindow(_hwnd);

        return true;
    }

    internal async Task InitializeWebViewsAsync()
    {
        try
        {
            if (!TryGetWebView2RuntimeVersion())
            {
                return;
            }

            string appDataDirectory = GetAppDataDirectory();
            string userDataDirectory = Path.Combine(appDataDirectory, "User Data");
            string browserDataDirectory = Path.Combine(appDataDirectory, "Browser Data");
            Directory.CreateDirectory(userDataDirectory);
            Directory.CreateDirectory(browserDataDirectory);

            _contentEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDirectory, null);
            _uiEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null, browserDataDirectory, null);

            await CreateBrowserControlsWebViewAsync();
            await CreateBrowserOptionsWebViewAsync();
        }
        catch (Exception ex)
        {
            LogWebView2Failure(ex);
            ShowError("WebView2 environment creation failed.");
        }
    }

    private bool TryGetWebView2RuntimeVersion()
    {
        try
        {
            string? version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            if (!string.IsNullOrWhiteSpace(version) &&
                !string.Equals(version, "0.0.0.0", StringComparison.Ordinal))
            {
                return true;
            }
        }
        catch (FileNotFoundException ex)
        {
            LogWebView2Failure(ex);
            ShowError(MissingRuntimeMessage);
            return false;
        }

        Program.LogFailure("WebView2 Runtime detection returned no available runtime.");
        ShowError(MissingRuntimeMessage);
        return false;
    }

    private static void LogWebView2Failure(Exception ex)
    {
        Program.LogFailure($"HResult=0x{ex.HResult:X8}{Environment.NewLine}{ex}");
    }

    private static string GetAppDataDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Microsoft", WindowTitle);
    }

    internal async Task<CoreWebView2Controller> CreateControllerAsync(CoreWebView2Environment environment)
    {
        ulong windowHandle = (ulong)(nuint)(IntPtr)_hwnd;
        CoreWebView2ControllerWindowReference windowReference =
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle(windowHandle);

        return await environment.CreateCoreWebView2ControllerAsync(windowReference);
    }

    private async Task CreateBrowserControlsWebViewAsync()
    {
        if (_uiEnvironment is null)
        {
            return;
        }

        _controlsController = await CreateControllerAsync(_uiEnvironment);
        _controlsWebView = _controlsController.CoreWebView2;
        _controlsWebView.Settings.AreDevToolsEnabled = false;

        _controlsController.ZoomFactorChanged += (_, _) => _controlsController.ZoomFactor = 1.0;
        _controlsWebView.WebMessageReceived += HandleUiMessageReceived;

        ResizeUiWebViews();
        _controlsWebView.Navigate(GetFilePathAsUri(GetFullPathFor(@"wvbrowser_ui\controls_ui\default.html")));
    }

    private async Task CreateBrowserOptionsWebViewAsync()
    {
        if (_uiEnvironment is null)
        {
            return;
        }

        _optionsController = await CreateControllerAsync(_uiEnvironment);
        _optionsWebView = _optionsController.CoreWebView2;
        _optionsWebView.Settings.AreDevToolsEnabled = false;

        _optionsController.ZoomFactorChanged += (_, _) => _optionsController.ZoomFactor = 1.0;
        _optionsController.IsVisible = false;
        _optionsController.LostFocus += (_, _) =>
        {
            JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.OptionsLostFocus);
            PostJsonToWebView(message, _controlsWebView);
        };
        _optionsWebView.WebMessageReceived += HandleUiMessageReceived;

        ResizeUiWebViews();
        _optionsWebView.Navigate(GetFilePathAsUri(GetFullPathFor(@"wvbrowser_ui\controls_ui\options.html")));
    }

    internal unsafe LRESULT HandleWindowMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == Win32SynchronizationContext.ContinuationMessage)
        {
            _syncContext?.DispatchPending();
            return new LRESULT(0);
        }

        switch (message)
        {
            case WM_GETMINMAXINFO:
                MINMAXINFO* minmax = (MINMAXINFO*)lParam.Value;
                minmax->ptMinTrackSize.X = _minWindowWidth;
                minmax->ptMinTrackSize.Y = _minWindowHeight;
                return new LRESULT(0);

            case WM_DPICHANGED:
                UpdateMinWindowSize();
                ResizeAllWebViews();
                return new LRESULT(0);

            case WM_SIZE:
                ResizeAllWebViews();
                return new LRESULT(0);

            case WM_CLOSE:
                if (_controlsWebView is not null)
                {
                    PostJsonToWebView(JsonNodeExtensions.CreateMessage(MessageCode.CloseWindow), _controlsWebView);
                }
                else
                {
                    DestroyWindow(hwnd);
                }

                return new LRESULT(0);

            case WM_DESTROY:
                PostQuitMessage(0);
                return new LRESULT(0);

            case WM_NCDESTROY:
                Dispose();
                return DefWindowProc(hwnd, message, wParam, lParam);

            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void HandleUiMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            JsonObject json = JsonNodeExtensions.ParseObject(eventArgs.WebMessageAsJson);
            JsonObject args = json.Args();

            switch (json.Message())
            {
                case MessageCode.CreateTab:
                    CreateTab(args.Int32("tabId"), args.Boolean("active"));
                    break;

                case MessageCode.Navigate:
                    NavigateActiveTab(args);
                    break;

                case MessageCode.GoForward:
                    ActiveWebView?.GoForward();
                    break;

                case MessageCode.GoBack:
                    ActiveWebView?.GoBack();
                    break;

                case MessageCode.Reload:
                    ActiveWebView?.Reload();
                    break;

                case MessageCode.Cancel:
                    _ = ActiveWebView?.CallDevToolsProtocolMethodAsync("Page.stopLoading", "{}");
                    break;

                case MessageCode.SwitchTab:
                    SwitchToTab(args.Int32("tabId"));
                    break;

                case MessageCode.CloseTab:
                    CloseTab(args.Int32("tabId"));
                    break;

                case MessageCode.CloseWindow:
                    DestroyWindow(_hwnd);
                    break;

                case MessageCode.ShowOptions:
                    if (_optionsController is not null)
                    {
                        _optionsController.IsVisible = true;
                        _optionsController.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    }

                    break;

                case MessageCode.HideOptions:
                    if (_optionsController is not null)
                    {
                        _optionsController.IsVisible = false;
                    }

                    break;

                case MessageCode.OptionSelected:
                    ActiveTab?.Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    break;

                case MessageCode.GetFavorites:
                case MessageCode.GetSettings:
                case MessageCode.GetHistory:
                    ForwardControlResponseToTab(json, args);
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
        }
    }

    private void CreateTab(int tabId, bool shouldBeActive)
    {
        if (_contentEnvironment is null)
        {
            return;
        }

        BrowserTab tab = new(this, _hwnd, tabId);
        if (_tabs.Remove(tabId, out BrowserTab? oldTab))
        {
            oldTab.Close();
        }

        _tabs.Add(tabId, tab);
        _ = tab.InitializeAsync(_contentEnvironment, shouldBeActive);
    }

    private void NavigateActiveTab(JsonObject args)
    {
        CoreWebView2? webView = ActiveWebView;
        if (webView is null)
        {
            return;
        }

        string uri = args.String("uri");
        const string browserScheme = "browser://";

        if (uri.StartsWith(browserScheme, StringComparison.OrdinalIgnoreCase))
        {
            string page = uri[browserScheme.Length..];
            if (page is "favorites" or "settings" or "history")
            {
                string filePath = GetFullPathFor($@"wvbrowser_ui\content_ui\{page}.html");
                webView.Navigate(GetFilePathAsUri(filePath));
            }

            return;
        }

        try
        {
            webView.Navigate(uri);
        }
        catch
        {
            webView.Navigate(args.String("encodedSearchURI"));
        }
    }

    private void ForwardControlResponseToTab(JsonObject json, JsonObject args)
    {
        int tabId = args.Int32("tabId");
        args.Remove("tabId");

        if (_tabs.TryGetValue(tabId, out BrowserTab? tab))
        {
            PostJsonToWebView(json, tab.WebView);
        }
    }

    internal void HandleTabUriUpdate(int tabId, CoreWebView2 webView)
    {
        JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.UpdateUri);
        JsonObject args = message.Args();
        string source = webView.Source;

        args["tabId"] = tabId;
        args["uri"] = source;

        if (string.Equals(source, GetBrowserPageUri("favorites"), StringComparison.OrdinalIgnoreCase))
        {
            args["uriToShow"] = "browser://favorites";
        }
        else if (string.Equals(source, GetBrowserPageUri("settings"), StringComparison.OrdinalIgnoreCase))
        {
            args["uriToShow"] = "browser://settings";
        }
        else if (string.Equals(source, GetBrowserPageUri("history"), StringComparison.OrdinalIgnoreCase))
        {
            args["uriToShow"] = "browser://history";
        }

        PostJsonToWebView(message, _controlsWebView);
    }

    internal void HandleTabHistoryUpdate(int tabId, CoreWebView2 webView)
    {
        JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.UpdateUri);
        JsonObject args = message.Args();

        args["tabId"] = tabId;
        args["uri"] = webView.Source;
        args["canGoForward"] = webView.CanGoForward;
        args["canGoBack"] = webView.CanGoBack;

        PostJsonToWebView(message, _controlsWebView);
    }

    internal void HandleTabNavigationStarting(int tabId, CoreWebView2 webView)
    {
        JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.NavigationStarting);
        message.Args()["tabId"] = tabId;
        PostJsonToWebView(message, _controlsWebView);
    }

    internal void HandleTabNavigationCompleted(
        int tabId,
        CoreWebView2 webView,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        _ = UpdateTitleAsync(tabId, webView);
        _ = UpdateFaviconAsync(tabId, webView);

        JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.NavigationCompleted);
        JsonObject args = message.Args();
        args["tabId"] = tabId;
        args["isError"] = !eventArgs.IsSuccess;

        PostJsonToWebView(message, _controlsWebView);
    }

    internal void HandleTabSecurityUpdate(
        int tabId,
        CoreWebView2 webView,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs eventArgs)
    {
        JsonObject securityEvent = JsonNodeExtensions.ParseObject(eventArgs.ParameterObjectAsJson);
        JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.SecurityUpdate);
        JsonObject args = message.Args();

        args["tabId"] = tabId;
        args["state"] = securityEvent["securityState"]?.DeepClone() ?? "unknown";

        PostJsonToWebView(message, _controlsWebView);
    }

    internal void HandleTabCreated(int tabId, bool shouldBeActive)
    {
        if (shouldBeActive)
        {
            SwitchToTab(tabId);
        }
    }

    internal void HandleTabMessageReceived(
        int tabId,
        CoreWebView2 webView,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            JsonObject json = JsonNodeExtensions.ParseObject(eventArgs.WebMessageAsJson);
            JsonObject args = json.Args();
            string source = webView.Source;

            switch (json.Message())
            {
                case MessageCode.GetFavorites:
                case MessageCode.RemoveFavorite:
                    ForwardTabRequestToControls(tabId, json, args, source, GetBrowserPageUri("favorites"));
                    break;

                case MessageCode.GetSettings:
                    ForwardTabRequestToControls(tabId, json, args, source, GetBrowserPageUri("settings"));
                    break;

                case MessageCode.ClearCache:
                    if (IsBrowserPage(source, "settings"))
                    {
                        _ = ClearCacheAsync(tabId, json, args);
                    }

                    break;

                case MessageCode.ClearCookies:
                    if (IsBrowserPage(source, "settings"))
                    {
                        _ = ClearCookiesAsync(tabId, json, args);
                    }

                    break;

                case MessageCode.GetHistory:
                case MessageCode.RemoveHistoryItem:
                case MessageCode.ClearHistory:
                    ForwardTabRequestToControls(tabId, json, args, source, GetBrowserPageUri("history"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
        }
    }

    private void ForwardTabRequestToControls(
        int tabId,
        JsonObject json,
        JsonObject args,
        string source,
        string expectedSource)
    {
        if (!string.Equals(source, expectedSource, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args["tabId"] = tabId;
        PostJsonToWebView(json, _controlsWebView);
    }

    private async Task ClearCacheAsync(int tabId, JsonObject json, JsonObject args)
    {
        args["content"] = await TryDevToolsCallAsync(ActiveWebView, "Network.clearBrowserCache");
        args["controls"] = await TryDevToolsCallAsync(_controlsWebView, "Network.clearBrowserCache");
        PostJsonToTab(tabId, json);
    }

    private async Task ClearCookiesAsync(int tabId, JsonObject json, JsonObject args)
    {
        args["content"] = await TryDevToolsCallAsync(ActiveWebView, "Network.clearBrowserCookies");
        args["controls"] = await TryDevToolsCallAsync(_controlsWebView, "Network.clearBrowserCookies");
        PostJsonToTab(tabId, json);
    }

    private static async Task<bool> TryDevToolsCallAsync(CoreWebView2? webView, string methodName)
    {
        if (webView is null)
        {
            return false;
        }

        try
        {
            await webView.CallDevToolsProtocolMethodAsync(methodName, "{}");
            return true;
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
            return false;
        }
    }

    private async Task UpdateTitleAsync(int tabId, CoreWebView2 webView)
    {
        const string getTitleScript =
            "(() => {" +
            "const titleTag = document.getElementsByTagName('title')[0];" +
            "if (titleTag) { return titleTag.innerHTML; }" +
            "const pathname = window.location.pathname;" +
            "const filename = pathname.split('/').pop();" +
            "if (filename) { return filename; }" +
            "const hostname = window.location.hostname;" +
            "if (hostname) { return hostname; }" +
            "return '';" +
            "})();";

        try
        {
            string result = await webView.ExecuteScriptAsync(getTitleScript);
            JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.UpdateTab);
            JsonObject args = message.Args();
            args["title"] = JsonNode.Parse(result) ?? "";
            args["tabId"] = tabId;
            PostJsonToWebView(message, _controlsWebView);
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
        }
    }

    private async Task UpdateFaviconAsync(int tabId, CoreWebView2 webView)
    {
        const string getFaviconScript =
            "(() => {" +
            "let faviconURI = '';" +
            "let links = document.getElementsByTagName('link');" +
            "Array.from(links).map(element => {" +
            "let rel = element.rel;" +
            "if (rel && (rel == 'shortcut icon' || rel == 'icon')) {" +
            "if (!element.href) { return; }" +
            "try { faviconURI = new URL(element.href).href; }" +
            "catch(e) {" +
            "let faviconLocation = `${window.location.origin}/${element.href}`;" +
            "try { faviconURI = new URL(faviconLocation).href; } catch (e2) { return; }" +
            "}" +
            "}" +
            "});" +
            "return faviconURI;" +
            "})();";

        try
        {
            string result = await webView.ExecuteScriptAsync(getFaviconScript);
            JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.UpdateFavicon);
            JsonObject args = message.Args();
            args["uri"] = JsonNode.Parse(result) ?? "";
            args["tabId"] = tabId;
            PostJsonToWebView(message, _controlsWebView);
        }
        catch (Exception ex)
        {
            Program.LogFailure(ex.ToString());
        }
    }

    private void SwitchToTab(int tabId)
    {
        if (!_tabs.TryGetValue(tabId, out BrowserTab? tab) || tab.Controller is null)
        {
            return;
        }

        int previousActiveTab = _activeTabId;
        tab.Resize();
        tab.Controller.IsVisible = true;
        _activeTabId = tabId;

        if (previousActiveTab != MessageCode.InvalidTabId &&
            previousActiveTab != _activeTabId &&
            _tabs.TryGetValue(previousActiveTab, out BrowserTab? previousTab) &&
            previousTab.Controller is not null)
        {
            try
            {
                previousTab.Controller.IsVisible = false;
            }
            catch (COMException)
            {
                JsonObject message = JsonNodeExtensions.CreateMessage(MessageCode.CloseTab);
                message.Args()["tabId"] = previousActiveTab;
                PostJsonToWebView(message, _controlsWebView);
            }
        }
    }

    private void CloseTab(int tabId)
    {
        if (_tabs.Remove(tabId, out BrowserTab? tab))
        {
            tab.Close();
        }

        if (_activeTabId == tabId)
        {
            _activeTabId = MessageCode.InvalidTabId;
        }
    }

    private void ResizeAllWebViews()
    {
        ResizeUiWebViews();
        ActiveTab?.Resize();
    }

    private void ResizeUiWebViews()
    {
        if (!GetClientRect(_hwnd, out RECT bounds))
        {
            return;
        }

        if (_controlsController is not null)
        {
            RECT controlsBounds = bounds;
            controlsBounds.bottom = controlsBounds.top + GetDpiAwareBound(UiBarHeight) + 1;
            _controlsController.Bounds = ToFoundationRect(controlsBounds);
        }

        if (_optionsController is not null)
        {
            RECT optionsBounds = bounds;
            optionsBounds.top = GetDpiAwareBound(UiBarHeight);
            optionsBounds.bottom = optionsBounds.top + GetDpiAwareBound(OptionsDropdownHeight);
            optionsBounds.left = optionsBounds.right - GetDpiAwareBound(OptionsDropdownWidth);
            _optionsController.Bounds = ToFoundationRect(optionsBounds);
        }
    }

    private void UpdateMinWindowSize()
    {
        if (!GetClientRect(_hwnd, out RECT clientRect) ||
            !GetWindowRect(_hwnd, out RECT windowRect))
        {
            return;
        }

        int bordersWidth = (windowRect.right - windowRect.left) - clientRect.right;
        int bordersHeight = (windowRect.bottom - windowRect.top) - clientRect.bottom;

        _minWindowWidth = GetDpiAwareBound(MinWindowWidth) + bordersWidth;
        _minWindowHeight = GetDpiAwareBound(MinWindowHeight) + bordersHeight;
    }

    internal int GetDpiAwareBound(int bound)
    {
        uint dpi = _hwnd == HWND.Null ? DefaultDpi : GetDpiForWindow(_hwnd);
        return (int)(bound * dpi / DefaultDpi);
    }

    internal static global::Windows.Foundation.Rect ToFoundationRect(RECT bounds)
    {
        return new global::Windows.Foundation.Rect
        {
            X = bounds.left,
            Y = bounds.top,
            Width = bounds.right - bounds.left,
            Height = bounds.bottom - bounds.top,
        };
    }

    private static void PostJsonToWebView(JsonObject message, CoreWebView2? webView)
    {
        webView?.PostWebMessageAsJson(message.ToJsonString());
    }

    private void PostJsonToTab(int tabId, JsonObject message)
    {
        if (_tabs.TryGetValue(tabId, out BrowserTab? tab))
        {
            PostJsonToWebView(message, tab.WebView);
        }
    }

    private string GetBrowserPageUri(string page)
    {
        return GetFilePathAsUri(GetFullPathFor($@"wvbrowser_ui\content_ui\{page}.html"));
    }

    private bool IsBrowserPage(string uri, string page)
    {
        return string.Equals(uri, GetBrowserPageUri(page), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFullPathFor(string relativePath)
    {
        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }

    private static string GetFilePathAsUri(string fullPath)
    {
        return new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;
    }

    private BrowserTab? ActiveTab =>
        _tabs.TryGetValue(_activeTabId, out BrowserTab? tab) ? tab : null;

    private CoreWebView2? ActiveWebView => ActiveTab?.WebView;

    internal void ShowError(string message)
    {
        MessageBox(
            HWND.Null,
            message,
            WindowTitle,
            MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR);
    }

    public void Dispose()
    {
        foreach (BrowserTab tab in _tabs.Values)
        {
            tab.Close();
        }

        _tabs.Clear();
        _optionsController?.Close();
        _controlsController?.Close();
        _optionsController = null;
        _controlsController = null;
        _optionsWebView = null;
        _controlsWebView = null;
        _uiEnvironment = null;
        _contentEnvironment = null;
    }
}
