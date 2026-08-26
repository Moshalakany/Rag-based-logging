#!/usr/bin/env bash
# =============================================================================
# LogRag — Azure VM Provisioning Script
# =============================================================================
# Creates an Azure VM and deploys LogRag via custom script extension.
# Prerequisites: Azure CLI installed and logged in (az login)
#
# Usage:
#   bash azure-deploy.sh <resource-group-name> [github-pat]
#
# Examples:
#   bash azure-deploy.sh lograg-prod ghp_xxxxxxxxxxxx
#   bash azure-deploy.sh lograg-prod              # prompts for PAT if repo is private
# =============================================================================

set -euo pipefail

# ── Config ──────────────────────────────────────────────────────────────────
RESOURCE_GROUP="${1:?Usage: $0 <resource-group-name> [github-pat]}"
GITHUB_PAT="${2:-}"
VM_NAME="lograg-vm"
LOCATION="eastus"
VM_SIZE="Standard_D4s_v3"
ADMIN_USER="azureuser"
IMAGE="Canonical:ubuntu-22_04-lts:server:latest"

# Prompt for PAT if not provided
if [ -z "${GITHUB_PAT}" ]; then
    read -rsp "GitHub PAT (or press Enter for public repo): " GITHUB_PAT
    echo ""
fi
SETUP_SCRIPT="./azure-setup.sh"

# ── Create resource group ───────────────────────────────────────────────────
echo "Creating resource group: ${RESOURCE_GROUP} in ${LOCATION}..."
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" --output none

# ── Create VM ───────────────────────────────────────────────────────────────
echo "Creating VM: ${VM_NAME} (${VM_SIZE})..."
az vm create \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${VM_NAME}" \
    --image "${IMAGE}" \
    --size "${VM_SIZE}" \
    --admin-username "${ADMIN_USER}" \
    --generate-ssh-keys \
    --public-ip-sku Standard \
    --output table

# Get public IP
PUBLIC_IP=$(az vm show \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${VM_NAME}" \
    --show-details \
    --query "publicIps" \
    --output tsv)

echo "VM created. Public IP: ${PUBLIC_IP}"

# ── Open ports ──────────────────────────────────────────────────────────────
echo "Opening firewall ports..."
az vm open-port \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${VM_NAME}" \
    --port 5000,4200,11434,6333 \
    --priority 1000 \
    --output none

# ── Upload and run setup script ─────────────────────────────────────────────
echo "Uploading setup script to VM..."
PAT_ARG=""
if [ -n "${GITHUB_PAT}" ]; then
    PAT_ARG="export GITHUB_PAT='${GITHUB_PAT}' && "
    echo "  GitHub PAT will be passed to VM (hidden from logs)."
fi
az vm extension set \
    --resource-group "${RESOURCE_GROUP}" \
    --vm-name "${VM_NAME}" \
    --name customScript \
    --publisher Microsoft.Azure.Extensions \
    --version 2.1 \
    --settings "{\"fileUris\": [\"file://${SETUP_SCRIPT}\"]}" \
    --protected-settings "{\"commandToExecute\": \"${PAT_ARG}bash azure-setup.sh\"}" \
    --output table

# Note: The file:// URI above only works if the script is on the same machine.
# For production use, upload the script to a storage account or GitHub first:
#   --settings '{"fileUris": ["https://raw.githubusercontent.com/Moshalakany/Rag-based-logging/main/azure-setup.sh"]}'

# ── Done ────────────────────────────────────────────────────────────────────
echo ""
echo "========================================"
echo "  Azure VM Provisioning Started!"
echo "========================================"
echo ""
echo "  VM:          ${VM_NAME}"
echo "  Resource:    ${RESOURCE_GROUP}"
echo "  Location:    ${LOCATION}"
echo "  Public IP:   ${PUBLIC_IP}"
echo ""
echo "  SSH:         ssh ${ADMIN_USER}@${PUBLIC_IP}"
echo "  API:         http://${PUBLIC_IP}:5000/health"
echo "  Frontend:    http://${PUBLIC_IP}:4200"
echo ""
echo "  Setup logs:  ssh ${ADMIN_USER}@${PUBLIC_IP} 'tail -f /var/log/lograg-setup.log'"
echo "  (Setup runs in background — wait ~10-15 min for model downloads)"
echo "========================================"
