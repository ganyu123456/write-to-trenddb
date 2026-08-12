# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

.NET 10 Worker Service that subscribes to MQTT topics, buffers incoming sensor batch data in memory, and periodically writes it to TrendDB5 (Luculent time-series database). Deployed via Helm to KubeEdge edge nodes.

## Build & run

```bash
dotnet restore
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

There are no tests in this repository. The project targets `net10.0` with `AllowUnsafeBlocks=true` (required by the TrendDB5 native SDK).

## Startup & DI flow

`Program.cs` registers services in this order (order matters — each depends on the previous):

1. `TrendDb5ConnectionPool` (singleton) — initializes N `TrendDb_API.Pool` objects in constructor, stripping the `Type=TrendDB5;` prefix from connection strings
2. `ITrendDb5Writer` / `TrendDb5Writer` (singleton) — depends on pool
3. `MqttConsumer` (singleton) — loads points CSV in constructor, initializes empty `ConcurrentDictionary` buffer
4. `TrendDbWriteWorker` (hosted service) — `ExecuteAsync` starts MQTT consumer first, then enters `PeriodicTimer` loop

**Points file validation happens twice** by design:
- `Program.cs` checks `File.Exists(PointsFilePath)` and throws on missing file — fail-fast at startup
- `MqttConsumer` constructor checks again — if CSV is missing, it falls back to `TagMappings` from `appsettings.json` (for small-scale deployments without hostPath mounts)

## Dockerfile

Multi-stage build (`mcr.microsoft.com/dotnet/sdk:10.0` → `runtime:10.0`). Runtime image installs debugging tools: `iputils-ping`, `curl`, `telnet`, `mosquitto-clients`. Container timezone is set to `Asia/Shanghai`. Health check uses `pgrep -x WriteToTrendDb`.

**Logging:** Uses Serilog with Console + File sinks. File sink supports size-based rolling (`rollOnFileSizeLimit` + `retainedFileCountLimit`). Log config is in the `Serilog` section of `appsettings.json`; File sink args are injected by the ConfigMap template from `logFile.*` values.

## Architecture

```
MQTT Broker → MqttConsumer (ConcurrentDictionary buffer) → TrendDbWriteWorker (PeriodicTimer)
                                                                    ↓
                                                              TrendDb5Writer
                                                                    ↓
                                                         TrendDb5ConnectionPool (round-robin)
                                                                    ↓
                                                               TrendDB5
```

- **`MqttConsumer`** — singleton. Connects to MQTT broker with auto-reconnect (5s backoff). Uses `_hasConnectedOnce` flag to separate initial connection retries (handled by `ConnectWithRetryAsync`'s while-loop) from post-connection disconnects (handled by `DisconnectedAsync` event). Parses incoming `MqttBatchMessage` JSON, filters against the points file `Source→Target` dictionary, and stores latest values in a `ConcurrentDictionary` buffer.
- **`TrendDbWriteWorker`** — `BackgroundService`. On startup, kicks off `MqttConsumer.StartAsync()`, then loops on `PeriodicTimer` (default 5s). Each tick calls `MqttConsumer.Flush()` (atomically drains + clears the buffer) and passes data to `ITrendDb5Writer.SetRtValuesByNames()`.
- **`TrendDb5Writer`** — groups points by database prefix (text before first `.`), calls `Pool.SetValueByTagName(dbName, names, values, ref resList)` per database. `ToUnixMilliseconds` handles DateTime Kind conversion (UTC/Local/Unspecified) before converting to the ulong timestamp TrendDB5 expects.
- **`TrendDb5ConnectionPool`** — creates N `TrendDb_API.Pool` objects on startup, round-robin acquires one for each write. Connection strings are comma-separated multi-db; the `Type=TrendDB5;` prefix is stripped before passing to the SDK.

## TrendDB5 SDK dependency

`lib/TrendDb_API.dll` is the Windows C++/CLI managed assembly from Luculent TrendDB5 client SDK. It depends on specific NuGet versions pinned in the csproj:

- `Grpc.Net.Client` 2.57.0
- `Google.Protobuf` 3.22.4
- `Grpc.Core.Api` 2.57.0

These versions must match the DLL's compile-time assembly references. On Linux/ARM64 edge nodes, the DLL is replaced with the platform-native `.so` equivalent.

## Points file (CSV mapping)

The points file is a CSV (no header, `source,target` per line) that maps MQTT message keys to TrendDB5 tag names. Only mapped points are processed; everything else is discarded.

**Critical performance note:** The CSV is loaded directly via `File.ReadLines()` in `MqttConsumer.LoadMappingsFromCsv()`, bypassing `IConfiguration` binding entirely. This avoids O(n²) performance issues when binding tens of thousands of `TagMapping` entries through the config system.

In Kubernetes, the points file is mounted via `hostPath` (not ConfigMap) because it can exceed ConfigMap size limits. To update points: `scp` the new file to the edge node, then `kubectl rollout restart`.

## Configuration

`appsettings.json` is the base config. In Kubernetes, it's rendered from Helm `values.yaml` → ConfigMap → mounted at `/app/appsettings.json`. All settings can be overridden via environment variables using double-underscore separators (e.g., `TRENDDB5__CONNECTIONSTRING`, `MQTT__BROKER`).

Key config sections:
- `TrendDb5.ConnectionString` — comma-separated connection strings (starts with `Type=TrendDB5;`)
- `TrendDb5.PoolSize` — connection pool size (default 7)
- `TrendDb5.WriteIntervalSeconds` — flush interval (default 5)
- `Mqtt.Topics` — list of MQTT topics to subscribe to
- `PointsFilePath` — container path to CSV points file
- `LogFilePath` — container path to log file (Serilog File sink writes here, directory mounted via `logFile.hostPath`)

## CI/CD

Two pipeline systems exist:

1. **Drone CI** (`.drone.yml`) — 4 pipelines: `amd64`, `arm64`, `manifest`, `helm`. Triggered on push/tag to `main`. Uses `plugins/docker` and `plugins/manifest`.

2. **GitHub Actions** (`.github/workflows/build-push.yml`) — triggered only on `v*` tags. 5 jobs: `version`, `build-amd64` (ubuntu-latest runner), `build-arm64` (ubuntu-24.04-arm native runner), `manifest`, `helm-package`, `release`. The release job creates offline deployment packages containing both architecture images, Helm chart, and `paramSchema.json`.

## Helm chart

Located at `helm/write-to-trenddb/`. Key deployment characteristics:
- Targets KubeEdge edge nodes via `node-role.kubernetes.io/edge` toleration
- Uses `hostNetwork: true` by default for direct access to TrendDB5/EMQX on the node
- Points CSV mounted via `hostPath` (type: File); log directory mounted via `hostPath` (type: DirectoryOrCreate)
- `logFile` section controls file log rotation (size limit, retention count, minimum level)
- `appsettings.json` injected as ConfigMap with checksum annotation for auto-rollout on config change
- `paramSchema.json` at repo root defines the platform deployment wizard form schema

`resource/deployment.yaml` is a reference non-Helm deployment manifest (uses `nodeName` hard scheduling, ConfigMap subPath mount). It predates the Helm chart and is not used in production.

## MQTT message format

```json
{
  "timestamp": 1780076839551,
  "deviceId": "sis-collect-dev-dy",
  "batchData": {
    "DDM.SIS.1DCS_BBA01XP01": {"value": 1, "timestamp": 1780041492, "state": 1},
    "DDM.SIS.1DCS_BBA01XP02": {"value": 0, "timestamp": 1780041574, "state": 0}
  }
}
```

- `timestamp` — message send time (Unix ms)
- `batchData.<key>.timestamp` — per-point collection time (Unix seconds)
- `state` — quality code: `1` = Good, `0` = Bad
