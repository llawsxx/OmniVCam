param([switch]$Isolated)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$vsRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community'
$msbuild = Join-Path $vsRoot 'MSBuild\Current\Bin\MSBuild.exe'
# Some shells supply both PATH and Path; .NET Framework MSBuild cannot spawn CL with that environment.
[Environment]::SetEnvironmentVariable('Path', $null, 'Process')
New-Item -ItemType Directory -Force tests/out | Out-Null
$controllerDir = 'OmniVCamController\bin\Debug'
$nativeArgs = @()
if ($Isolated) {
    $controllerDir = 'tests\out\controller'
    $nativeArgs = @("/p:OutDir=$projectRoot\tests\out\native\")
}
& $msbuild OmniVCamController/OmniVCamController.csproj /t:Build /p:Configuration=Debug "/p:OutputPath=$projectRoot\$controllerDir\" /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Controller build failed' }
& $msbuild OmniVCam/OmniVCam.vcxproj /t:Build /p:Configuration=Debug /p:Platform=x64 "/p:SolutionDir=$projectRoot\" @nativeArgs /v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
$compile = 'call "' + $vsRoot + '\VC\Auxiliary\Build\vcvars64.bat" >nul && cl /nologo /MTd /EHsc /I ffmpeg_deps\include tests\CalibrationCardFixture.cpp OmniVCam\x64\Debug\TestCard.obj OmniVCam\x64\Debug\qrcodegen.obj /Fo:tests\out\fixture.obj /Fe:tests\out\fixture.exe /link /LIBPATH:ffmpeg_deps\lib avutil.lib swscale.lib gdi32.lib user32.lib'
& cmd.exe /d /c $compile
if ($LASTEXITCODE -ne 0) { throw 'Native fixture build failed' }
$env:PATH = (Join-Path $projectRoot 'x64\Debug') + ';' + $env:PATH
& ./tests/out/fixture.exe
if ($LASTEXITCODE -ne 0) { throw 'Native frame generation failed' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$controllerDir\zxing.dll" /out:tests\out\CalibrationChecks.exe tests\CalibrationChecks.cs
if ($LASTEXITCODE -ne 0) { throw 'Managed checks build failed' }
Copy-Item -LiteralPath "$controllerDir\zxing.dll" -Destination tests/out/zxing.dll
& ./tests/out/CalibrationChecks.exe "$controllerDir\OmniVCamController.exe"
if ($LASTEXITCODE -ne 0) { throw 'Calibration checks failed' }
