#!/usr/bin/env bash
set -Eeuo pipefail

DOMAIN="${1:-}"
BOOTSTRAP_TOKEN="${2:-}"
PACKAGE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_ROOT="/opt/cloud-remote-shouter"
APP_DIR="$APP_ROOT/app"
VENV_DIR="$APP_ROOT/venv"
DATA_DIR="/var/lib/cloud-remote-shouter"
BACKUP_DIR="/var/backups/cloud-remote-shouter"

fail() {
  echo "错误：$*" >&2
  exit 1
}

[[ $EUID -eq 0 ]] || fail "请使用 root 账号运行安装程序。"
[[ "$DOMAIN" =~ ^([A-Za-z0-9-]+\.)+[A-Za-z]{2,}$ ]] || fail "域名格式不正确：$DOMAIN"
[[ ${#BOOTSTRAP_TOKEN} -ge 32 ]] || fail "初始化密钥无效。"
[[ -f "$PACKAGE_ROOT/server/app.py" ]] || fail "安装包缺少 server/app.py。"
[[ -f "$PACKAGE_ROOT/web_controller/index.html" ]] || fail "安装包缺少教师网页。"
[[ -f "$PACKAGE_ROOT/cloud/migrations/0001_initial.sql" ]] || fail "安装包缺少数据库结构。"
[[ -f "$PACKAGE_ROOT/receiver_release/manifest.json" ]] || fail "安装包缺少教室端更新信息。"
[[ -f "$PACKAGE_ROOT/receiver_release/ICeCreamShouter.exe" ]] || fail "安装包缺少教室端 EXE。"

if [[ ! -r /etc/os-release ]]; then
  fail "无法识别服务器系统。请重装为 Ubuntu Server 24.04 LTS。"
fi
. /etc/os-release
if [[ "${ID:-}" != "ubuntu" ]] || [[ "${VERSION_ID:-}" != "24.04" ]]; then
  fail "当前系统是 ${PRETTY_NAME:-未知系统}。本一键脚本要求 Ubuntu Server 24.04 LTS。"
fi

export DEBIAN_FRONTEND=noninteractive
echo "[1/8] 安装系统组件..."
apt-get update
apt-get install -y --no-install-recommends python3 python3-venv nginx certbot python3-certbot-nginx curl sqlite3 ca-certificates

echo "[2/8] 创建独立运行账号和目录..."
if ! id cloudshouter >/dev/null 2>&1; then
  useradd --system --home-dir "$APP_ROOT" --shell /usr/sbin/nologin cloudshouter
fi
install -d -m 0755 "$APP_ROOT" "$DATA_DIR" "$BACKUP_DIR"
chown cloudshouter:cloudshouter "$DATA_DIR" "$BACKUP_DIR"

echo "[3/8] 安装 ICeCream Shouter..."
STAGING_DIR="$APP_ROOT/app.new"
rm -rf "$STAGING_DIR"
install -d -m 0755 "$STAGING_DIR/server" "$STAGING_DIR/web_controller" "$STAGING_DIR/cloud/migrations" "$STAGING_DIR/receiver_release"
install -m 0644 "$PACKAGE_ROOT/server/app.py" "$STAGING_DIR/server/app.py"
install -m 0644 "$PACKAGE_ROOT/server/requirements.txt" "$STAGING_DIR/server/requirements.txt"
install -m 0644 "$PACKAGE_ROOT/web_controller/index.html" "$STAGING_DIR/web_controller/index.html"
install -m 0644 "$PACKAGE_ROOT/web_controller/favicon.svg" "$STAGING_DIR/web_controller/favicon.svg"
install -m 0644 "$PACKAGE_ROOT/cloud/migrations/0001_initial.sql" "$STAGING_DIR/cloud/migrations/0001_initial.sql"
install -m 0644 "$PACKAGE_ROOT/receiver_release/manifest.json" "$STAGING_DIR/receiver_release/manifest.json"
install -m 0644 "$PACKAGE_ROOT/receiver_release/ICeCreamShouter.exe" "$STAGING_DIR/receiver_release/ICeCreamShouter.exe"
chown -R root:root "$STAGING_DIR"

python3 -m venv "$VENV_DIR"
"$VENV_DIR/bin/pip" install --disable-pip-version-check --index-url https://mirrors.cloud.tencent.com/pypi/simple --upgrade pip
"$VENV_DIR/bin/pip" install --disable-pip-version-check --index-url https://mirrors.cloud.tencent.com/pypi/simple -r "$STAGING_DIR/server/requirements.txt"

systemctl stop cloud-remote-shouter.service 2>/dev/null || true
rm -rf "$APP_DIR"
mv "$STAGING_DIR" "$APP_DIR"

echo "[4/8] 配置开机自启服务..."
umask 077
cat > /etc/cloud-remote-shouter.env <<EOF
CRS_BOOTSTRAP_TOKEN=$BOOTSTRAP_TOKEN
CRS_DATABASE_PATH=$DATA_DIR/cloud-remote-shouter.db
CRS_SESSION_DAYS=14
CRS_HISTORY_DAYS=0
CRS_HOST=127.0.0.1
CRS_PORT=8765
EOF
chmod 0600 /etc/cloud-remote-shouter.env

cat > /etc/systemd/system/cloud-remote-shouter.service <<'EOF'
[Unit]
Description=ICeCream Shouter domestic server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=cloudshouter
Group=cloudshouter
WorkingDirectory=/opt/cloud-remote-shouter/app
EnvironmentFile=/etc/cloud-remote-shouter.env
ExecStart=/opt/cloud-remote-shouter/venv/bin/python /opt/cloud-remote-shouter/app/server/app.py
Restart=always
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/cloud-remote-shouter

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now cloud-remote-shouter.service

echo "[5/8] 检查应用服务..."
for attempt in {1..30}; do
  if curl --fail --silent --show-error http://127.0.0.1:8765/api/bootstrap >/dev/null; then
    break
  fi
  if [[ $attempt -eq 30 ]]; then
    journalctl -u cloud-remote-shouter.service --no-pager -n 80 >&2
    fail "应用服务未正常启动。"
  fi
  sleep 1
done

echo "[6/8] 配置 Nginx 和 WebSocket..."
cat > /etc/nginx/sites-available/cloud-remote-shouter <<EOF
server {
    listen 80;
    listen [::]:80;
    server_name $DOMAIN;
    client_max_body_size 5m;

    location /ws/ {
        proxy_pass http://127.0.0.1:8765;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 75s;
        proxy_send_timeout 75s;
    }

    location = /api/receiver/update/download {
        proxy_pass http://127.0.0.1:8765;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_buffering off;
        proxy_read_timeout 600s;
        proxy_send_timeout 600s;
    }

    location / {
        proxy_pass http://127.0.0.1:8765;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 30s;
    }
}
EOF
ln -sfn /etc/nginx/sites-available/cloud-remote-shouter /etc/nginx/sites-enabled/cloud-remote-shouter
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl enable --now nginx
systemctl reload nginx

echo "[7/8] 申请并启用免费 HTTPS 证书..."
certbot --nginx --non-interactive --agree-tos --register-unsafely-without-email --redirect -d "$DOMAIN"
systemctl enable --now certbot.timer

echo "[8/8] 配置每日数据库备份..."
cat > /usr/local/sbin/cloud-remote-shouter-backup <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
DATABASE=/var/lib/cloud-remote-shouter/cloud-remote-shouter.db
BACKUP_DIR=/var/backups/cloud-remote-shouter
[[ -f "$DATABASE" ]] || exit 0
install -d -o cloudshouter -g cloudshouter -m 0750 "$BACKUP_DIR"
TARGET="$BACKUP_DIR/cloud-remote-shouter-$(date +%Y%m%d-%H%M%S).db"
sqlite3 "$DATABASE" ".backup '$TARGET'"
chown cloudshouter:cloudshouter "$TARGET"
chmod 0640 "$TARGET"
find "$BACKUP_DIR" -type f -name 'cloud-remote-shouter-*.db' -mtime +14 -delete
EOF
chmod 0755 /usr/local/sbin/cloud-remote-shouter-backup

cat > /etc/systemd/system/cloud-remote-shouter-backup.service <<'EOF'
[Unit]
Description=Back up ICeCream Shouter database

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/cloud-remote-shouter-backup
EOF

cat > /etc/systemd/system/cloud-remote-shouter-backup.timer <<'EOF'
[Unit]
Description=Daily ICeCream Shouter database backup

[Timer]
OnCalendar=*-*-* 03:20:00
Persistent=true
RandomizedDelaySec=10m

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now cloud-remote-shouter-backup.timer
/usr/local/sbin/cloud-remote-shouter-backup

curl --fail --silent --show-error "https://$DOMAIN/api/bootstrap" >/dev/null || fail "HTTPS 外部检查失败。请检查腾讯云防火墙是否已放行 80 和 443。"

echo
echo "国内服务器部署完成。"
echo "SYSTEM_URL=https://$DOMAIN"
