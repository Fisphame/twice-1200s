# One-click verification. Run by double-clicking verify.cmd.
#
# 三步:
#   1. 环境体检(21 项)
#   2. 本机回环验证 —— 在本机起一个超级节点并连它,确认客户端功能正常
#   3. 清理残留进程
#
# 需要管理员权限:脚本自己会请求提权(会弹一次 UAC)。
#
# 关于本文件的编码:它含中文,因此必须是 **UTF-8 带 BOM**。
# Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 读,中文会全部乱码。
# 项目里有一条自动检查守着这个约定(见 tests/scripts-check.ps1)。

$ErrorActionPreference = 'Continue'

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($root)) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }
$cli  = Join-Path $root 'src\Twice1200.Cli\bin\Debug\net9.0-windows\twice1200.exe'

function Say  { param([string]$M) Write-Host $M }
function Step { param([string]$M) Write-Host "[*] $M" -ForegroundColor Cyan }
function Good { param([string]$M) Write-Host "[+] $M" -ForegroundColor Green }
function Warn { param([string]$M) Write-Host "[!] $M" -ForegroundColor Yellow }
function Die  { param([string]$M) Write-Host "[x] $M" -ForegroundColor Red }

# ---------------------------------------------------------------- 提权
$isAdmin = (New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Warn '需要管理员权限(本机回环验证要启动 edge,而 edge 必须提权才能访问 TAP 网卡)。'
    Say  '正在请求提权,请在 UAC 对话框点「是」...'
    try {
        Start-Process -FilePath 'powershell.exe' `
            -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath) `
            -Verb RunAs | Out-Null
    } catch {
        Die '提权被取消。'
    }
    exit 0
}

Say ''
Say '============================================================'
Say '  Twice1200 一键验证'
Say '============================================================'
Say "  程序目录 : $root"
Say "  权限     : 管理员"
Say ''

if (-not (Test-Path -LiteralPath $cli)) {
    Die "找不到命令行程序:$cli"
    Say '请先构建:dotnet build src\Twice1200.Cli\Twice1200.Cli.csproj'
    exit 1
}

# ---------------------------------------------------------------- 第 1 步:体检
Say ''
Say '------------------------------------------------------------'
Say '  第 1 步 / 共 3 步:环境体检'
Say '------------------------------------------------------------'
Say ''
& $cli doctor
$doctorCode = $LASTEXITCODE
Say ''
Say "  体检退出码:$doctorCode   (0=全部通过 1=有失败 2=仅有警告)"

# ---------------------------------------------------------------- 第 2 步:回环验证
Say ''
Say '------------------------------------------------------------'
Say '  第 2 步 / 共 3 步:本机回环验证'
Say '------------------------------------------------------------'
Say '  这一步会真实启动 edge,并短暂占用 UDP 7777 端口。'
Say '  传了 --virtual-ip,所以不会改动虚拟网卡的地址。'
Say ''

$community = 'verify-' + (Get-Random -Minimum 100000 -Maximum 999999)
& $cli n2n up --local-supernode --community $community --virtual-ip 10.236.167.114 --verbose-edge
$loopbackCode = $LASTEXITCODE
Say ''
Say "  回环验证退出码:$loopbackCode   (0=成功)"

# ---------------------------------------------------------------- 第 3 步:清理
Say ''
Say '------------------------------------------------------------'
Say '  第 3 步 / 共 3 步:清理残留进程'
Say '------------------------------------------------------------'
& $cli n2n down --force

# ---------------------------------------------------------------- 结论
Say ''
Say '============================================================'
if ($loopbackCode -eq 0) {
    Good '结论:客户端功能正常。'
    Say  '  本机能连、远端不能连 → 问题在服务端(超级节点)。'
    Say  '  周末联系上朋友后,去云控制台确认 supernode 是否在运行、UDP 7777 是否放行。'
} else {
    Warn '结论:本机回环也没能完成握手 —— 问题在客户端一侧。'
    Say  '  请把上面的输出截图反馈,特别是「启动流程」那几行。'
}
Say ''
Say "  体检=$doctorCode   回环=$loopbackCode"
Say '============================================================'
Say ''

Read-Host '按回车键关闭本窗口'
