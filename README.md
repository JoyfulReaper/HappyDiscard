# HappyDiscard

HappyDiscard is a lightweight asynchronous TCP discard server written in C# and .NET 10.

It implements the classic Discard Protocol: every byte received from a client is accepted and thrown away. Discarded content is never decoded, stored, logged, echoed, or published to telemetry.

## Features

* Asynchronous TCP connections
* Configurable address and port
* Concurrent connection limit
* Connection timeout
* Maximum bytes per connection
* Pooled network buffers
* Graceful shutdown
* Mission Control connection lifecycle telemetry
* Production Docker support
* Windows Service support
* Structured logging

# Try it live

Connect to TCP port 9 discard.kgivler.com and send bytes.

## Requirements

To build HappyDiscard:

* .NET 10 SDK

For the recommended Linux VPS deployment:

* Docker Engine with Compose
* A Linux VPS with permission to accept inbound TCP connections

The repository includes a `NuGet.config` and `local-nuget` package feed for the JoyfulReaperLib packages used by Docker builds. Keep those package files in sync with the versions referenced by `HappyDiscard/HappyDiscard.csproj`.

## Build And Test

```bash
git clone https://github.com/JoyfulReaper/HappyDiscard.git
cd HappyDiscard

dotnet restore HappyDiscard.slnx
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
    "ListenAddress": "0.0.0.0",
    "Port": 9,
    "MaxConcurrentConnections": 64,
    "RequestTimeoutSeconds": 15,
    "MaxBytesPerConnection": 1048576,
    "TelemetryIgnoredRemoteAddress": null
  },
  "MissionControl": {
    "Enabled": false,
    "BaseUrl": "http://localhost:5190",
    "ApiKey": "",
    "TimeoutMilliseconds": 1000
  }
}
```

| Setting                         |     Default | Description                                                                        |
| ------------------------------- | ----------: | ---------------------------------------------------------------------------------- |
| `ListenAddress`                 | `127.0.0.1` | Address used by the TCP listener. Use `0.0.0.0` to accept remote IPv4 connections. |
| `Port`                          |         `9` | TCP listening port. Port 9 is the traditional Discard Protocol port.               |
| `MaxConcurrentConnections`      |        `64` | Maximum number of simultaneous client connections.                                 |
| `RequestTimeoutSeconds`         |        `15` | Maximum lifetime of one connection.                                                |
| `MaxBytesPerConnection`         |   `1048576` | Maximum bytes accepted during one connection. The default is 1 MiB.                |
| `TelemetryIgnoredRemoteAddress` |     `null` | Optional monitor IP whose Discard sessions are processed normally but excluded from Mission Control lifecycle telemetry. |

Settings can also be supplied through environment variables:

```bash
Discard__ListenAddress=0.0.0.0
Discard__Port=9
Discard__MaxConcurrentConnections=64
Discard__RequestTimeoutSeconds=15
Discard__MaxBytesPerConnection=1048576
Discard__TelemetryIgnoredRemoteAddress=172.21.0.1

MissionControl__Enabled=true
MissionControl__BaseUrl=http://gateway:8080
MissionControl__ApiKey=replace-with-a-strong-random-key
MissionControl__TimeoutMilliseconds=1000
```

`TelemetryIgnoredRemoteAddress` suppresses Mission Control telemetry only. The TCP session is still accepted, discarded, timed out, byte-limited, and cleaned up normally. The comparison uses only the normalized remote IP address, not the source port, and IPv4-mapped IPv6 addresses are mapped to IPv4 before comparison. This is intended for Uptime Kuma or another trusted TCP monitor. Docker network gateway addresses vary by host and network, so verify the actual monitor source address before setting it.

## Mission Control Events

HappyDiscard publishes best-effort lifecycle telemetry through `JoyfulReaperLib.MissionControl`. Telemetry failures are logged as warnings and never break discard traffic or graceful shutdown.

Event types:

* `happydiscard.service.started`
* `happydiscard.discarding.started`
* `happydiscard.discarding.stopped`

`service.started` payload:

* `listenAddress`

`discarding.started` payload:

* `remote`
* `requestTimeoutSeconds`
* `maxBytesPerConnection`

`discarding.stopped` payload:

* `remote`
* `bytesDiscarded`
* `durationMilliseconds`
* `outcome`
* `succeeded`

Outcomes:

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

The two events for one discard session share the same Mission Control correlation ID.

## Docker

Build the image:

```bash
docker build --no-cache -t happy-discard .
```

The Dockerfile:

* Restores, tests, and publishes in a .NET SDK build stage.
* Publishes a Native AOT executable.
* Uses the small .NET `runtime-deps` image for the final stage.
* Does not contain the full managed .NET runtime.
* Runs as `${APP_UID}`, not root.
* Listens internally on TCP port `9009`.
* Can be published externally as canonical Discard TCP port `9` with `9:9009`.
* Does not embed configuration or secrets.

The Docker image defaults to:

```dockerfile
ENV Discard__ListenAddress=0.0.0.0
ENV Discard__Port=9009
```

Local unprivileged mapping:

```bash
docker run --rm -p 9009:9009 happy-discard
```

Canonical public Discard mapping:

```bash
docker run --rm -p 9:9009 happy-discard
```

Publishing host port 9 may require host-level privileges on Linux and macOS, while the process inside the container remains non-root and needs no added Linux capability.

No Docker health check is currently defined. The current `runtime-deps` image does not include a TCP probing utility such as `nc`. Compose can still verify the service process state, and listener checks should be performed externally or from the host.

## Linux VPS Deployment With Docker Compose

Docker Compose is the recommended Linux deployment path.

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
    Discard__Port: 9009
    Discard__MaxConcurrentConnections: 64
    Discard__RequestTimeoutSeconds: 15
    Discard__MaxBytesPerConnection: 1048576
    Discard__TelemetryIgnoredRemoteAddress: "172.21.0.1"

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

### 9. Test From An External Machine

```bash
printf 'Hello from HappyDiscard\n' | nc -v your-vps-hostname 9
```

Expected behavior:

```text
Connection succeeds, no payload is returned, and the server closes when the client disconnects, times out, or reaches the configured byte limit.
```

### 10. Confirm Mission Control Events

Mission Control should contain a matching pair with the same correlation ID:

```text
happydiscard.discarding.started
happydiscard.discarding.stopped
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

* HappyDiscard is a raw TCP service.
* It does not provide authentication or encryption.
* Public TCP ports will be scanned by automated systems.
* Keep connection, timeout, byte, firewall, and memory limits enabled.
* `RequestTimeoutSeconds` limits the total connection lifetime. It does not reset after each payload.
* `MaxBytesPerConnection` limits the total bytes accepted by one connection.
* Port 9 is privileged on Linux. Publish host port 9 to container port 9009; do not run HappyDiscard as root and do not add `NET_BIND_SERVICE` to the container.
* Monitoring connections can be excluded from lifecycle telemetry with `TelemetryIgnoredRemoteAddress`.

## License

HappyDiscard is licensed under the [MIT License](LICENSE).
