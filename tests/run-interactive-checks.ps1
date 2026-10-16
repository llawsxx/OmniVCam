$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$controllerDir = Join-Path $projectRoot 'tests\out\controller'
New-Item -ItemType Directory -Force $controllerDir | Out-Null
$msbuild = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild OmniVCamController/OmniVCamController.csproj /t:Build /p:Configuration=Debug "/p:OutputPath=$controllerDir\" /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Controller build failed' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.Linq.dll /out:tests\out\InteractivePlaybackChecks.exe tests\InteractivePlaybackChecks.cs
if ($LASTEXITCODE -ne 0) { throw 'Interactive checks build failed' }
& ./tests/out/InteractivePlaybackChecks.exe "$controllerDir\OmniVCamController.exe"
if ($LASTEXITCODE -ne 0) { throw 'Interactive checks failed' }
