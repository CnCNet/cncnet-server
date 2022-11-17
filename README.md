# cncnet-server

* .NET7
* Cross platform (Windows, Linux, Mac, ...)
* No admin privileges required to run
* Supports CnCNet V2 & V3 tunnel protocol

## Versions

* The cross platform version ('any') is recommended for low maintenance.
This requires the .NET Runtime 7 and ASP.NET Core Runtime 7 to be installed seperately. Security and other updates to the .NET runtimes are then usually handled automatically by the OS.

* OS specific versions can be expected to have better performance and are contained in a single file. This self contained executable contains all the required .NET runtimes.
Updating the contained runtimes requires releasing a new version of the CnCNet server.

## How to run/install

Make sure these ports are open/forwarded to the machine (default ports):

* TCP 50000
* UDP 50000
* UDP 50001
* UDP 3478
* UDP 8054

### Arguments

Run `cncnet-server -?` to see a list of possible arguments.

Example:

![Screenshot 2022-09-24 155610](https://user-images.githubusercontent.com/25006126/192101835-c2e392f7-2e37-4373-a415-918c10ee772f.png)

### Start from console

`cncnet-server --name NewServer`

### Install as a service on Windows (using PowerShell)

`New-Service -BinaryPathName '"C:\cncnet-server\cncnet-server.exe --name NewServer"' -StartupType "Automatic"`

### Install as a service on Linux (Ubuntu example)

`sudo apt-get update && \
  sudo apt-get install -y aspnetcore-runtime-6.0`

`wget <cncnet-server.zip>`

`unzip -d cncnet-server <cncnet-server.zip>`

`useradd cncnet-server`

`passwd cncnet-server`

`chown cncnet-server -R /home/cncnet-server`

`chmod +x /home/cncnet-server/cncnet-server.dll`

`cd /etc/systemd/system#`

`vi cncnet-server.service` :

```
[Unit]
Description=CnCNet Tunnel Server

[Service]
Type=notify
WorkingDirectory=/home/cncnet-server
ExecStart=/usr/bin/dotnet /home/cncnet-server/cncnet-server.dll -- name "NewServer"
SyslogIdentifier=CnCNet-Server
User=cncnet-server
Restart=always
RestartSec=5

KillSignal=SIGINT
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

[Install]
WantedBy=multi-user.target
```

`sudo systemctl daemon-reload`

`sudo systemctl start cncnet-server.service`

`sudo ufw allow proto tcp from any to any port 50000`

`sudo ufw allow proto udp from any to any port 50000`

`sudo ufw allow proto udp from any to any port 50001`

`sudo ufw allow proto udp from any to any port 3478`

`sudo ufw allow proto udp from any to any port 8054`

to start on machine start:
`sudo systemctl enable cncnet-server.service`

to inspect logs:
`sudo journalctl -u cncnet-server`

## How to build/publish

### Non self contained executable (requires .NET Runtime 7 and ASP.NET Core Runtime 7 to be installed)

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

#### Optional release parameters

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

#### Example combination:

`dotnet publish -c Release -r win-x64 -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded`
