param(
    [string]$ProbePath = '',
    [switch]$BuildTests
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
if (-not $ProbePath) {
    $ProbePath = Join-Path $PSScriptRoot 'vendor\ffprobe.exe'
    if (-not (Test-Path -LiteralPath $ProbePath)) {
        $ProbePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ffprobe.exe'
    }
}
if (-not (Test-Path -LiteralPath $ProbePath)) { throw '请先运行 scripts/build-ffprobe.sh，或使用 -ProbePath 指定 ffprobe.exe。' }
$output = Join-Path $PSScriptRoot 'build\本地视频拉黑'
if (Test-Path -LiteralPath $output) { throw 'build/本地视频拉黑 已存在，请先移走旧构建结果。' }
$support = Join-Path $output 'app'
New-Item -ItemType Directory -Path $support -Force | Out-Null
$sources = @('AssemblyInfo.cs', 'Core.cs', 'WindowsRecycle.cs', 'Program.cs') | ForEach-Object { Join-Path $PSScriptRoot ('src\' + $_) }
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
$common = @('/nologo', '/utf8output', '/platform:x64', '/optimize+', '/debug-', '/warnaserror+')
& $compiler @common /target:winexe "/out:$output\本地视频拉黑.exe" "/win32manifest:$PSScriptRoot\src\app.manifest" @references @sources
if ($LASTEXITCODE -ne 0) { throw '程序编译失败。' }
Copy-Item -LiteralPath $ProbePath -Destination (Join-Path $support 'ffprobe.exe')
[IO.File]::WriteAllBytes((Join-Path $output 'blacklist.txt'), [byte[]]::new(0))
if ($BuildTests) {
    $testOutput = Join-Path $PSScriptRoot 'build\tests'
    New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
    & $compiler @common /target:exe /main:Tests "/out:$testOutput\Tests.exe" @references @sources (Join-Path $PSScriptRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
    $wpf = Join-Path (Split-Path -Parent $compiler) 'WPF'
    & $compiler @common /target:exe "/out:$testOutput\PortableSmoke.exe" /r:System.dll /r:System.Core.dll "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" (Join-Path $PSScriptRoot 'tests\PortableSmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw '便携程序测试编译失败。' }
}
Write-Output $output
