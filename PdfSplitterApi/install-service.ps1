# install-service.ps1 — Installe PDF Splitter API (C#) en tant que service Windows
# Executer en tant qu'administrateur

param(
    [ValidateSet("install", "uninstall", "start", "stop", "status")]
    [string]$Action = "install"
)

$ErrorActionPreference = "Stop"
$ServiceName = "PdfSplitterApi"
$DisplayName = "PDF Splitter API"
$Description = "Service REST de decoupe, extraction et fusion de PDF (ASP.NET Core sur le port 8000)."
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step($msg) { Write-Host "[*] $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "[+] $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "[-] $msg" -ForegroundColor Red }

switch ($Action) {
    "install" {
        # Publier l'application
        Write-Step "Publication de l'application..."
        $publishDir = Join-Path $ScriptDir "publish"
        dotnet publish $ScriptDir -c Release -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            Write-Err "Echec de la publication."
            exit 1
        }

        $exePath = Join-Path $publishDir "PdfSplitterApi.exe"
        if (-not (Test-Path $exePath)) {
            Write-Err "Executable introuvable : $exePath"
            exit 1
        }

        # Supprimer le service existant s'il existe
        $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Step "Service existant detecte, suppression..."
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            sc.exe delete $ServiceName | Out-Null
            Start-Sleep -Seconds 2
        }

        # Creer le service
        Write-Step "Creation du service $ServiceName..."
        New-Service -Name $ServiceName `
            -BinaryPathName "$exePath --urls http://*:8000" `
            -DisplayName $DisplayName `
            -Description $Description `
            -StartupType Automatic

        # Configurer le redemarrage automatique en cas d'echec
        sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

        # Demarrer le service
        Write-Step "Demarrage du service..."
        Start-Service -Name $ServiceName

        Write-Ok "Service $ServiceName installe et demarre."
        Write-Ok "API disponible sur http://localhost:8000"
        Write-Ok "Health check : http://localhost:8000/health"
    }

    "uninstall" {
        Write-Step "Arret du service..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

        Write-Step "Suppression du service..."
        sc.exe delete $ServiceName
        Write-Ok "Service $ServiceName supprime."
    }

    "start" {
        Start-Service -Name $ServiceName
        Write-Ok "Service $ServiceName demarre."
    }

    "stop" {
        Stop-Service -Name $ServiceName -Force
        Write-Ok "Service $ServiceName arrete."
    }

    "status" {
        $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($svc) {
            Write-Host "Service : $($svc.DisplayName)"
            Write-Host "Etat    : $($svc.Status)"
            Write-Host "Type    : $($svc.StartType)"

            $listening = netstat -ano | Select-String ":8000.*LISTENING"
            if ($listening) {
                Write-Ok "Port 8000 en ecoute."
            } else {
                Write-Err "Port 8000 pas en ecoute."
            }
        } else {
            Write-Err "Service $ServiceName non installe."
        }
    }
}
