# cncnet-server

* .NET6
* Cross platform (Windows, Linux, Mac, ...)
* No admin privileges required to run
* Compatible with currently distributed version

## How to run/install

Example startup command from console:

`cncnet-server --name NewServer`

Install as Windows service using PowerShell:

`New-Service -BinaryPathName '"C:\cncnet-server\cncnet-server.exe --name NewServer"' -StartupType "Automatic"`

## How to build and release

### Non self contained executable (requires .NET Runtime 6 and ASP.NET Core Runtime 6 to be installed)

Create 1 release for all platforms

`dotnet publish -c Release`
<1MB

### Self contained executable (.NET6 runtimes included)

Create release for Windows 7 and up 64bit

`dotnet publish -c Release -r win-x64`
~90MB

Create release for Windows 10 and up 64bit

`dotnet publish -c Release -r win10-x64`

Create release for Windows 10 and up arm64

`dotnet publish -c Release -r win10-arm64`

Create release for Linux 64bit

`dotnet publish -c Release -r linux-x64`

Create release for macOS 64bit

`dotnet publish -c Release -r osx-x64`

### Optional release parameters

`-p:PublishSingleFile=true`
'single' file, produces only 2 files (+symbols file)
~85MB

` -p:DebugType=embedded`
'single' file, produces only 2 files (symbols file embedded in executable)

`-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`
'single' compressed file, smaller size, longer startup time
~44MB

`-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
'single' compressed file, smaller size, longest startup time, produces only 1 file (+symbols file)
Some Linux environments will need to have $DOTNET_BUNDLE_EXTRACT_BASE_DIR specified explicitly

`-p:PublishReadyToRun=true`
Shortest startup time

`-p:PublishReadyToRunComposite=true`
Best runtime performance

Example combination:

`dotnet publish -c Release -r win-x64 -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded`