<#
.SYNOPSIS
    把 Twice1200 打包成可以直接分发的压缩包。

.DESCRIPTION
    产物是「一个 exe + 一个 assets 文件夹 + 许可与说明」,压成一个 zip。
    目标机器不需要预先安装 .NET —— 默认使用自包含发布。

.PARAMETER Configuration
    构建配置,默认 Release。

.PARAMETER Runtime
    目标运行时标识,默认 win-x64。

.PARAMETER FolderMode
    不用单文件模式。产物是一堆 dll + 一个 exe。
    启动更快、更好排查,但文件夹里文件较多。默认不启用。

.PARAMETER SkipZip
    只生成文件夹,不压缩。

.EXAMPLE
    .\package.ps1

.EXAMPLE
    .\package.ps1 -FolderMode -SkipZip
#>

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$FolderMode,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

# 控制台按 UTF-8 输出。
# 不设的话,在代码页不是 936 的终端里中文会变成乱码 ——
# 这个脚本会打印不少中文进度与提醒,乱码会让它显得不可靠。
try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

# ---------------------------------------------------------------- 路径

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) {
    $root = Split-Path -Parent $MyInvocation.MyCommand.Definition
}

$appProject = Join-Path $root 'src\Twice1200.App\Twice1200.App.csproj'

if (-not (Test-Path -LiteralPath $appProject)) {
    throw "找不到 App 项目:$appProject"
}

# 从 Directory.Build.props 读版本号 —— 单一来源,避免脚本里再维护一份
$version = '0.0.0'
$propsPath = Join-Path $root 'Directory.Build.props'

if (Test-Path -LiteralPath $propsPath) {
    $match = [regex]::Match(
        (Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8),
        '<Version>\s*([^<]+?)\s*</Version>')

    if ($match.Success) {
        $version = $match.Groups[1].Value
    }
}

$packageName = "Twice1200-v$version-$Runtime"
$distRoot = Join-Path $root 'dist'
$outputDir = Join-Path $distRoot $packageName
$zipPath = Join-Path $distRoot "$packageName.zip"

Write-Host ''
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host "  Twice1200 打包  v$version  $Runtime" -ForegroundColor Cyan
Write-Host '============================================================' -ForegroundColor Cyan
Write-Host "  项目   : $appProject"
Write-Host "  输出   : $outputDir"
Write-Host "  模式   : $(if ($FolderMode) { '文件夹(多文件)' } else { '单文件 + 自包含' })"
Write-Host ''

# ---------------------------------------------------------------- 清理

# 【安全检查】只允许删除 dist 之下的目录。
# 路径是拼出来的,删之前必须确认它确实在预期位置 ——
# 否则一个变量写错就会删掉用户的其他东西。
if (Test-Path -LiteralPath $outputDir) {
    $resolved = (Resolve-Path -LiteralPath $outputDir).Path
    $distResolved = (Resolve-Path -LiteralPath $distRoot).Path

    if (-not $resolved.StartsWith($distResolved, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除:$resolved 不在 $distResolved 之下"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Host '  已清理旧的输出目录' -ForegroundColor DarkGray
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# ---------------------------------------------------------------- 发布

$publishArgs = @(
    'publish'
    $appProject
    '-c', $Configuration
    '-r', $Runtime
    '--self-contained', 'true'
    '-o', $outputDir
    '--nologo'
    '-v', 'quiet'
)

if (-not $FolderMode) {
    # 单文件:把托管 dll 全部塞进 exe。
    #
    # IncludeNativeLibrariesForSelfExtract 是 WPF 的关键 ——
    # WPF 依赖几个原生 dll(wpfgfx_cor3、PresentationNative_cor3、
    # D3DCompiler_47_cor3),不把它们也嵌进去的话,单文件 exe 旁边仍会散落这些文件。
    #
    # EnableCompressionInSingleFile 让体积小约 40%,代价是首次启动稍慢
    # (运行时先把内容解压到临时目录)。
    $publishArgs += @(
        '-p:PublishSingleFile=true'
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:EnableCompressionInSingleFile=true'
    )
}

Write-Host '  正在发布(自包含,首次会下载运行时包)…' -ForegroundColor DarkGray

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败(退出码 $LASTEXITCODE)"
}

# ---------------------------------------------------------------- 附带资源

# assets 不会被 dotnet publish 自动带上 —— 它在项目目录之外。
# 必须放在 exe 旁边(程序会从 exe 所在目录向上找 assets/)。
$assetsSource = Join-Path $root 'assets'
$assetsTarget = Join-Path $outputDir 'assets'

if (Test-Path -LiteralPath $assetsSource) {
    Copy-Item -LiteralPath $assetsSource -Destination $assetsTarget -Recurse -Force
    Write-Host '  已复制 assets/' -ForegroundColor DarkGray
} else {
    Write-Warning '  assets/ 不存在 —— 打包结果将无法启动 edge/frpc'
}

foreach ($file in @('LICENSE')) {
    $source = Join-Path $root $file

    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $outputDir $file) -Force
    }
}

# 清掉调试符号文件。
#
# 它们对使用者没有用处,却会:
#   · 让包里多出几个文件,看起来更杂
#   · 保留内部类型与源码行号信息
# 出问题时可以从构建产物里再取,不需要随分发包一起走。
Get-ChildItem -LiteralPath $outputDir -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        Write-Host "  已移除调试符号 $($_.Name)" -ForegroundColor DarkGray
    }

# 说明:刻意不复制项目根的 README.md。
# 那份是开发者文档(上千行,含构建方式、探坑记录、环境问题),
# 面向使用者的是下面生成的 使用说明.txt。
# 法律条款由 LICENSE 承担,不依赖 README。

# ---------------------------------------------------------------- 给使用者的说明

$readme = @"
Twice1200  v$version
============================================================

怎么开始
------------------------------------------------------------
1. 双击 Twice1200.exe

2. 在「n2n 组网」页填写四项:
      超级节点   例:supernode.example.com:7777
      网络组名   和同伴约定好的组名
      本机虚拟 IP 同一个网段里,每台机器必须不同
      网络密码   和同伴约定好的密码

   填完密码要点「保存密码」—— 它单独存进本机凭据库,不写进配置文件。

3. 点「连接」。看到这一行就说明成功了:
      [OK] edge <<< ================ >>> supernode

4. 想确认同伴也能连上,可以点「本机自测」——
   它在本机起一个临时超级节点,完全不依赖外部服务器。


第一次运行会被 Windows 拦一下
------------------------------------------------------------
两个提示都是正常的,不是病毒:

* "Windows 已保护你的电脑"(蓝色窗口,未知发布者)
  → 点「更多信息」→「仍要运行」

* 杀毒软件报警
  → 包内含 n2n 与 frp 的官方二进制,属于常见的灰名单误杀。
    把它们加入信任即可。

本程序没有代码签名,所以这两类提示会一直出现。


双击后没有反应 / 一闪就退出
------------------------------------------------------------
这个版本是「单文件」形式:exe 第一次启动时会把自己解压到

    %TEMP%\.net\Twice1200\

如果这一步失败,程序会直接退出、连窗口都不显示。
常见原因:

* 系统临时目录被安全软件或组策略限制
* 磁盘空间不足
* 临时目录所在磁盘有问题

可以试:
1. 清空 %TEMP% 后重试
2. 换个 Windows 账户试
3. 找分发者要「文件夹版」——那个版本启动时不需要解压
   (重新打包时加 -FolderMode 参数即可生成)


需要管理员权限的操作
------------------------------------------------------------
「准备环境」需要管理员 —— 它要安装 TAP 虚拟网卡驱动。
会弹一次 UAC,点「是」即可。

其它功能(连接、断开、frp、体检)都不需要管理员。


你的数据存在哪
------------------------------------------------------------
    %APPDATA%\Twice1200\config.json    配置(不含密码)
    %APPDATA%\Twice1200\secrets.dat    密码,以 DPAPI 加密
                                       只能用当前 Windows 账户解开

换电脑需要重新输入密码 —— 这是设计如此,不是缺陷。


许可
------------------------------------------------------------
本程序源码是 MIT,但 assets\ 里的第三方二进制各自适用自己的许可:
    frp             Apache-2.0
    n2n             GPL-3.0-or-later
    TAP-Windows     GPLv2
详见 LICENSE 文件。
"@

[System.IO.File]::WriteAllText(
    (Join-Path $outputDir '使用说明.txt'),
    $readme,
    [System.Text.UTF8Encoding]::new($true))   # 带 BOM,记事本才不会乱码

Write-Host '  已生成 使用说明.txt' -ForegroundColor DarkGray

# ---------------------------------------------------------------- 压缩

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host '  已压缩' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 汇总

$exePath = Join-Path $outputDir 'Twice1200.exe'

Write-Host ''
Write-Host '------------------------------------------------------------'
Write-Host '  打包完成'

if (Test-Path -LiteralPath $exePath) {
    $exeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)
    Write-Host "  exe          : $exeMb MB"
    Write-Host "  带运行时     : 是(目标机器不需要装 .NET)"
}

$fileCount = @(Get-ChildItem -LiteralPath $outputDir -Recurse -File).Count
$folderMb = [math]::Round(
    (Get-ChildItem -LiteralPath $outputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host "  文件夹       : $fileCount 个文件,$folderMb MB"

if (Test-Path -LiteralPath $zipPath) {
    $zipMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "  压缩包       : $zipMb MB"
    Write-Host "  路径         : $zipPath"
}

Write-Host '------------------------------------------------------------'
Write-Host ''

# ---------------------------------------------------------------- 自检提醒

Write-Host '  分发前请确认:' -ForegroundColor Yellow
Write-Host '    · 已轮换任何曾出现在公开位置的凭据'
Write-Host '    · assets\ 里的第三方许可文件齐全'
Write-Host '    · 在一台没装 .NET 的机器上试运行过'
Write-Host ''
