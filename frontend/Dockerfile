# Build context 係 frontend/（`docker build ./frontend`，或者由根目錄嘅 compose.yaml 觸發）。

# ---- build ----
FROM node:24-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# VITE_* 喺 build 時寫入 bundle，改咗要重新 build image。
# VITE_API_BASE_URL 留空 = browser 打同源 /api，由下面嘅 nginx 轉發去 backend（browser 唔會直接用 container hostname）。
ARG VITE_POLL_INTERVAL_MS=1000
ARG VITE_API_BASE_URL=
ENV VITE_POLL_INTERVAL_MS=$VITE_POLL_INTERVAL_MS \
    VITE_API_BASE_URL=$VITE_API_BASE_URL
RUN npm run build

# ---- runtime ----
FROM nginxinc/nginx-unprivileged:stable-alpine AS runtime

# 官方 nginx image 啟動時會將 /etc/nginx/templates/*.template 用環境變數展開到 /etc/nginx/conf.d/。
COPY nginx/default.conf.template /etc/nginx/templates/default.conf.template
COPY nginx/security-headers.conf /etc/nginx/security-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html

# BACKEND_UPSTREAM：Compose 網絡內 backend service 嘅位置；只供 nginx 喺 container 內部轉發，唔會出現喺 browser。
# NGINX_ENTRYPOINT_LOCAL_RESOLVERS：令 entrypoint 由 /etc/resolv.conf 取得 DNS resolver（見 nginx/default.conf.template）。
ENV BACKEND_UPSTREAM=http://backend:5080 \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1

EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=3 \
    CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/healthz"]
