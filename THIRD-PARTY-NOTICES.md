# Third-party notices

C.A.R.S. Control is licensed under the [MIT License](LICENSE). It includes, unmodified, the components below, each under its own
licence. This file ships with every installed copy (`C:\Program Files\CarsControl\THIRD-PARTY-NOTICES.md`, also reachable from
Settings › About & updates › Licenses).

The payload's `licenses` folder contains full MIT, Apache-2.0 and MPL-2.0 texts, package-supplied licence/notice files,
and `PACKAGE-NOTICES.txt` generated from the exact restored runtime packages, including source repository commits and
copyright declarations where supplied. This inventory is regenerated when packaging, so it follows dependency updates.

## Shipped with C.A.R.S. Control

| Component | Version | Licence | Source |
|---|---|---|---|
| WPF-UI, WPF-UI.Tray, WPF-UI.Abstractions | 4.3.0 | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | MIT | https://github.com/CommunityToolkit/WindowsCommunityToolkit |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.12 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyInjection (+ .Abstractions) | 10.0.12 | MIT | https://github.com/dotnet/runtime |
| System.Management, System.IO.Ports | 10.0.2 / 10.0.3 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Windows.CsWinRT (WinRT.Runtime, Microsoft.Windows.SDK.NET) | 2.2.0 | MIT | https://github.com/microsoft/CsWinRT |
| QRCoder | 1.6.0 | MIT | https://github.com/codebude/QRCoder |
| SQLitePCLRaw (core, provider, bundle, lib.e_sqlite3) | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (inside e_sqlite3.dll) | | Public domain | https://www.sqlite.org/copyright.html |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/v0.9.6 |
| DiskInfoToolkit | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit |
| RAMSPDToolkit-NDD | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit |
| BlackSharp.Core | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp |
| HidSharp | 2.6.4 | Apache-2.0 | https://software.seekye.com/hidsharp |
| Mono.Posix.NETStandard | 1.0.0 | MIT | https://github.com/mono/mono |
| `Microsoft.Management.Deployment.winmd` (Windows Package Manager COM metadata) | | MIT | https://github.com/microsoft/winget-cli |

**About the MPL-2.0 components** (LibreHardwareMonitorLib, DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core): they are used
as separate, unmodified DLLs. Their source code is available at the links above, at the versions listed. MPL-2.0 is a file-level
licence: it doesn't extend to C.A.R.S. Control's own code.

**Apache-2.0 components** (SQLitePCLRaw, HidSharp): licence text at https://www.apache.org/licenses/LICENSE-2.0. HidSharp:
Copyright 2010-2025 James F. Bellinger.

## Used at build time only (not shipped)

| Component | Licence |
|---|---|
| .NET SDK and runtime | MIT |
| Microsoft.NETFramework.ReferenceAssemblies (builds the installer) | MIT (Microsoft .NET Library licence for the reference assemblies) |
| xUnit (tests) | Apache-2.0 / MIT |

## Downloaded only at your request (not shipped)

| Component | What | Where it comes from |
|---|---|---|
| .NET 10 Desktop Runtime | Installed by Setup when it's missing | Microsoft (`builds.dotnet.microsoft.com`), SHA-512 checked |
| Intel PresentMon 2.6.0 | Records frame times for Optimization › Test it on this PC; kept in `C:\ProgramData\CarsControl\tools` | Its official release (https://github.com/GameTechDev/PresentMon), size and SHA-256 pinned and checked before every run; MIT licence |
| PawnIO 2.2.0 | The sensor driver, installed from Health › Sensors | Its official release (https://github.com/namazso/PawnIO.Setup), SHA-256 pinned; see its project for its licence |
| WinDbg | Microsoft's debugger, installed from Health › Crashes | winget (`Microsoft.WinDbg`), Microsoft's licence terms |
| Debug symbols | Downloaded by WinDbg when analysing a crash | Microsoft's symbol server |
| WizTree (optional, never installed by C.A.R.S.) | Used for fast scans if you have it | https://diskanalyzer.com/, its own licence |

The full licence texts for MIT, Apache-2.0 and MPL-2.0 are at https://opensource.org/license/mit,
https://www.apache.org/licenses/LICENSE-2.0 and https://www.mozilla.org/MPL/2.0/.
