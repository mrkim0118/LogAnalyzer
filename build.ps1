param([switch]$Test)
$ErrorActionPreference = 'Stop'
$buildTool = 'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $buildTool)) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $buildTool = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $buildTool) { throw 'Visual Studio MSBuild를 찾을 수 없습니다.' }
if ($Test) {
    & $buildTool "$PSScriptRoot\Tests\Tests.csproj" /t:Build /p:Configuration=Release /nologo /v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & "$PSScriptRoot\Tests\bin\Release\Tests.exe"
    exit $LASTEXITCODE
}
& $buildTool "$PSScriptRoot\LogAnalyzer.csproj" /t:Build /p:Configuration=Release /nologo /v:minimal
exit $LASTEXITCODE
