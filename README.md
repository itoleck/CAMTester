# CAMTester

**CAMTester** is a lightweight `.NET 10` Windows desktop application (WPF) that lists every available camera input on your PC and displays a live preview when you click any camera.

It supports both:
- **Hardware & multi-sensor cameras** (USB webcams, built-in cameras, capture cards, IR/depth sensors) via Windows Media Foundation / WinRT.
- **Virtual & legacy cameras** (such as OBS Virtual Camera, DroidCam, ManyCam, and NDI) via DirectShow.

## Features

- Automatic discovery and deduplication of WinRT and DirectShow camera inputs
- Live hot-plug detection when cameras are connected or disconnected
- Click-to-preview video viewport with live resolution, FPS, pixel format, and mode telemetry
- Resolution and frame rate selector for the active camera
- Horizontal mirror toggle and PNG snapshot capture
- Zero third-party NuGet dependencies

## Requirements

- Windows 10 (version 2004 / build 19041 or later) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## How to Compile and Run

Open a terminal in the project folder and run:

```powershell
dotnet build
```

To compile and launch the app in one step:

```powershell
dotnet run
```

To build a release executable:

```powershell
dotnet build -c Release
```

The compiled executable is output to:

```text
bin\Release\net10.0-windows10.0.19041.0\CAMTester.exe
```

## License

This project is licensed under the MIT License — see [LICENSE.md](LICENSE.md) for details.
