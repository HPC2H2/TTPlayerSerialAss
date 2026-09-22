$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
Push-Location -LiteralPath $taskRoot
try {
    & $taskDotnet publish src\TTSerial.App\TTSerial.App.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination publish\win-x64\README.md -Force
    New-Item -ItemType Directory -Path publish\win-x64\docs\validation -Force | Out-Null
    foreach ($taskImage in @('classic.png', 'iblue.png', 'hifi.png')) {
        Copy-Item -LiteralPath (Join-Path docs\validation $taskImage) -Destination (Join-Path publish\win-x64\docs\validation $taskImage) -Force
    }
    Copy-Item -LiteralPath resources\skins\来源说明.md -Destination publish\win-x64\Skins\来源说明.md -Force
    Write-Output 'Ready: publish\win-x64\TTPlayerSerialAss.exe'
} finally { Pop-Location }
