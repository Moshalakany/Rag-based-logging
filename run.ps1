$ErrorActionPreference = "Continue"
$rootPath = $PSScriptRoot
$apiPath = Join-Path $rootPath "LogRag.Api"
$uiPath = Join-Path $rootPath "lograg-ui"

Write-Host "Starting frontend..."
Write-Host "Location: $uiPath"
Start-Process -FilePath "cmd.exe" -ArgumentList "/k", "npm install && npm start" -WorkingDirectory $uiPath

Write-Host "Starting LogRag.Api..."
Write-Host "Location: $apiPath"
Start-Process -FilePath "cmd.exe" -ArgumentList "/k", "dotnet run" -WorkingDirectory $apiPath
