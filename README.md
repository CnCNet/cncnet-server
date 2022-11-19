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

![Screenshot 2022-09-24 155610](https://user-images.githubusercontent.com/25006126/192101835-c2e392f7-2e37-4373-a415-918c10ee772f.png)

### Start from console

`cncnet-server --name NewServer`

### Install as a service on Windows (using PowerShell)

`New-Service -BinaryPathName '"C:\cncnet-server\cncnet-server.exe --name NewServer"' -StartupType "Automatic"`

### Install as a service on Linux (Ubuntu example)

`sudo apt-get update && \
  sudo apt-get install -y aspnetcore-runtime-7.0`

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