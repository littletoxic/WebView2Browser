# WebView2Browser C# NativeAOT

This project is a C# NativeAOT translation of Microsoft's WebView2Browser sample.

It keeps the original WebView2 browser UI assets and ports the Win32 host, tab management, WebView2 messaging, and browser data isolation code to C#.

## Build

```powershell
dotnet build WebView2Browser.slnx -c Release
dotnet publish WebView2Browser.csproj -c Release
```
