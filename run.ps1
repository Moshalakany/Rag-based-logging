$ErrorActionPreference = "Continue"
$rootPath = $PSScriptRoot
$apiPath = Join-Path $rootPath "LogRag.Api"
$uiPath = Join-Path $rootPath "lograg-ui"

function Test-PortOpen($Port) {
    try {
        $tcpClient = New-Object System.Net.Sockets.TcpClient
        $connect = $tcpClient.BeginConnect("127.0.0.1", $Port, $null, $null)
        $wait = $connect.AsyncWaitHandle.WaitOne(1000, $false)
        if ($wait -and $tcpClient.Connected) {
            $tcpClient.EndConnect($connect)
            $tcpClient.Close()
            return $true
        }
        $tcpClient.Close()
        return $false
    }
    catch {
        return $false
    }
}

# Helper to verify if Docker Daemon is running
function Test-DockerRunning {
    $process = Start-Process -FilePath "docker" -ArgumentList "info" -NoNewWindow -PassThru -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    $process.WaitForExit(3000)
    return $process.ExitCode -eq 0
}

# Helper to start/ensure a Docker container is running
function Ensure-DockerContainer($ContainerName, $ImageName, $PortMapping, $ExtraArgs = @()) {
    Write-Host "[INFO] Checking Docker container status for '$ContainerName'..." -ForegroundColor Cyan
    
    # Check if container exists (including stopped ones)
    $containerExists = (docker ps -a -q -f "name=^/$ContainerName$") -ne $null
    
    if (-not $containerExists) {
        Write-Host "[INFO] Creating and starting new container '$ContainerName' from image '$ImageName'..." -ForegroundColor Yellow
        $dockerArgs = @("run", "-d", "--name", $ContainerName) + $PortMapping + $ExtraArgs + @($ImageName)
        docker $dockerArgs
    }
    else {
        # Check if container is running
        $isRunning = (docker ps -q -f "name=^/$ContainerName$") -ne $null
        if (-not $isRunning) {
            Write-Host "[INFO] Container '$ContainerName' is stopped. Starting it..." -ForegroundColor Yellow
            docker start $ContainerName
        }
        else {
            Write-Host "[SUCCESS] Container '$ContainerName' is already running." -ForegroundColor Green
        }
    }
}

# Helper to wait for a port to become ready
function Wait-PortReady($Port, $TimeoutSeconds = 15) {
    Write-Host "[INFO] Waiting for port $Port to respond..." -ForegroundColor Cyan
    $elapsed = 0
    while (-not (Test-PortOpen $Port) -and $elapsed -lt $TimeoutSeconds) {
        Start-Sleep -Seconds 1
        $elapsed++
    }
    if (Test-PortOpen $Port) {
        Write-Host "[SUCCESS] Port $Port is ready." -ForegroundColor Green
        return $true
    }
    else {
        Write-Host "[WARNING] Port $Port did not respond within $TimeoutSeconds seconds." -ForegroundColor Red
        return $false
    }
}

# 1. Manage Qdrant DB Dependency
$qdrantPort = 6333
Write-Host "`n=== [Step 1: Check Qdrant DB] ===" -ForegroundColor DarkCyan
if (Test-PortOpen $qdrantPort) {
    Write-Host "[SUCCESS] Qdrant is already running on port $qdrantPort." -ForegroundColor Green
}
else {
    Write-Host "[WARNING] Qdrant is not running on port $qdrantPort." -ForegroundColor Yellow
    if (Test-DockerRunning) {
        Ensure-DockerContainer -ContainerName "qdrant" -ImageName "qdrant/qdrant" -PortMapping @("-p", "6333:6333", "-p", "6334:6334")
        Wait-PortReady $qdrantPort
    }
    else {
        Write-Host "[ERROR] Docker is not running. Please start Docker Desktop to enable Qdrant DB." -ForegroundColor Red
    }
}

# 2. Manage Ollama Model Server Dependency
$ollamaPort = 11434
Write-Host "`n=== [Step 2: Check Ollama Model Server] ===" -ForegroundColor DarkCyan
if (Test-PortOpen $ollamaPort) {
    Write-Host "[SUCCESS] Ollama is already running on port $ollamaPort." -ForegroundColor Green
}
else {
    Write-Host "[WARNING] Ollama is not running on port $ollamaPort." -ForegroundColor Yellow
    if (Test-DockerRunning) {
        Ensure-DockerContainer -ContainerName "ollama" -ImageName "ollama/ollama" -PortMapping @("-p", "11434:11434") -ExtraArgs @("-v", "ollama:/root/.ollama")
        Wait-PortReady $ollamaPort
    }
    else {
        # Check if native Ollama app is installed and try launching it
        $nativeOllama = Get-Command "ollama" -ErrorAction SilentlyContinue
        if ($null -ne $nativeOllama) {
            Write-Host "[INFO] Launching local native Ollama instance..." -ForegroundColor Yellow
            Start-Process -FilePath "ollama" -ArgumentList "serve" -NoNewWindow
            Wait-PortReady $ollamaPort
        }
        else {
            Write-Host "[ERROR] Docker is not running and native Ollama CLI is not found. Please start Ollama." -ForegroundColor Red
        }
    }
}

# 3. Start Frontend & Backend
Write-Host "`n=== [Step 3: Launching Applications] ===" -ForegroundColor DarkCyan

Write-Host "Starting frontend lograg-ui..." -ForegroundColor Green
Write-Host "Location: $uiPath"
Start-Process -FilePath "cmd.exe" -ArgumentList "/k", "npm install && npm start" -WorkingDirectory $uiPath

Write-Host "Starting LogRag.Api..." -ForegroundColor Green
Write-Host "Location: $apiPath"
Start-Process -FilePath "cmd.exe" -ArgumentList "/k", "dotnet run" -WorkingDirectory $apiPath

Write-Host "`nSetup complete! Frontend and API processes launched." -ForegroundColor Green
