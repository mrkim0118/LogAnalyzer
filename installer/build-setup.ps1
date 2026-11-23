param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectFolder = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) {
    $candidates = @(
        (Join-Path $projectFolder '.tools\InnoSetup\ISCC.exe'),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
    )
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup ISCC.exe 경로를 -CompilerPath로 지정해 주세요.'
}
& (Join-Path $projectFolder 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $CompilerPath (Join-Path $PSScriptRoot 'LogAnalyzer.iss')
exit $LASTEXITCODE
