# wirte-to-trenddb

订阅 MQTT 主题，将传感器数据批量回写到 TrendDB5 时序数据库的 .NET 10 Worker Service。

## 架构

```
sensor-simulator-mapper
        │
        │ MQTT 批量消息（JSON 数组）
        │ Topic: sensors/batch、sensors2/batch ...
        ▼
  MqttConsumer（内存缓冲）
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

mapper 推送的消息为 JSON 数组，每个元素结构如下：

```json
[
  {"name":"db01.tag1","value":123.456,"timestamp":"2026-03-15T08:00:00Z","value_state":1},
  {"name":"db01.tag2","value":456.789,"timestamp":"2026-03-15T08:00:00Z","value_state":1}
]
```

## 配置说明（appsettings.json）

### TrendDB5 连接字符串

```
Type=TrendDB5;SERVER=ip:port;DATABASE=db01;UID=user;PWD=pass,SERVER=ip:port;DATABASE=db02;UID=user;PWD=pass
```

- 以 `Type=TrendDB5;` 开头（程序内部会自动跳过此前缀）
- 多个数据库之间用逗号分隔
- `PoolSize`：连接池大小，默认 7（与 TrendDB5 原项目保持一致）
- `WriteIntervalSeconds`：回写间隔秒数，默认 5 秒

### MQTT 配置

```json
"Mqtt": {
  "Broker": "192.168.122.231",
  "Port": 1884,
  "ClientId": "trenddb-writer",
  "Username": "",
  "Password": "",
  "Topics": ["sensors/batch", "sensors2/batch"]
}
```

### 测点映射（TagMappings）

配置消费测点名（来自 MQTT）与回写测点名（写入 TrendDB5）的对应关系：

```json
"TagMappings": [
  { "Source": "db01.tag1", "Target": "db01.tag1" },
  { "Source": "db01.tag2", "Target": "db02.tag2" }
]
```

- `Source`：MQTT 消息中 `name` 字段的值，**区分大小写**
- `Target`：TrendDB5 中的完整测点名，必须包含数据库前缀（`dbName.tagName`）
- 只有出现在 `TagMappings` 中的测点才会被处理，其余一律丢弃
- 两侧名称可以不同，支持跨库回写

## 环境变量覆盖（生产部署）

.NET 配置系统支持通过环境变量覆盖 JSON 配置，使用双下划线分隔层级：

```bash
# 覆盖 TrendDB5 连接字符串
TRENDDB5__CONNECTIONSTRING="Type=TrendDB5;SERVER=10.0.0.1:20010;DATABASE=db01;UID=system;PWD=pass"

# 覆盖 MQTT Broker 地址
MQTT__BROKER=192.168.122.231
MQTT__PORT=1884

# 覆盖回写间隔
TRENDDB5__WRITEINTERVALSECONDS=10
```

## 关于 TrendDb_API.dll

项目依赖 `lib/TrendDb_API.dll`（来自 Luculent TrendDB5 客户端 SDK）。

- 该 DLL 为 C++/CLI 托管封装，内含 `TrendDb_API.Pool` 和 `Ld.COMMON.TagValue` 等类型
- **Windows 节点**：直接使用 `lib/TrendDb_API.dll`（已复制到项目）
- **Linux/ARM64 节点**：需要将 Luculent 提供的 Linux 版本客户端库（`.so` 文件）放置在同一目录，并替换 `TrendDb_API.dll` 为对应平台版本

## 本地运行

```bash
cd wirte-to-trenddb

# 安装依赖
dotnet restore

# 运行（开发环境，日志更详细）
ASPNETCORE_ENVIRONMENT=Development dotnet run
```

## Docker 构建

```bash
# amd64
docker build -t wirte-to-trenddb:latest .

# 运行（通过环境变量覆盖配置）
docker run -d \
  -e TRENDDB5__CONNECTIONSTRING="Type=TrendDB5;SERVER=127.0.0.1:20010;DATABASE=db01;UID=system;PWD=luculent123@" \
  -e MQTT__BROKER=192.168.122.231 \
  -e MQTT__PORT=1884 \
  --name wirte-to-trenddb \
  wirte-to-trenddb:latest
```

## CI/CD（Drone）

`.drone.yml` 配置三个 Pipeline：

1. `wirte-to-trenddb-amd64` — 在 amd64 Runner 构建并推送 `linux-amd64` 标签
2. `wirte-to-trenddb-arm64` — 在 arm64 Runner 构建并推送 `linux-arm64` 标签
3. `wirte-to-trenddb-manifest` — 合并为多架构 Manifest，打 `latest` 标签

触发条件：`main` 分支的 push 或 tag 事件。

需在 Drone 中配置如下 Secret：
- `harbor_username`
- `harbor_password`

## 调试工具

镜像内置以下调试命令：

```bash
# 测试网络连通性
ping 192.168.122.231
curl http://192.168.122.211:8080/api/sensors?page=1&size=5
telnet 192.168.122.231 1884

# 进入容器
kubectl exec -it <pod-name> -- bash
```

## 项目文件结构

```
wirte-to-trenddb/
├── Configuration/
│   └── AppSettings.cs        # 配置类定义
├── Models/
│   └── SensorMessage.cs      # MQTT 消息 + 内部 TagData 模型
├── TrendDb/
│   ├── ITrendDb5Writer.cs    # 写入接口
│   ├── TrendDb5ConnectionPool.cs  # 连接池（参照原项目）
│   └── TrendDb5Writer.cs     # SetRtValuesByNames 实现
├── Mqtt/
│   └── MqttConsumer.cs       # 订阅 + 缓冲
├── Workers/
│   └── TrendDbWriteWorker.cs # 定时写入 BackgroundService
├── lib/
│   └── TrendDb_API.dll       # TrendDB5 客户端 SDK
├── Program.cs
├── WriteToTrendDb.csproj
├── appsettings.json
├── Dockerfile
└── .drone.yml
```
