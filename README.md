# HappyDiscard

HappyDiscard is a lightweight asynchronous TCP and optional UDP discard server written in C# and .NET 10.

It implements the classic Discard Protocol: every byte received from a client is accepted and thrown away. Discarded content is never decoded, stored, logged, echoed, or published to telemetry.

## Features

* Asynchronous TCP connections
* IPv6 and dual-stack TCP listening
* Optional UDP discard service
* Configurable address and port
* Concurrent connection limit
* Connection timeout
* Maximum bytes per connection
* Pooled network buffers
* Graceful shutdown
* Mission Control lifecycle and UDP datagram telemetry
* Production Docker support
* Windows Service support
* Structured logging

# Try it live

Connect to TCP port 9 at `discard.kgivler.com` and send bytes.

## Requirements

To build HappyDiscard:

* .NET 10 SDK

For the recommended Linux VPS deployment:

* Docker Engine with Compose
* A Linux VPS with permission to accept inbound TCP connections and, when UDP is enabled, UDP datagrams

The repository includes a `NuGet.config` and `local-nuget` package feed for the JoyfulReaperLib packages used by Docker builds. Keep those package files in sync with the versions referenced by `HappyDiscard/HappyDiscard.csproj`.

Current local package dependencies:

| Package                          |  Version |
| -------------------------------- | -------: |
| `JoyfulReaperLib`                | `0.0.11` |
| `JoyfulReaperLib.MissionControl` |  `0.0.3` |
| `JoyfulReaperLib.TcpServer`      |  `0.0.5` |

`JoyfulReaperLib.TcpServer` 0.0.5 adds the `DualMode` option support used by HappyDiscard.

## Build And Test

```bash
git clone https://github.com/JoyfulReaper/HappyDiscard.git
cd HappyDiscard

dotnet restore HappyDiscard.slnx --configfile NuGet.config
dotnet build HappyDiscard.slnx --configuration Release --no-restore
dotnet test HappyDiscard.slnx --configuration Release --no-build
```

Run it locally:

```bash
dotnet run --project HappyDiscard/HappyDiscard.csproj
```

## Configuration

HappyDiscard reads settings from the `Discard` configuration section.

```json
{
  "Discard": {
    "ListenAddress": "::",
    "DualMode": true,
    "Port": 9,
    "MaxConcurrentConnections": 64,
    "RequestTimeoutSeconds": 15,
    "MaxBytesPerConnection": 1048576,
    "TelemetryIgnoredRemoteAddress": null,
    "UdpEnabled": false,
    "UdpListenAddress": null,
    "UdpPort": null,
    "MaxUdpDatagramBytes": 65507
  },
  "MissionControl": {
    "Enabled": false,
    "BaseUrl": "http://localhost:5190",
    "ApiKey": "",
    "TimeoutMilliseconds": 1000
  }
}
```

| Setting                         |   Default | Description                                                                                                                  |
| ------------------------------- | --------: | ---------------------------------------------------------------------------------------------------------------------------- |
| `ListenAddress`                 |      `::` | Address used by the TCP listener. Use `127.0.0.1` for IPv4 loopback or `::1` for IPv6 loopback.                              |
| `DualMode`                      |    `true` | Enables IPv4 and IPv6 on TCP and UDP IPv6-any (`::`) listeners.                                                              |
| `Port`                          |       `9` | TCP listening port. Port 9 is the traditional Discard Protocol port.                                                         |
| `MaxConcurrentConnections`      |      `64` | Maximum number of simultaneous client connections.                                                                           |
| `RequestTimeoutSeconds`         |      `15` | Maximum lifetime of one connection.                                                                                          |
| `MaxBytesPerConnection`         | `1048576` | Maximum bytes accepted during one connection. The default is 1 MiB.                                                          |
| `TelemetryIgnoredRemoteAddress` |    `null` | Optional monitor IP whose TCP Discard sessions are processed normally but excluded from Mission Control lifecycle telemetry. |
| `UdpEnabled`                    |   `false` | Enables the optional UDP Discard listener. Keep it disabled unless explicitly needed.                                        |
| `UdpListenAddress`              |    `null` | UDP listening address. When unset, `ListenAddress` is used.                                                                  |
| `UdpPort`                       |    `null` | UDP listening port. When unset, `Port` is used.                                                                              |
| `MaxUdpDatagramBytes`           |   `65507` | Largest UDP datagram accepted; larger datagrams are dropped.                                                                 |

Settings can also be supplied through environment variables:

```bash
Discard__ListenAddress=0.0.0.0
Discard__DualMode=false
Discard__Port=9
Discard__MaxConcurrentConnections=64
Discard__RequestTimeoutSeconds=15
Discard__MaxBytesPerConnection=1048576
Discard__TelemetryIgnoredRemoteAddress=172.21.0.1
Discard__UdpEnabled=false

MissionControl__Enabled=true
MissionControl__BaseUrl=http://gateway:8080
MissionControl__ApiKey=replace-with-a-strong-random-key
MissionControl__TimeoutMilliseconds=1000
```

`TelemetryIgnoredRemoteAddress` suppresses TCP Mission Control session telemetry only. The TCP session is still accepted, discarded, timed out, byte-limited, and cleaned up normally. The comparison uses only the normalized remote IP address, not the source port, and IPv4-mapped IPv6 addresses are mapped to IPv4 before comparison. This is intended for Uptime Kuma or another trusted TCP monitor. Docker network gateway addresses vary by host and network, so verify the actual monitor source address before setting it.

When UDP is enabled, `UdpListenAddress` and `UdpPort` inherit `ListenAddress` and `Port` when left unset.

## Local Protocol Testing

Port 9 is the conventional production Discard port. Use the unprivileged high port `7009` for these local tests. Run the server command in one PowerShell window and the matching client command in another. No `netcat` installation or administrator privileges are needed.

The TCP client helper below connects, sends a small payload, and succeeds only when the server sends no bytes back during a short read window:

```powershell
function Test-TcpDiscard([string] $Address, [Net.Sockets.AddressFamily] $Family) {
    $client = [Net.Sockets.TcpClient]::new($Family)
    try {
        $client.Connect($Address, 7009)
        $stream = $client.GetStream()
        $payload = [Text.Encoding]::UTF8.GetBytes('discard me')
        $stream.Write($payload, 0, $payload.Length)
        $stream.ReadTimeout = 500
        try {
            $value = $stream.ReadByte()
            if ($value -ge 0) { throw "Unexpected response byte: $value" }
        }
        catch [IO.IOException] {
            Write-Host 'Success: TCP connection accepted and no response received.'
        }
    }
    finally {
        $client.Dispose()
    }
}
```

TCP IPv4 loopback server and client:

```powershell
$env:Discard__ListenAddress = '127.0.0.1'
$env:Discard__DualMode = 'false'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'false'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

```powershell
Test-TcpDiscard '127.0.0.1' ([Net.Sockets.AddressFamily]::InterNetwork)
```

TCP IPv6 loopback server and client:

```powershell
$env:Discard__ListenAddress = '::1'
$env:Discard__DualMode = 'false'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'false'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

```powershell
Test-TcpDiscard '::1' ([Net.Sockets.AddressFamily]::InterNetworkV6)
```

For one dual-stack TCP listener, use the IPv6-any address with dual mode enabled, then run both client commands:

```powershell
$env:Discard__ListenAddress = '::'
$env:Discard__DualMode = 'true'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'false'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

```powershell
Test-TcpDiscard '127.0.0.1' ([Net.Sockets.AddressFamily]::InterNetwork)
Test-TcpDiscard '::1' ([Net.Sockets.AddressFamily]::InterNetworkV6)
```

The UDP client helper sends one datagram and treats a receive timeout as the expected result:

```powershell
function Test-UdpDiscard([string] $Address, [Net.Sockets.AddressFamily] $Family) {
    $client = [Net.Sockets.UdpClient]::new($Family)
    try {
        $client.Connect($Address, 7009)
        $payload = [Text.Encoding]::UTF8.GetBytes('discard me')
        [void] $client.Send($payload, $payload.Length)
        $client.Client.ReceiveTimeout = 500

        $remote = if ($Family -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
            [Net.IPEndPoint]::new([Net.IPAddress]::IPv6Any, 0)
        } else {
            [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        }

        try {
            [void] $client.Receive([ref] $remote)
            throw 'Unexpected UDP response.'
        }
        catch [Net.Sockets.SocketException] {
            if ($_.Exception.SocketErrorCode -ne [Net.Sockets.SocketError]::TimedOut) {
                throw
            }

            Write-Host 'Success: UDP datagram sent and no response received.'
        }
    }
    finally {
        $client.Dispose()
    }
}
```

UDP IPv4 loopback server and client:

```powershell
$env:Discard__ListenAddress = '127.0.0.1'
$env:Discard__DualMode = 'false'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'true'
$env:Discard__UdpListenAddress = '127.0.0.1'
$env:Discard__UdpPort = '7009'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

```powershell
Test-UdpDiscard '127.0.0.1' ([Net.Sockets.AddressFamily]::InterNetwork)
```

UDP IPv6 loopback server and client:

```powershell
$env:Discard__ListenAddress = '::1'
$env:Discard__DualMode = 'false'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'true'
$env:Discard__UdpListenAddress = '::1'
$env:Discard__UdpPort = '7009'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

```powershell
Test-UdpDiscard '::1' ([Net.Sockets.AddressFamily]::InterNetworkV6)
```

For one dual-stack UDP listener, use the IPv6-any address and enable dual mode:

```powershell
$env:Discard__ListenAddress = '::'
$env:Discard__DualMode = 'true'
$env:Discard__Port = '7009'
$env:Discard__UdpEnabled = 'true'
$env:Discard__UdpListenAddress = '::'
$env:Discard__UdpPort = '7009'
dotnet run --project .\HappyDiscard\HappyDiscard.csproj
```

Then test both address families:

```powershell
Test-UdpDiscard '127.0.0.1' ([Net.Sockets.AddressFamily]::InterNetwork)
Test-UdpDiscard '::1' ([Net.Sockets.AddressFamily]::InterNetworkV6)
```

TCP Discard reads stream bytes until the client disconnects, the request timeout or byte limit is reached, or the server shuts down. It never writes protocol data back.

UDP Discard receives complete datagrams and never sends a response. UDP remains disabled by default and should remain disabled in production unless explicitly enabled.

Payload content is discarded and must never be published to telemetry.

## Mission Control Events

HappyDiscard publishes best-effort telemetry through `JoyfulReaperLib.MissionControl`. Telemetry failures are logged and do not prevent TCP or UDP Discard traffic from being processed.

Event types:

* `happydiscard.service.started`
* `happydiscard.discarding.started`
* `happydiscard.discarding.stopped`
* `happydiscard.udp.started`
* `happydiscard.udp.stopped`
* `happydiscard.udp.datagram.discarded`
* `happydiscard.udp.datagram.dropped`

### TCP Service Startup

`happydiscard.service.started` payload:

* `listenAddress`

### TCP Session Started

`happydiscard.discarding.started` payload:

* `remote`
* `requestTimeoutSeconds`
* `maxBytesPerConnection`

### TCP Session Stopped

`happydiscard.discarding.stopped` payload:

* `remote`
* `bytesDiscarded`
* `durationMilliseconds`
* `outcome`
* `succeeded`

TCP session outcomes:

| Outcome               | Succeeded | Meaning                                                              |
| --------------------- | --------- | -------------------------------------------------------------------- |
| `client-disconnected` | `true`    | The client completed or disconnected normally before the byte limit. |
| `byte-limit-reached`  | `true`    | HappyDiscard successfully enforced `MaxBytesPerConnection`.          |
| `timeout`             | `false`   | The per-connection timeout expired.                                  |
| `io-error`            | `false`   | An `IOException` ended the session.                                  |
| `socket-error`        | `false`   | A `SocketException` ended the session.                               |
| `server-shutdown`     | `false`   | The application stopping token ended the session.                    |
| `failed`              | `false`   | An unexpected exception ended the session.                           |

Started example:

```json
{
  "eventType": "happydiscard.discarding.started",
  "payload": {
    "remote": "203.0.113.10:54321",
    "requestTimeoutSeconds": 15,
    "maxBytesPerConnection": 1048576
  }
}
```

Stopped example:

```json
{
  "eventType": "happydiscard.discarding.stopped",
  "payload": {
    "remote": "203.0.113.10:54321",
    "bytesDiscarded": 21,
    "durationMilliseconds": 4,
    "outcome": "client-disconnected",
    "succeeded": true
  }
}
```

The two TCP events for one discard session share the same Mission Control correlation ID.

### UDP Telemetry

`happydiscard.udp.started` reports UDP listener startup and includes:

* `listenEndpoint`
* `maxDatagramBytes`

`happydiscard.udp.datagram.discarded` reports a successfully discarded datagram and includes:

* `remote`
* `bytesDiscarded`

`happydiscard.udp.datagram.dropped` reports a datagram rejected by the configured policy and includes:

* `remote`
* `bytesReceived`
* `reason`

The current drop reason is:

* `oversized`

`happydiscard.udp.stopped` reports UDP listener shutdown and includes:

* `listenEndpoint`
* `datagramsReceived`
* `datagramsDiscarded`
* `datagramsDropped`
* `bytesDiscarded`
* `durationMilliseconds`

Datagram payload content is never included in Mission Control telemetry.

UDP startup and per-datagram telemetry are best-effort and do not block the datagram receive loop. Slow or unavailable Mission Control publishing therefore does not prevent subsequent UDP datagrams from being discarded.

The final `happydiscard.udp.stopped` event is given a bounded opportunity to publish during shutdown.

## Docker

Build the image:

```bash
docker build --no-cache -t happy-discard .
```

The build should complete without `IL2xxx` Native AOT or trimming warnings from HappyDiscard or its dependencies.

The Dockerfile:

* Restores, tests, and publishes in a .NET SDK build stage.
* Publishes a Native AOT executable and serves as the release trim-analysis check.
* Uses the small .NET `runtime-deps` image for the final stage.
* Does not contain the full managed .NET runtime.
* Runs as `${APP_UID}`, not root.
* Listens internally on TCP port `9009`.
* Can be published externally as canonical Discard TCP port `9` with `9:9009`.
* Leaves UDP disabled by default.
* Does not embed configuration or secrets.

The Docker image defaults to:

```dockerfile
ENV Discard__ListenAddress=0.0.0.0
ENV Discard__DualMode=false
ENV Discard__Port=9009
```

Because `UdpEnabled` is not set by the image, UDP remains disabled by its application default.

Local unprivileged mapping:

```bash
docker run --rm -p 9009:9009/tcp happy-discard
```

Canonical public Discard mapping:

```bash
docker run --rm -p 9:9009/tcp happy-discard
```

Publishing host port 9 may require host-level privileges on Linux and macOS, while the process inside the container remains non-root and needs no added Linux capability.

To explicitly enable UDP in a container, configure it and publish the UDP port separately:

```bash
docker run --rm \
  -e Discard__UdpEnabled=true \
  -p 9009:9009/tcp \
  -p 9009:9009/udp \
  happy-discard
```

No Docker health check is currently defined. The current `runtime-deps` image does not include a TCP probing utility such as `nc`. Compose can still verify the service process state, and listener checks should be performed externally or from the host.

## Linux VPS Deployment With Docker Compose

Docker Compose is the recommended Linux deployment path.

The production example below intentionally keeps UDP disabled and exposes only TCP Discard.

### 1. Clone Or Update The Repository

```bash
cd /opt/joyful-stack

git clone https://github.com/JoyfulReaper/HappyDiscard.git HappyDiscard
```

For updates:

```bash
cd /opt/joyful-stack/HappyDiscard
git pull
```

### 2. Add The Mission Control Source Key

Add a source entry to the Mission Control gateway configuration:

```yaml
EventSources__Sources__9__Name: happydiscard-production
EventSources__Sources__9__ApiKey: ${HAPPYDISCARD_MISSION_CONTROL_KEY}
```

Add the required value to `/opt/joyful-stack/.env`:

```dotenv
HAPPYDISCARD_MISSION_CONTROL_KEY=replace-with-a-strong-random-key
```

Do not commit real secrets.

### 3. Add The Compose Service

Add this service to `/opt/joyful-stack/docker-compose.yaml`:

```yaml
happydiscard:
  build:
    context: ./HappyDiscard
    dockerfile: Dockerfile

  logging:
    driver: json-file
    options:
      max-size: "10m"
      max-file: "3"

  deploy:
    resources:
      limits:
        memory: 128M

  restart: unless-stopped
  init: true

  environment:
    DOTNET_ENVIRONMENT: Production

    Discard__ListenAddress: 0.0.0.0
    Discard__DualMode: "false"
    Discard__Port: 9009
    Discard__MaxConcurrentConnections: 64
    Discard__RequestTimeoutSeconds: 15
    Discard__MaxBytesPerConnection: 1048576
    Discard__TelemetryIgnoredRemoteAddress: "172.21.0.1"
    Discard__UdpEnabled: "false"

    MissionControl__Enabled: "true"
    MissionControl__BaseUrl: http://gateway:8080
    MissionControl__ApiKey: ${HAPPYDISCARD_MISSION_CONTROL_KEY}
    MissionControl__TimeoutMilliseconds: 1000

    DOTNET_EnableDiagnostics: "0"

  ports:
    - "9:9009/tcp"

  cap_drop:
    - ALL

  security_opt:
    - no-new-privileges:true

  depends_on:
    gateway:
      condition: service_healthy

  networks:
    - backend
```

If UDP is intentionally enabled in production, add:

```yaml
environment:
  Discard__UdpEnabled: "true"

ports:
  - "9:9009/tcp"
  - "9:9009/udp"
```

When UDP uses a different internal port, also configure `Discard__UdpPort` and publish the matching UDP container port.

### 4. Validate Compose

```bash
cd /opt/joyful-stack

docker compose config --quiet
```

### 5. Build HappyDiscard

```bash
docker compose build \
  --no-cache \
  --progress=plain \
  happydiscard
```

### 6. Stop The Old systemd Service

```bash
sudo systemctl disable --now happydiscard.service
```

### 7. Start The Container

```bash
docker compose up \
  -d \
  happydiscard
```

### 8. Verify The TCP Listener

```bash
docker compose ps happydiscard

docker compose logs \
  --tail=200 \
  happydiscard

sudo ss -ltnp | grep ':9 '
```

If UDP is enabled, also verify the UDP listener:

```bash
sudo ss -lunp | grep ':9 '
```

### 9. Test From An External Machine

TCP:

```bash
printf 'Hello from HappyDiscard\n' | nc -v your-vps-hostname 9
```

Expected behavior:

```text
Connection succeeds, no payload is returned, and the server closes when the client disconnects, times out, or reaches the configured byte limit.
```

When UDP is intentionally enabled:

```bash
printf 'Hello from HappyDiscard\n' | nc -u -w1 your-vps-hostname 9
```

Expected behavior:

```text
The datagram is accepted and no response is returned.
```

### 10. Confirm Mission Control Events

For a TCP session, Mission Control should contain a matching pair with the same correlation ID:

```text
happydiscard.discarding.started
happydiscard.discarding.stopped
```

When UDP is enabled, corresponding UDP activity may also produce:

```text
happydiscard.udp.started
happydiscard.udp.datagram.discarded
happydiscard.udp.datagram.dropped
happydiscard.udp.stopped
```

## Legacy systemd Rollback

Use this path only if Docker Compose needs to be rolled back.

Publish HappyDiscard:

```bash
dotnet publish HappyDiscard/HappyDiscard.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o publish
```

Install under `/opt/happydiscard`, run it as an unprivileged `happydiscard` user, and grant only `CAP_NET_BIND_SERVICE` so port 9 can bind without root.

Example service:

```ini
[Unit]
Description=HappyDiscard TCP Discard Server
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=happydiscard
Group=happydiscard
WorkingDirectory=/opt/happydiscard
ExecStart=/usr/bin/dotnet /opt/happydiscard/HappyDiscard.dll
EnvironmentFile=/etc/happydiscard/happydiscard.env
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
MemoryMax=128M

[Install]
WantedBy=multi-user.target
```

## Operational Notes

* HappyDiscard is a raw TCP service with an optional UDP Discard listener.
* It does not provide authentication or encryption.
* Public TCP and UDP ports will be scanned by automated systems.
* Keep connection, timeout, byte, firewall, and memory limits enabled.
* `RequestTimeoutSeconds` limits the total TCP connection lifetime. It does not reset after each payload.
* `MaxBytesPerConnection` limits the total bytes accepted by one TCP connection.
* `MaxUdpDatagramBytes` limits the size of a UDP datagram accepted for discard.
* UDP is disabled by default and should be enabled publicly only when explicitly desired.
* Port 9 is privileged on Linux. Publish host port 9 to container port 9009; do not run HappyDiscard as root and do not add `NET_BIND_SERVICE` to the container.
* Monitoring TCP connections can be excluded from lifecycle telemetry with `TelemetryIgnoredRemoteAddress`.
* Discarded TCP and UDP payload contents are never included in telemetry.

## License

HappyDiscard is licensed under the [MIT License](LICENSE).
