$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
Push-Location -LiteralPath $taskRoot
try {
    & $taskDotnet build TTPlayerSerialAss.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $taskDotnet run --project tests\TTSerial.Tests -c Release --no-build -- $taskRoot
    if ($LASTEXITCODE -ne 0) { throw 'Core / IO checks failed.' }
    $taskOutput = Join-Path $taskRoot ('artifacts\verification-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    $taskExe = Join-Path $taskRoot 'src\TTSerial.App\bin\Release\net10.0-windows\TTPlayerSerialAss.exe'
    $taskRun = Start-Process -FilePath $taskExe -ArgumentList ('--smoke-test "' + $taskOutput + '"') -WindowStyle Hidden -PassThru
    $taskRun.WaitForExit()
    if ($taskRun.ExitCode -ne 0) { throw ('WPF checks failed: ' + $taskOutput) }
    Get-Content -LiteralPath (Join-Path $taskOutput 'result.json')
    Write-Output ('Images and reports: ' + $taskOutput)
} finally { Pop-Location }
