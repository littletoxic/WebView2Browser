# WebView2Browser C# NativeAOT

This project is a C# NativeAOT translation of Microsoft's WebView2Browser sample.

Original sample: https://github.com/MicrosoftEdge/WebView2Browser

It keeps the original WebView2 browser UI assets and ports the Win32 host, tab management, WebView2 messaging, and browser data isolation code to C#.

## Build

```powershell
dotnet build WebView2Browser.slnx -c Release
dotnet publish WebView2Browser.csproj -c Release -r win-x64
```

## Deployment

The published app includes `WebView2Loader.dll`, but it does not include the Microsoft Edge WebView2 Runtime. Install the Evergreen WebView2 Runtime on clean machines such as Windows Sandbox before launching the app, or publish a package that carries a Fixed Version Runtime and pass that runtime folder to WebView2 environment creation.
