# write-to-trenddb

订阅 MQTT 主题，将传感器数据批量回写到 TrendDB5 时序数据库的 .NET 10 Worker Service。

## 架构

```
sensor-simulator-mapper / IoT Gateway
        │
        │ MQTT 批量消息（JSON 字典）
        │ Topic: device/sis/data
        ▼
  MqttConsumer（收包回调，只入队不处理）
        │
        │ Channel 有界队列（满时丢最旧保最新）
        ▼
  后台消费循环（反序列化 + 点表映射 + 内存缓冲）
        │
        │ 定时刷新（WriteIntervalSeconds）
        ▼
  TrendDb5Writer
        │
        │ Pool.SetValueByTagName(dbName, names, values)
        ▼
     TrendDB5
```

## MQTT 消息格式

MQTT 客户端推送的消息为 **JSON 字典**，key 为测点名，value 包含值、Unix 秒级时间戳和状态：

```json
{
  "timestamp": 1780076839551,
  "deviceId": "sis-collect-dev-dy",
  "batchData": {
    "DDM.SIS.1DCS_BBA01XP01": {"value": 1, "timestamp": 1780041492, "state": 1},
    "DDM.SIS.1DCS_BBA01XP02": {"value": 0, "timestamp": 1780041574, "state": 1},
    "DDM.SIS.1DCS_BBA02XP01": {"value": 1, "timestamp": 1780041492, "state": 0}
  }
}
```

| 字段        | 类型   | 说明                              |
|-------------|--------|-----------------------------------|
| `value`     | double | 测点值                            |
| `timestamp` | long   | Unix 秒级时间戳                   |
| `state`     | int    | 质量码：`1` = Good，`0` = Bad     |

## 配置说明（appsettings.json）

### TrendDB5 连接字符串

```
Type=TrendDB5;SERVER=ip:port;DATABASE=db01;UID=user;PWD=pass,SERVER=ip:port;DATABASE=db02;UID=user;PWD=pass
```

- 以 `Type=TrendDB5;` 开头（程序内部会自动跳过此前缀）
- 多个数据库之间用逗号分隔
- `PoolSize`：连接池大小，默认 7
- `WriteIntervalSeconds`：回写间隔秒数，代码默认 5，`appsettings.json` 配置为 1

### MQTT 配置

```json
"Mqtt": {
  "Broker": "192.168.122.231",
  "Port": 1884,
  "ClientId": "trenddb-writer",
  "Username": "",
  "Password": "",
  "Topics": ["device/sis/data"],
  "ChannelCapacity": 256,
  "SocketBufferSize": 1048576
}
```

| 字段 | 默认 | 说明 |
|---|---|---|
| `ChannelCapacity` | 256 | 收包→消费有界队列容量（**消息条数**，非测点数）。满时丢弃最旧、保留最新，保证收包线程永不阻塞。测点量大时**应调小**以免单条消息占用过高内存 |
| `SocketBufferSize` | 1048576 | MQTT 底层 TCP socket 接收缓冲区（字节，对应 `SO_RCVBUF`）。默认值 8KB 在云边高延迟链路下会把 TCP 接收窗口卡死（吞吐上限 ~1.4MB/s），3.x 起默认提升到 1MB |

> `CleanSession` 固定为 `true`（不可配置），原因见下方"断连问题修复记录"。

### 日志配置（Serilog）

使用 Serilog 结构化日志，支持 Console + File 双输出，各自独立控制日志等级：

```json
"Serilog": {
  "Using": ["Serilog.Sinks.Console", "Serilog.Sinks.File"],
  "MinimumLevel": "Verbose",
  "WriteTo": [
    {
      "Name": "Console",
      "Args": { "restrictedToMinimumLevel": "Information" }
    },
    {
      "Name": "File",
      "Args": {
        "path": "/logs/write-to-trenddb.log",
        "rollOnFileSizeLimit": true,
        "fileSizeLimitBytes": 10485760,
        "retainedFileCountLimit": 30,
        "restrictedToMinimumLevel": "Information"
      }
    }
  ]
}
```

- `MinimumLevel: "Verbose"` — 全局最低等级，实际过滤由各 sink 的 `restrictedToMinimumLevel` 控制
- Console sink：控制台输出等级
- File sink：文件输出等级 + 按大小轮转（`rollOnFileSizeLimit`），日志文件通过 hostPath 持久化到边缘节点

### 点表文件（PointsFilePath）

测点映射通过外部 CSV 文件管理，不再写入 `appsettings.json`：

```json
"PointsFilePath": "/config/points.csv"
```

**CSV 格式**（无表头，每行 `source,target`）：

```csv
DDM.SIS.1DCS_BBA01XP01,DDM.SIS.1DCS_BBA01XP01
DDM.SIS.1DCS_BBA01XP02,DDM.SIS.1DCS_BBA01XP02
DDM.SIS.M1_FH,DDM.SIS.M1_FH
```

- `source`：MQTT 消息字典中的 key（测点名），**区分大小写**
- `target`：TrendDB5 中的完整测点名，必须包含数据库前缀（`dbName.tagName`）
- 只有出现在点表中的测点才会被处理，其余一律丢弃
- 两侧名称可以不同，支持跨库回写
- 点表文件不存在时服务**启动即失败**，避免静默丢点

## 断连问题修复记录（v2.10.0 → v3.0.1）

> 背景：云边协同场景下，边缘节点经公网/高延迟链路连接云端 EMQX，write-to-trenddb 持续出现 **MQTT 断连 + 时序数据丢失**。2026-08 排查（运城边缘节点 write-to-trenddb MQTT 断连数据丢失排查报告已归档）后，按 **会话层 → 应用层 → 传输层** 三层依次修复。若再次遇到断连/丢数，按此表定位。

| 版本 | 层级 | 根因 | 修复 |
|---|---|---|---|
| 2.10.0 | 会话 | QoS0 断线期间消息直接丢失，无任何暂存 | QoS1 + 持久会话（当时 `CleanSession=false`，后于 2.10.3 改回，见下） |
| 2.10.1 | 会话 | 断线重连与首次连接逻辑纠缠，`already connected` 刷屏、重连竞态 | 重连加互斥与已连接守卫，首次连接（while 重试）与断线重连（Disconnected 事件）彻底分离 |
| 2.10.3 | 会话 | `CleanSession=false` 导致 broker 侧 mqueue 积压，断线后重连循环 | **`CleanSession` 改回 `true`**（固定值，不可配置）。本服务消费模式为"最新值覆盖"（`ConcurrentDictionary` 缓冲），离线期间的旧值无回写价值，持久会话反而是拥塞源 |
| 2.11.0 | 应用 | MQTTnet 收包回调里做反序列化 + 点表映射等重活，消费一慢就阻塞收包循环 → broker 侧 `send_pend` 堆积 → 触发 socket 超时断连 | **收包与消费解耦**：收包回调只拷贝 payload 入队（`System.Threading.Channels` 有界队列，满时 **DropOldest** 丢最旧保最新）立即返回；重活交给后台消费循环。丢弃计数 `_droppedMessageCount` 可观测 |
| 3.0.1 | 传输 | TCP socket 接收缓冲区默认仅 8KB，在高延迟链路上把 TCP 接收窗口卡死在 ~14KB，吞吐上限 ~1.4MB/s，EMQX 发送队列堆积后 socket 超时断连 | **`SocketBufferSize` 默认提升到 1MB**（可配置），对应 MQTTnet `tcp.BufferSize` / `SO_RCVBUF` |

**配套服务端调优**：EMQX 侧同步放大了监听器 `SNDBUF`/`RECBUF`（4MB）与 `BUFFER`（64KB）、`max_packet_size`（10MB，采集网关单条消息可达 ~2.4MB），见 `emqx` 仓库 v6.0.2。

**排查口诀**：断连先看 EMQX 侧 `send_pend` 是否堆积（有 → 收包侧被阻塞，查应用层/传输层）；再看 broker mqueue 是否积压（有 → 会话层，`CleanSession` 问题）；最后看 TCP 窗口/缓冲是否过小（高延迟链路必备 3.0.1 + 服务端调优）。

## 环境变量覆盖（生产部署）

.NET 配置系统支持通过环境变量覆盖 JSON 配置，使用双下划线分隔层级：

```bash
# 覆盖 TrendDB5 连接字符串
TRENDDB5__CONNECTIONSTRING="Type=TrendDB5;SERVER=10.0.0.1:20010;DATABASE=db01;UID=system;PWD=pass"

# 覆盖 MQTT Broker 地址
MQTT__BROKER=192.168.122.231
MQTT__PORT=1884

# 覆盖回写间隔
TRENDDB5__WRITEINTERVALSECONDS=1

# 覆盖点表文件路径
POINTSFILEPATH=/data/points.csv
```

## 关于 TrendDb_API.dll

项目依赖 `lib/TrendDb_API.dll`（来自 Luculent TrendDB5 客户端 SDK）。

- 该 DLL 为 C++/CLI 托管封装，内含 `TrendDb_API.Pool` 和 `Ld.COMMON.TagValue` 等类型
- **Windows 节点**：直接使用 `lib/TrendDb_API.dll`
- **Linux/ARM64 节点**：需要将 Luculent 提供的 Linux 版本客户端库（`.so` 文件）放置在同一目录，并替换为对应平台版本

## 本地运行

```bash
dotnet restore
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

## Docker 构建

```bash
# 构建镜像
docker build -t write-to-trenddb:latest .

# 运行（挂载本地点表文件 + 日志目录）
docker run -d \
  -v /opt/write-to-trenddb/config/points.csv:/config/points.csv:ro \
  -v /opt/write-to-trenddb/logs:/logs \
  -e TRENDDB5__CONNECTIONSTRING="Type=TrendDB5;SERVER=127.0.0.1:20010;DATABASE=db01;UID=system;PWD=luculent123@" \
  -e MQTT__BROKER=192.168.122.231 \
  -e MQTT__PORT=1884 \
  --name write-to-trenddb \
  write-to-trenddb:latest
```

## Helm 部署

### 安装 / 升级

```bash
helm upgrade --install write-to-trenddb \
  oci://harbor.zkjgy.online/library/write-to-trenddb \
  --namespace <namespace> \
  -f values.yaml
```

### 日志文件持久化配置

日志文件通过 `logFile` 段配置，支持按大小自动轮转并持久化到边缘节点：

```yaml
logFile:
  enabled: true
  hostPath: /opt/write-to-trenddb/logs       # 边缘节点本地日志目录（部署前需 mkdir）
  mountPath: /logs                            # 容器内挂载路径
  fileSizeLimitMB: 10                         # 单个日志文件最大 MB
  retainedFileCountLimit: 30                   # 最多保留的日志文件数
  consoleLevel: Information                    # 控制台输出最低日志等级
  fileLevel: Information                       # 写入文件的日志最低等级
```

部署前创建日志目录：
```bash
ssh root@<edge-node> mkdir -p /opt/write-to-trenddb/logs
```

### 点表文件管理

点表 CSV 文件通过 **hostPath** 挂载，配置见 `values.yaml`：

```yaml
pointsFile:
  hostPath: /opt/write-to-trenddb/config/points.csv   # 边缘节点上的路径
  mountPath: /config/points.csv                         # 容器内路径（与 PointsFilePath 一致）
```

**首次部署前**，需将点表文件上传到边缘节点：

```bash
scp config/points.csv root@<edge-node>:/opt/write-to-trenddb/config/points.csv
```

**更新点表**（无需 helm upgrade，无需重建镜像）：

```bash
# 1. 上传新点表到边缘节点
scp points.csv root@<edge-node>:/opt/write-to-trenddb/config/points.csv

# 2. 重启 Pod 使新点表生效
kubectl rollout restart deployment/write-to-trenddb -n <namespace>
```

## CI/CD（GitHub Actions）

`.github/workflows/build-push.yml` 在 `v*` tag 时自动触发：

| Job                  | 说明                                                   |
|----------------------|--------------------------------------------------------|
| `version`            | 从 tag 提取版本号                                       |
| `build-amd64`        | 构建并推送 `linux-amd64` 镜像到 Harbor                 |
| `build-arm64`        | 使用原生 ARM runner 构建并推送 `linux-arm64` 镜像      |
| `manifest`           | 合并为多架构 Manifest                                  |
| `helm-package`       | 打包 Helm Chart 并推送到 Harbor OCI Registry           |
| `release`            | 创建 GitHub Release，附加镜像 tar + Helm Chart + paramSchema |

**所需 GitHub Secrets：**

| Secret            | 说明                    |
|-------------------|-------------------------|
| `HARBOR_USERNAME` | Harbor 登录用户名       |
| `HARBOR_PASSWORD` | Harbor 登录密码         |

## 调试工具

镜像内置以下调试命令：

```bash
# 进入容器
kubectl exec -it <pod-name> -- bash

# 测试 MQTT 连通性
telnet 192.168.122.231 1884

# 测试网络
ping 192.168.122.231
curl http://192.168.122.211:8080/api/sensors
```

## 项目文件结构

```
write-to-trenddb/
├── Configuration/
│   └── AppSettings.cs              # 配置类（含 PointsFilePath）
├── Models/
│   └── SensorMessage.cs            # SensorValue（字典值）+ TagData 模型
├── TrendDb/
│   ├── ITrendDb5Writer.cs           # 写入接口
│   ├── TrendDb5ConnectionPool.cs    # 连接池
│   └── TrendDb5Writer.cs            # 批量写入实现
├── Mqtt/
│   └── MqttConsumer.cs              # 订阅 + 字典格式解析 + 内存缓冲
├── Workers/
│   └── TrendDbWriteWorker.cs        # 定时写入 BackgroundService
├── helm/
│   └── write-to-trenddb/
│       ├── Chart.yaml
│       ├── values.yaml              # 含 logFile + pointsFile 配置
│       └── templates/
│           ├── deployment.yaml      # hostPath 卷挂载（点表 + 日志）
│           └── configmap.yaml       # appsettings.json + Serilog 配置渲染
├── config/
│   └── points.csv                   # 本地测试用点表（勿提交生产数据）
├── lib/
│   └── TrendDb_API.dll              # TrendDB5 客户端 SDK
├── Program.cs                       # 启动入口，Serilog 初始化 + CSV 校验
├── WriteToTrendDb.csproj
├── appsettings.json
├── Dockerfile
├── paramSchema.json                 # 平台部署向导参数表单
└── .github/workflows/build-push.yml
```
