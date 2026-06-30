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

The published app includes `WebView2Loader.dll`, but it does not include the Microsoft Edge WebView2 Runtime.

If the app starts without an installed WebView2 Runtime, it prompts the user to download Microsoft's Evergreen Bootstrapper from `https://go.microsoft.com/fwlink/p/?LinkId=2124703`, saves it to the user's temporary directory, runs it with `/silent /install`, and retries WebView2 initialization. This requires internet access during first launch.

For fully self-contained browser binaries instead of installing the shared Evergreen Runtime, publish a package that carries a Fixed Version Runtime and pass that runtime folder to WebView2 environment creation.
