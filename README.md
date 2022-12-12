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
  --name <name> (REQUIRED)                                Name of the server
  --port, --tunnelport <tunnelport>                       Port used for the V3 tunnel server [default: 50001]
  --portv2, --tunnelv2port <tunnelv2port>                 Port used for the V2 tunnel server [default: 50000]
  --maxclients <maxclients>                               Maximum clients allowed on the tunnel server [default: 200]
  --nomaster, --nomasterannounce                          Don't register to master [default: False]
  --masterpassword, --masterpw <masterpassword>           Master password []
  --maintenancepassword, --maintpw <maintenancepassword>  Maintenance password []
  --master, --masterserverurl <masterserverurl>           Master server URL [default:
                                                          https://cncnet.org/master-announce]
  --iplimit <iplimit>                                     Maximum clients allowed per IP address [default: 8]
  --nop2p, --nopeertopeer                                 Disable NAT traversal ports (8054, 3478 UDP) [default: False]
  --tunnelv3, --tunnelv3enabled                           Start a V3 tunnel server [default: True]
  --tunnelv2, --tunnelv2enabled                           Start a V2 tunnel server [default: True]
  --serverloglevel                                        CnCNet server messages log level [default: Information]
  <Critical|Debug|Error|Information|None|Trace|Warning>
  --systemloglevel                                        Low level system messages log level [default: Warning]
  <Critical|Debug|Error|Information|None|Trace|Warning>
  --announceipv6, --ipv6                                  Announce IPv6 address to master server [default: False]
  --announceipv4, --ipv4                                  Announce IPv4 address to master server [default: True]
  --https, --tunnelv2https                                Use https Tunnel V2 web server [default: False]
  --version                                               Show version information
  -?, -h, --help                                          Show help and usage information
```

### Start from console

```
cncnet-server --name NewServer
```

### Install as a service on Windows (using PowerShell)

```
New-Service -BinaryPathName '"C:\cncnet-server\cncnet-server.exe --name NewServer"' -StartupType "Automatic"
```

### Install as a service on Linux (Ubuntu example)

```
sudo apt-get update && \
  sudo apt-get install -y aspnetcore-runtime-7.0
```

```
wget <cncnet-server.zip>
```

```
unzip -d cncnet-server <cncnet-server.zip>
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
chmod +x cncnet-server.dll
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
ExecStart=/usr/bin/dotnet /home/cncnet-server/cncnet-server.dll --name "NewServer" --masterpassword "PW" --maintpw "PW" --maxclients 500
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
