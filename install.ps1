# Compila SamaDash, lo installa in %LOCALAPPDATA%\Programs\SamaDash e lo avvia.
# Uso: powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\SamaDash'
$publish = Join-Path $PSScriptRoot 'bin\publish'

dotnet publish (Join-Path $PSScriptRoot 'SamaDash.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build fallita' }

Get-Process SamaDash -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $publish '*') $target -Recurse -Force
$exe = Join-Path $target 'SamaDash.exe'

# Collegamento nel menu Start: avviarlo con SamaDash gia' aperto mostra la barra
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'SamaDash.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $target
$shortcut.Save()

Start-Process $exe
Write-Host "SamaDash installato in $target"
