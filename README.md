# cncnet-server

* .NET7
* Cross platform (Windows, Linux, Mac, ...)
* No admin privileges required to run
* Supports CnCNet V2 & V3 tunnel protocol

## Versions

* OS specific versions `win-x64` etc.: Use for best performance.
* Cross platform version `any`: Runs on all supported .NET platforms.

## How to run/install

Requires the [.NET Runtime 7 and ASP.NET Core Runtime 7](https://dotnet.microsoft.com/en-us/download/dotnet/7.0/runtime).

Make sure these ports are open/forwarded to the machine (default ports):

* TCP 50000
* UDP 50000
* UDP 50001
* UDP 3478
* UDP 8054

### Arguments

Run `cncnet-server -?` to see a list of possible arguments.

Example:

```
Description:
  CnCNet tunnel server

Usage:
  cncnet-server [options]

Options:
  --n, --name <name> (REQUIRED)                            Name of the server
  --p, --tunnelport <tunnelport>                           Port used for the V3 tunnel server [default: 50001]
  --p2, --tunnelv2port <tunnelv2port>                      Port used for the V2 tunnel server [default: 50000]
  --m, --maxclients <maxclients>                           Maximum clients allowed on the tunnel server [default: 200]
  --nm, --nomasterannounce                                 Don't register to master [default: False]
  --masp, --masterpassword <masterpassword>                Master password []
  --maintenancepassword, --maip <maintenancepassword>      Maintenance password []
  --masterserverurl, --mu <masterserverurl>                Master server URL [default:
                                                           https://cncnet.org/master-announce]
  --i, --iplimit <iplimit>                                 Maximum clients allowed per IP address [default: 8]
  --nopeertopeer, --np                                     Disable STUN NAT traversal server (UDP 8054 & 3478)
                                                           [default: False]
  --3, --tunnelv3enabled                                   Start a V3 tunnel server [default: True]
  --2, --tunnelv2enabled                                   Start a V2 tunnel server [default: True]
  --sel, --serverloglevel                                  CnCNet server messages log level [default: Information]
  <Critical|Debug|Error|Information|None|Trace|Warning>
  --syl, --systemloglevel                                  Low level system messages log level [default: Warning]
  <Critical|Debug|Error|Information|None|Trace|Warning>
  --6, --announceipv6                                      Announce IPv6 address to master server [default: True]
  --4, --announceipv4                                      Announce IPv4 address to master server [default: True]
  --h, --tunnelv2https                                     Use https Tunnel V2 web server [default: False]
  --maxpacketsize, --mps <maxpacketsize>                   Maximum accepted packet size [default: 2048]
  --maxpingsglobal, --mpg <maxpingsglobal>                 Maximum accepted ping requests globally [default: 1024]
  --maxpingsperip, --mpi <maxpingsperip>                   Maximum accepted ping requests per IP [default: 20]
  --ai, --masterannounceinterval <masterannounceinterval>  Master server announce interval in seconds [default: 60]
  --c, --clienttimeout <clienttimeout>                     Client timeout in seconds [default: 60]
  --version                                                Show version information
  -?, -h, --help                                           Show help and usage information
```

### Start from console

```
cncnet-server --name NewServer
```

### Install as a service on Windows (using PowerShell)

```
Download <cncnet-server-win-x64.zip>
```

```
Extract to e.g. C:\cncnet-server\
```

```
New-Service -Name CnCNetServer -BinaryPathName '"C:\cncnet-server\cncnet-server.exe" --name "NewServer"' -StartupType "Automatic" -DisplayName "CnCNet Tunnel Server" -Description "CnCNet Tunnel Server"
```

```
Start-Service CnCNetServer
```

### Install as a service on Linux (Ubuntu example)

```
sudo apt-get update && \
  sudo apt-get install -y aspnetcore-runtime-7.0
```

```
wget <cncnet-server-linux-x64.zip>
```

```
unzip -d cncnet-server <cncnet-server-linux-x64.zip>
```

```
useradd cncnet-server
```

```
passwd cncnet-server
```

```
chown cncnet-server -R /home/cncnet-server
```

```
cd /home/cncnet-server/
```

```
chmod +x cncnet-server
```

```
cd /etc/systemd/system/
```

```
vi cncnet-server.service
```
cncnet-server.service example contents:

```
[Unit]
Description=CnCNet Tunnel Server

[Service]
Type=notify
WorkingDirectory=/home/cncnet-server
ExecStart=/home/cncnet-server/cncnet-server --n "NewServer" --masp "PW" --maip "PW" --m 250
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

```
sudo systemctl daemon-reload
```

```
sudo systemctl start cncnet-server.service
```

```
sudo ufw allow proto tcp from any to any port 50000
```

```
sudo ufw allow proto udp from any to any port 50000
```

```
sudo ufw allow proto udp from any to any port 50001
```

```
sudo ufw allow proto udp from any to any port 3478
```

```
sudo ufw allow proto udp from any to any port 8054
```

to start on machine start:
```
sudo systemctl enable cncnet-server.service
```

to inspect logs:
```
sudo journalctl -u cncnet-server
```
