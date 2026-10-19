$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$version = '1.2.0'
$portable = Join-Path $project 'build\本地视频拉黑'
$support = Join-Path $portable 'app'
if (-not (Test-Path -LiteralPath (Join-Path $portable '本地视频拉黑.exe'))) { throw '请先运行 build.ps1。' }
if ((Get-Item -LiteralPath (Join-Path $portable 'blacklist.txt')).Length -ne 0) { throw '发布黑名单必须为空。' }
if (Test-Path -LiteralPath (Join-Path $support 'logs')) { throw '发布目录包含运行日志，已停止。' }
$projectCopy = Join-Path $support 'project'
if (Test-Path -LiteralPath $projectCopy) { throw 'app/project 已存在，请移走旧打包目录后重新运行。' }
New-Item -ItemType Directory -Path $projectCopy | Out-Null
foreach ($name in @('README.md', 'LICENSE', 'CHANGELOG.md', 'build.ps1', '.gitignore', '.gitattributes')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $projectCopy $name)
}
foreach ($name in @('src', 'tests', 'scripts', 'docs')) {
    Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $projectCopy $name) -Recurse
}
$vendor = Join-Path $projectCopy 'vendor'
New-Item -ItemType Directory -Path $vendor | Out-Null
foreach ($name in @('README.md', 'PROVENANCE.json', 'COPYING.LGPLv2.1')) {
    Copy-Item -LiteralPath (Join-Path $project ('vendor\' + $name)) -Destination (Join-Path $vendor $name)
}
Copy-Item -LiteralPath (Join-Path $project 'vendor\source') -Destination (Join-Path $vendor 'source') -Recurse
$rootEntries = @(Get-ChildItem -LiteralPath $portable -Force)
if ($rootEntries.Count -ne 3 -or @($rootEntries | Where-Object { $_.Name -cnotin @('本地视频拉黑.exe', 'blacklist.txt', 'app') }).Count -gt 0) {
    throw '发布第一层存在多余项目。'
}
$checksums = @(Get-ChildItem -LiteralPath $portable -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($portable, $_.FullName).Replace('\', '/'); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$checksums | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $support 'checksums.json') -Encoding UTF8
$dist = Join-Path $project 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist ('local-video-blacklist-v' + $version + '-win-x64.zip')
if (Test-Path -LiteralPath $zip) { throw '发布 ZIP 已存在，停止以避免覆盖。' }
[IO.Compression.ZipFile]::CreateFromDirectory($portable, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output $zip
