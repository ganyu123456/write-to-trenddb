# write-to-trenddb

订阅 MQTT 主题，将传感器数据批量回写到 TrendDB5 时序数据库的 .NET 10 Worker Service。

## 架构

```
sensor-simulator-mapper / IoT Gateway
        │
        │ MQTT 批量消息（JSON 字典）
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

MQTT 客户端推送的消息为 **JSON 字典**，key 为测点名，value 包含值、Unix 秒级时间戳和状态：

```json
{
  "DDM.SIS.1DCS_BBA01XP01": {"value": 1, "timestamp": 1780041492, "state": 1},
  "DDM.SIS.1DCS_BBA01XP02": {"value": 0, "timestamp": 1780041574, "state": 1},
  "DDM.SIS.1DCS_BBA02XP01": {"value": 1, "timestamp": 1780041492, "state": 0}
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

# 运行（挂载本地点表文件）
docker run -d \
  -v /opt/write-to-trenddb/config/points.csv:/config/points.csv:ro \
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

`.github/workflows/build-push.yml` 包含四个 Job，在 `main` 分支 push 或 tag 时自动触发：

| Job                  | 说明                                                   |
|----------------------|--------------------------------------------------------|
| `build-amd64`        | 构建并推送 `linux-amd64` 镜像到 Harbor                 |
| `build-arm64`        | 使用 QEMU 构建并推送 `linux-arm64` 镜像到 Harbor       |
| `manifest`           | 合并为多架构 Manifest，打 `latest` 标签                |
| `helm-package-push`  | 打包 Helm Chart 并推送到 Harbor OCI Registry           |

打 tag 时，还会额外：
- 将 amd64/arm64 镜像导出为 `.tar.gz`
- 打包 Helm Chart `.tgz`
- 创建 GitHub Release 并附上上述三个文件

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
│       ├── values.yaml              # 含 pointsFile.hostPath 配置
│       └── templates/
│           ├── deployment.yaml      # hostPath 卷挂载点表
│           └── configmap.yaml       # appsettings.json 渲染
├── config/
│   └── points.csv                   # 本地测试用点表（勿提交生产数据）
├── lib/
│   └── TrendDb_API.dll              # TrendDB5 客户端 SDK
├── Program.cs                       # 启动入口，含 CSV 加载逻辑
├── WriteToTrendDb.csproj
├── appsettings.json
├── Dockerfile
└── .github/workflows/build-push.yml
```
