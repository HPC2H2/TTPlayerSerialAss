$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'WpfEnvironmentCheck.csproj'
$reportDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\docs\environment\verification'))
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

dotnet build $projectFile --configuration Release --nologo 2>&1 |
    Tee-Object -FilePath (Join-Path $reportDirectory 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'WPF build failed. See build.log.' }

$checkExecutable = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\WpfEnvironmentCheck.exe'
$checkArguments = '--verify "{0}"' -f $reportDirectory
$checkProcess = Start-Process -FilePath $checkExecutable -ArgumentList $checkArguments -WindowStyle Hidden -Wait -PassThru
if ($checkProcess.ExitCode -ne 0) { throw 'WPF runtime check failed. See verification-error.txt.' }
Get-Content -LiteralPath (Join-Path $reportDirectory 'verification.json')
