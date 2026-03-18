# ─────────────────────────────────────────────
# Stage 1: Build
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /build

# 先还原依赖（利用 Docker 层缓存，只有 csproj 变化时才重新还原）
COPY WriteToTrendDb.csproj .
COPY lib/ lib/
RUN dotnet restore WriteToTrendDb.csproj

# 再复制其余源码并发布
COPY . .
RUN dotnet publish WriteToTrendDb.csproj \
      -c Release \
      -o /out \
      --no-restore

# ─────────────────────────────────────────────
# Stage 2: Runtime
# 使用 Ubuntu 系 .NET 10 运行时（对本地 so 库兼容性更好）
# ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0

WORKDIR /app

# 安装调试工具：ping / curl / telnet
RUN apt-get update && apt-get install -y --no-install-recommends \
      iputils-ping \
      curl \
      telnet \
    && rm -rf /var/lib/apt/lists/*

# 复制发布产物（含 TrendDb_API.dll 及 appsettings.json）
COPY --from=builder /out .

# 健康检查：仅检测进程是否存活
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
  CMD pgrep -x WriteToTrendDb || exit 1

ENTRYPOINT ["dotnet", "WriteToTrendDb.dll"]
