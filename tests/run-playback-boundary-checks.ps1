param([switch]$ShortMedia)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
[Environment]::SetEnvironmentVariable('Path', $null, 'Process')
New-Item -ItemType Directory -Force tests/out | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $vsRoot) { throw 'Visual Studio C++ tools were not found' }
$vcvars = Join-Path $vsRoot 'VC/Auxiliary/Build/vcvars64.bat'
$msbuild = Join-Path $vsRoot 'MSBuild/Current/Bin/MSBuild.exe'
& $msbuild OmniVCam/OmniVCam.vcxproj /t:Build /p:Configuration=Debug /p:Platform=x64 "/p:SolutionDir=$projectRoot\" "/p:OutDir=$projectRoot\tests\out\native\" /v:quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Native camera build failed' }
$engineObjects = @('Threads', 'ParseConfig', 'TestCard', 'qrcodegen', 'video_frame', 'OBSVirtualCamReader', 'shared-memory-queue', 'dshow_source', 'dshow_camera', 'dshow_grabber', 'clock', 'Utils') | ForEach-Object { 'OmniVCam/x64/Debug/' + $_ + '.obj' }
$compile = 'call "' + $vcvars + '" >nul && cl /nologo /w /Gy /Gw /MTd /I ffmpeg_deps/include tests/PlaybackBoundaryChecks.c ' + ($engineObjects -join ' ') + ' /Fo:tests/out/PlaybackBoundaryChecks.obj /Fe:tests/out/PlaybackBoundaryChecks.exe /link /OPT:REF /ignore:4099 /LIBPATH:ffmpeg_deps/lib /LIBPATH:deps/lib/x64 avutil.lib avformat.lib avcodec.lib avfilter.lib avdevice.lib swscale.lib swresample.lib ws2_32.lib strmbasd.lib ole32.lib oleaut32.lib strmiids.lib quartz.lib user32.lib advapi32.lib winmm.lib gdi32.lib'
& cmd.exe /d /c $compile
if ($LASTEXITCODE -ne 0) { throw 'Native playback fixture build failed' }
$env:PATH = (Join-Path $projectRoot 'x64/Debug') + ';' + $env:PATH
& ./tests/out/PlaybackBoundaryChecks.exe
if ($LASTEXITCODE -ne 0) { throw 'Native playback boundary checks failed' }
if ($ShortMedia) {
    & python tests/short_media_checks.py
    if ($LASTEXITCODE -ne 0) { throw 'Short real-media integration checks failed' }
}
