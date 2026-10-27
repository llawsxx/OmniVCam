$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
# Some hosts supply both PATH and Path; MSBuild cannot spawn the compiler with duplicate keys.
[Environment]::SetEnvironmentVariable('Path', $null, 'Process')
$controllerDir = Join-Path $projectRoot 'tests\out\controller'
New-Item -ItemType Directory -Force $controllerDir | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found' }
& $msbuild OmniVCamController/OmniVCamController.csproj /t:Build /p:Configuration=Debug "/p:OutputPath=$controllerDir\" /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Controller build failed' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /out:tests\out\InteractivePlaybackChecks.exe tests\InteractivePlaybackChecks.cs tests\InteractiveObsChecks.cs tests\InteractiveFileGroupPhaseChecks.cs tests\InteractiveComboChecks.cs tests\InteractiveTransitionModeChecks.cs tests\InteractiveGroupRenderingChecks.cs tests\InteractiveOverlapChecks.cs tests\InteractiveIgnoreRetriggerChecks.cs tests\InteractiveTriggerDeliveryChecks.cs
if ($LASTEXITCODE -ne 0) { throw 'Interactive checks build failed' }
& ./tests/out/InteractivePlaybackChecks.exe "$controllerDir\OmniVCamController.exe"
if ($LASTEXITCODE -ne 0) { throw 'Interactive checks failed' }
