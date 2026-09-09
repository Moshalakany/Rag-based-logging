#!/usr/bin/env bash
# =============================================================================
# LogRag — Azure VM Setup Script
# =============================================================================
# Usage:
#   1. Create an Azure Ubuntu 22.04 VM (Standard_D4s_v3 or better)
#   2. SSH in or use Azure Custom Script Extension:
#      az vm extension set --resource-group <rg> --vm-name <vm> \
#          --name customScript --publisher Microsoft.Azure.Extensions \
#          --settings '{"fileUris": ["https://raw.githubusercontent.com/.../setup.sh"]}' \
#          --protected-settings '{"commandToExecute": "bash setup.sh"}'
#   3. Or use as cloud-init: az vm create --custom-data setup.sh
#
# Installs: Docker, .NET 8, Node.js 22, Qdrant, Ollama (with models)
# Clones the project, builds API + frontend, starts everything as services.
# =============================================================================

set -euo pipefail

# ── Config ──────────────────────────────────────────────────────────────────
GITHUB_REPO="https://github.com/Moshalakany/Rag-based-logging.git"
# For PRIVATE repos, set one of these before running:
#   export GITHUB_PAT="ghp_xxxxxxxxxxxx"        # Personal Access Token
#   export GITHUB_SSH_KEY="/path/to/deploy_key"   # SSH deploy key
GITHUB_PAT="${GITHUB_PAT:-}"           # Set via environment or Azure Key Vault
GITHUB_SSH_KEY="${GITHUB_SSH_KEY:-}"   # Alternative: SSH deploy key path

PROJECT_DIR="/opt/lograg"
API_DIR="${PROJECT_DIR}/LogRag.Api"
UI_DIR="${PROJECT_DIR}/lograg-ui"
LOG_FILE="/var/log/lograg-setup.log"

# Ollama models (comma-separated)
OLLAMA_MODELS="nomic-embed-text,llama3.2"

# Ports
API_PORT=5000
QDRANT_PORT=6333
OLLAMA_PORT=11434
MONGO_PORT=27017

# ── Logging ─────────────────────────────────────────────────────────────────
exec > >(tee -a "${LOG_FILE}") 2>&1
echo "=== LogRag Azure VM Setup — $(date -u +%Y-%m-%dT%H:%M:%SZ) ==="

# ── System updates ──────────────────────────────────────────────────────────
echo "[1/9] Updating system packages..."
apt-get update -y
apt-get upgrade -y

# ── Install Docker ──────────────────────────────────────────────────────────
echo "[2/9] Installing Docker..."
if ! command -v docker &>/dev/null; then
    curl -fsSL https://get.docker.com | sh
    usermod -aG docker azureuser || usermod -aG docker "${USER}"
    systemctl enable docker
    systemctl start docker
fi
docker --version

# ── Install .NET 8 SDK ──────────────────────────────────────────────────────
echo "[3/9] Installing .NET 8 SDK..."
if ! command -v dotnet &>/dev/null; then
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm /tmp/packages-microsoft-prod.deb
    apt-get update -y
    apt-get install -y dotnet-sdk-8.0
fi
dotnet --version

# ── Install Node.js ─────────────────────────────────────────────────────────
echo "[4/9] Installing Node.js 22..."
if ! command -v node &>/dev/null; then
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
    apt-get install -y nodejs
fi
node --version
npm --version

# ── Start MongoDB (Docker) ──────────────────────────────────────────────────
echo "[5/9] Starting MongoDB..."
docker rm -f mongodb 2>/dev/null || true
docker run -d --restart unless-stopped \
    --name mongodb \
    -p ${MONGO_PORT}:27017 \
    -v mongodb_data:/data/db \
    mongo:latest

# ── Start Qdrant (Docker) ──────────────────────────────────────────────────
echo "[6/9] Starting Qdrant..."
docker rm -f qdrant 2>/dev/null || true
docker run -d --restart unless-stopped \
    --name qdrant \
    -p ${QDRANT_PORT}:6333 \
    -p 6334:6334 \
    -v qdrant_storage:/qdrant/storage \
    qdrant/qdrant

# ── Start Ollama (Docker) ───────────────────────────────────────────────────
echo "[7/9] Starting Ollama and pulling models..."
docker rm -f ollama 2>/dev/null || true
docker run -d --restart unless-stopped \
    --name ollama \
    -p ${OLLAMA_PORT}:11434 \
    -v ollama_data:/root/.ollama \
    ollama/ollama serve

# Wait for Ollama to be ready
echo "  Waiting for Ollama to start..."
for i in $(seq 1 30); do
    if curl -s http://localhost:${OLLAMA_PORT}/api/tags &>/dev/null; then
        echo "  Ollama is ready."
        break
    fi
    sleep 2
done

# Pull models
IFS=',' read -ra MODELS <<< "${OLLAMA_MODELS}"
for model in "${MODELS[@]}"; do
    echo "  Pulling model: ${model}..."
    docker exec ollama ollama pull "${model}"
done
echo "  Models installed:"
docker exec ollama ollama list

# ── Clone project ───────────────────────────────────────────────────────────
echo "[8/9] Cloning project from GitHub..."
if [ -n "${GITHUB_PAT}" ]; then
    # Private repo via Personal Access Token
    CLONE_URL=$(echo "${GITHUB_REPO}" | sed "s|https://|https://${GITHUB_PAT}@|")
    echo "  Cloning with PAT (token hidden)..."
elif [ -n "${GITHUB_SSH_KEY}" ] && [ -f "${GITHUB_SSH_KEY}" ]; then
    # Private repo via SSH deploy key
    chmod 600 "${GITHUB_SSH_KEY}"
    export GIT_SSH_COMMAND="ssh -i ${GITHUB_SSH_KEY} -o StrictHostKeyChecking=no"
    CLONE_URL=$(echo "${GITHUB_REPO}" | sed 's|https://github.com/|git@github.com:|')
    echo "  Cloning with SSH deploy key..."
else
    # Public repo or already-configured git credentials
    CLONE_URL="${GITHUB_REPO}"
    echo "  Cloning (public / pre-configured)..."
fi

if [ -d "${PROJECT_DIR}" ]; then
    echo "  Project directory exists, pulling latest..."
    cd "${PROJECT_DIR}"
    git remote set-url origin "${CLONE_URL}" 2>/dev/null || true
    git pull origin main 2>/dev/null || git pull origin master
else
    git clone "${CLONE_URL}" "${PROJECT_DIR}"
fi

# ── Build API ───────────────────────────────────────────────────────────────
echo "  Building .NET API..."
cd "${API_DIR}"
dotnet restore
dotnet publish -c Release -o "${API_DIR}/publish"

# ── Install frontend deps ───────────────────────────────────────────────────
echo "  Installing frontend dependencies..."
cd "${UI_DIR}"
npm ci --omit=dev 2>/dev/null || npm install

# ── Create systemd services ─────────────────────────────────────────────────
echo "[9/9] Creating systemd services for auto-start..."

# API service
cat > /etc/systemd/system/lograg-api.service << 'SERVICE_EOF'
[Unit]
Description=LogRag .NET API
After=network.target docker.service
Requires=docker.service
Wants=qdrant.service ollama.service

[Service]
Type=simple
WorkingDirectory=/opt/lograg/LogRag.Api
Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
Environment=DOTNET_ENVIRONMENT=Production
ExecStart=/usr/bin/dotnet /opt/lograg/LogRag.Api/publish/LogRag.Api.dll
Restart=on-failure
RestartSec=10
User=root

[Install]
WantedBy=multi-user.target
SERVICE_EOF

# Frontend service (using npx serve for production static serving)
# Note: For production, build the Angular app and serve with nginx instead.
# This is a simplified dev setup.
cat > /etc/systemd/system/lograg-ui.service << 'SERVICE_EOF'
[Unit]
Description=LogRag Angular Frontend
After=network.target lograg-api.service
Requires=lograg-api.service

[Service]
Type=simple
WorkingDirectory=/opt/lograg/lograg-ui
ExecStart=/usr/bin/npx ng serve --host 0.0.0.0 --port 4200 --disable-host-check
Restart=on-failure
RestartSec=10
User=root

[Install]
WantedBy=multi-user.target
SERVICE_EOF

# Docker container systemd dependencies
for svc in qdrant ollama; do
    cat > "/etc/systemd/system/${svc}.service" << SVCEOF
[Unit]
Description=${svc} Docker Container
After=docker.service
Requires=docker.service

[Service]
Type=simple
ExecStart=/usr/bin/docker start -a ${svc}
ExecStop=/usr/bin/docker stop ${svc}
Restart=on-failure
RestartSec=5
User=root

[Install]
WantedBy=multi-user.target
SVCEOF
done

systemctl daemon-reload
systemctl enable qdrant ollama lograg-api lograg-ui

# ── Start services ──────────────────────────────────────────────────────────
echo "Starting services..."
systemctl start lograg-api
systemctl start lograg-ui

# ── Open firewall ports ─────────────────────────────────────────────────────
echo "Configuring firewall..."
ufw allow ${API_PORT}/tcp comment 'LogRag API'
ufw allow 4200/tcp comment 'LogRag UI'
ufw allow ${QDRANT_PORT}/tcp comment 'Qdrant'
ufw --force enable || true

# ── Done ────────────────────────────────────────────────────────────────────
echo ""
echo "========================================"
echo "  LogRag Setup Complete!"
echo "========================================"
echo ""
echo "  API:       http://$(hostname -I | awk '{print $1}'):${API_PORT}/health"
echo "  Frontend:  http://$(hostname -I | awk '{print $1}'):4200"
echo "  Qdrant:    http://localhost:${QDRANT_PORT}"
echo "  Ollama:    http://localhost:${OLLAMA_PORT}"
echo ""
echo "  Log file:  ${LOG_FILE}"
echo "  Project:   ${PROJECT_DIR}"
echo ""
echo "  Check status: systemctl status lograg-api lograg-ui"
echo "  View logs:    journalctl -u lograg-api -f"
echo "========================================"
