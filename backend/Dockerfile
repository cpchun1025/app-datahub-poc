# Build context 係 backend/（`docker build ./backend`，或者由根目錄嘅 compose.yaml 觸發）。

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只複製 restore 需要嘅檔案，令 NuGet restore 呢層喺原始碼改動時仍然可以重用 cache。
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DataHub.Api/DataHub.Api.csproj src/DataHub.Api/
RUN dotnet restore src/DataHub.Api/DataHub.Api.csproj

COPY src/ src/
RUN dotnet publish src/DataHub.Api/DataHub.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ASPNETCORE_URLS 要綁 +（所有介面），否則 container 外面（frontend 嘅 nginx、host）連唔到。
# 基礎 image 預設有 ASPNETCORE_HTTP_PORTS=8080；同 ASPNETCORE_URLS 同時存在會令 Kestrel 打 warning，所以清空。
ENV ASPNETCORE_URLS=http://+:5080 \
    ASPNETCORE_HTTP_PORTS= \
    DataHub__Checkpoint__Directory=/var/lib/datahub/checkpoints

# 預先建立 checkpoint 目錄並交畀非 root 用戶；named volume 第一次掛載時會沿用呢個擁有者。
RUN mkdir -p /var/lib/datahub/checkpoints \
    && chown -R "$APP_UID:$APP_UID" /var/lib/datahub

COPY --from=build /app/publish .

USER $APP_UID
EXPOSE 5080

# 基礎 image 冇 curl／wget，所以用 bash 內置嘅 /dev/tcp 發一個最簡單嘅 HTTP/1.0 request，確認 /api/health 回 200。
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/5080 && printf 'GET /api/health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "DataHub.Api.dll"]
