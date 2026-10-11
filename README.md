# Twice1200

n2n 组网与 frp 内网穿透的 **Windows 图形客户端**。

目标:让没有命令行基础的用户也能一键完成虚拟局域网组网与端口映射,
同时修掉现有手工方案里的安全问题(明文密钥、弱默认口令)。

---

## 当前状态

| 部分 | 状态 |
|---|---|
| `Twice1200.Core` | ✅ 可编译、有测试覆盖 |
| 诊断(体检)模块 | ✅ 已完成,**21 项检查**全部可运行 |
| 其中:edge 实测(N-04) | ✅ 真实启动 edge 并判断卡在哪一步 |
| `Twice1200.Cli` 命令行 | ✅ 可用(`doctor` / `n2n` / `tap` / `paths` / `config`) |
| 进程管理模块 | ✅ 已完成,132 项自检覆盖 |
| **TAP 网卡管理** | ✅ 已完成(检测 / 装驱动 / 创建 / 改名 / 启用,含自提权) |
| **凭据存储(DPAPI)** | ✅ 已完成(零 NuGet 依赖,直接 P/Invoke `crypt32.dll`) |
| **`Twice1200.App` WPF 界面** | ✅ 已完成初版(单窗口 + 两个功能页 + 共享日志面板) |

**这个项目刻意按"先内核后界面"推进。** 现在能跑的部分已经足够回答"环境到底行不行"这个问题,
而这类工具 80% 的麻烦都在环境上。

---

## 快速开始

### 环境要求

- Windows 10 / 11
- **.NET 9 SDK**(构建)或 **.NET 9 桌面运行时**(仅运行)

### 构建

```powershell
cd C:\Users\Phamoce\Desktop\eng\program\Twice1200

# 注意:逐个构建项目,不要用 dotnet build Twice1200.sln
# 原因见下面"已知环境问题"一节
dotnet build src\Twice1200.Core\Twice1200.Core.csproj
dotnet build src\Twice1200.Cli\Twice1200.Cli.csproj
dotnet build tests\Twice1200.SelfTest\Twice1200.SelfTest.csproj
```

### 运行

```powershell
# 环境体检 —— 最常用的命令
.\src\Twice1200.Cli\bin\Debug\net9.0-windows\twice1200.exe doctor

# 查看各类路径与资源文件状态
.\src\Twice1200.Cli\bin\Debug\net9.0-windows\twice1200.exe paths --hash

# 生成一份不含任何密钥的配置模板
.\src\Twice1200.Cli\bin\Debug\net9.0-windows\twice1200.exe config --init

# 自检(Core 的冒烟测试,68+ 项)
.\tests\Twice1200.SelfTest\bin\Debug\net9.0\twice1200-selftest.exe
```

`doctor` 的退出码可以直接用在脚本里:

| 退出码 | 含义 |
|---|---|
| 0 | 全部通过 |
| 1 | 存在失败项 |
| 2 | 无失败,但有警告 |
| 64 | 命令行用法错误 |
| 70 | 程序内部错误 |

### doctor 的常用选项

```powershell
--assets <目录>            显式指定资源目录
--config <路径>            指定配置文件(默认 %APPDATA%\Twice1200\config.json)
--supernode-host <主机>    超级节点地址
--frp-host <主机>          frp 服务端地址
--virtual-ip <地址>        指定本机虚拟 IP,用于冲突检测
--scan-virtual-network     扫描虚拟网段找空闲 IP(会向网段发 ICMP,默认关闭)
--probe-edge               实测:真实启动一次 edge.exe,从它的日志判断卡在哪一步
--probe-timeout <秒>       实测等待时长,默认 10
--verbose                  输出完整细节(默认只显示每项的前几行)
```

### `n2n` 子命令

```powershell
# 只渲染将要执行的命令,不启动任何进程
# —— 用来确认含空格的网卡名有没有被正确保持成一项
twice1200 n2n args

# 查看运行状态:进程、当前权限、虚拟网卡地址
twice1200 n2n status

# 连接(需要管理员权限)
twice1200 n2n up --community <组名> --virtual-ip 10.236.167.114

# 本机回环验证:自己起一个超级节点并连它,完全不依赖远程服务器
twice1200 n2n up --local-supernode --community test --virtual-ip 10.236.167.114

# 结束残留进程
twice1200 n2n down
```

**⚠️ `--local-supernode` 必须同时指定 `--virtual-ip`。**

原因是实测发现的一个副作用:不指定 IP 时,`edge.exe` 会接受超级节点分配的地址
并**写进你的虚拟网卡** —— 实测中把用户的 `10.236.167.114` 改成了 `10.235.158.244`。

这是 n2n 的正常设计行为("自动分配 IP"),但对诊断工具来说不可接受:
用户只是想验证连接,网络配置不该被改。CLI 现在会**拒绝执行并说明原因**;
若已被改动,可用 `.probe-edge/restore-adapter-ip.ps1` 恢复。

### `tap` 子命令

```powershell
# 查看网卡、驱动与权限状态(不需要管理员权限)
twice1200 tap status

# 确保有一块可用的 TAP 网卡 —— 会自动请求提权
twice1200 tap ensure --name n2n-TAP
```

`tap ensure` 按 **检测 → 装驱动 → 创建网卡实例 → 改名 → 启用** 的顺序推进,
每一步失败都带着明确原因返回。这是界面上"一键准备环境"背后的逻辑。

### 自提权是怎么做的

需要特权的操作没法在原进程里完成,所以程序会**把自己以管理员身份重新启动一次**。

这里有个 Windows 的硬限制:**`Start-Process -Verb RunAs` 无法重定向标准输出。**
因此不能靠管道回传结果,走的是:

```
父进程(标准权限)
  │  生成结果文件路径,连同"要做什么"一起通过命令行传给子进程
  │  Start-Process -Verb RunAs
  ▼
子进程(管理员)
  │  执行实际操作,把结果写成 JSON
  ▼
父进程读取结果文件并展示,然后删除它
```

好处是结果结构清晰、不依赖管道;代价是多一次进程启动和一次磁盘写入。

**UAC 被点"否"时的行为:** 报错并退出,退出码 74,**虚拟网卡不发生任何改动** ——
不会留下"做了一半"的状态。

### `credential` 子命令

```powershell
# 保存 n2n 网络密码(掩码输入,两次确认)
twice1200 credential set n2n/key

# 保存 frp token
twice1200 credential set frp/token

# 查看已保存的键名与状态(不显示内容)
twice1200 credential list

# 核对某个键:只显示长度与指纹
twice1200 credential show n2n/key

# 自检 DPAPI 加解密是否正常
twice1200 credential diagnose

# 删除 / 清空
twice1200 credential delete n2n/key
twice1200 credential clear
```

`n2n up` 现在会**自动从凭据库取密钥**,不再需要 `--key`:

```
[*] 已从凭据库取出密钥(键「n2n/key」)。
```

### 凭据存储的设计

**零 NuGet 依赖。** `System.Security.Cryptography.ProtectedData` 不在 BCL 里,
需要引包;而 DPAPI 本质上就是 `crypt32.dll` 的两个导出函数。所以直接 P/Invoke:

```csharp
CryptProtectData / CryptUnprotectData   // CRYPTPROTECT_UI_FORBIDDEN,作用域为当前用户
```

好处是**内存的分配与释放都看得见**,不需要猜封装层做了什么,
也让 Core 保持"永远可以构建"。

**作用域选的是当前用户,不是 `LOCAL_MACHINE`。** 后者会让这台机器上的**任何**用户
都能解密;只有在"服务账户无人登录时也要解密"的场景才需要,本项目不需要。

**文件位置** `%APPDATA%\Twice1200\secrets.dat`,与 `config.json` 同目录,便于整体备份。
密文换机器就解不开 —— 这是 DPAPI 的作用域语义,不是 bug。

**写入是原子的:** 先写 `.tmp` 再替换。这样即使写到一半断电,
原有的凭据库也不会变成半截内容 —— 密钥恰恰是用户最不想重输的东西。

**损坏的文件不会被静默覆盖。** 解析失败时中止操作并保留原文件,
因为那里面可能还有可以手工恢复的数据。有一条测试专门守这个行为。

**`ListHandles()` 只返回值名,不返回值。** 这条 API 的形状本身就是安全约束:
想拿到密钥必须显式调用 `TryGetSecret`,让"读凭据"在代码里是一个看得见的动作。

**弱密码会被拦下。** 保存时用 `ConfigurationValidator.DescribeWeakness` 检查,
命中 n2n 官方示例值或明显弱口令时要求确认(`--force` 可跳过)。判定说明里不含密码内容。

### `frp` 子命令

```powershell
# 保存 token(掩码输入)
twice1200 credential set frp/token

# 查看将要生成的 frpc 配置(token 打码,不写文件)
twice1200 frp config

# 启动端口映射
twice1200 frp up

# 查看状态
twice1200 frp status

# 停止
twice1200 frp down
```

**与 n2n 的关键差别:frp 这条链路可以立刻端到端验证,因为服务端是活的。**
实测确认(真实服务器、真实 token):

```
[client/service.go:378] try to connect to server...
[client/service.go:370] [708761c3ddded7c3] login to server success, get run id [...]
[proxy/proxy_manager.go:180] [...] proxy added: [web]
[client/control.go:176] [...] [web] start proxy success
```

### frp 实现里的三个关键设计

**① token 绝不进入命令行。**

命令行对本机任何进程可读。所以 token 只写进一个临时配置文件,
参数只有 `-c <配置文件>`。有测试逐项断言参数列表里不含 token:

```csharp
Check.False(arguments.Contains("-t"), "不应使用 -t 传 token");
Check.False(arguments.Contains("--token"), "不应使用 --token 传 token");
```

**② 临时配置文件在进程启动后立即删除。**

取舍:frpc 在启动时读取配置,运行期不再需要它,所以这样做是安全的;
代价是运行期无法热重载配置 —— 对本工具的用途(启停连接)不构成限制。
留一份含明文 token 的文件在磁盘上,风险大于这点便利。

实测确认:启动并运行 14 秒后,运行期目录里**零个文件**。

**③ 错误分类来自实测,不是猜的。**

| frpc 的输出 | 我们的判定 | 退出码 |
|---|---|---|
| `login to server success` + `start proxy success` | 成功 | 0 |
| `token in login doesn't match` | 鉴权失败 | 77 |
| `connect to server error` | 服务端不可达 | 69 |
| 配置逻辑缺失 | 配置非法,不启动 | 64 |

**注意 `frpc verify` 不抓逻辑缺失** —— 实测确认:没有 `serverAddr`、
没有 `proxies`,它都说 "syntax is ok"。所以逻辑校验必须自己做,
`frpc verify` 只能当第二道网。

**④ 剥掉 ANSI 颜色转义。**

实测确认 frpc **即使输出被重定向到管道也带颜色序列**
(`\u001b[1;34m`)。它们会污染界面显示,并让日志的字符串匹配不可靠。
清理放在 `ProcessSupervisor` 的 `LineTransform` 钩子里,在**进入日志缓冲之前**,
这样缓冲、事件、界面三处看到的完全一致 —— 只在某一处清理会导致
"界面没有但匹配到了"这类难查的不一致。

---

## 图形界面

### 运行

```powershell
dotnet build src\Twice1200.App\Twice1200.App.csproj
.\src\Twice1200.App\bin\Debug\net9.0-windows\Twice1200.exe
```

### 界面构成

```
┌─ Twice1200   n2n 组网与 frp 穿透          当前:标准用户 ─┐
│ ┌─ n2n 组网 ─┬─ frp 穿透 ─┐                            │
│ │ 虚拟网卡名  [以太网 2          ]                       │
│ │ 网络组名    [                 ]                       │
│ │ 超级节点    [1200s.top:7777   ]                       │
│ │ 本机虚拟 IP [10.236.167.114   ]                       │
│ │ 网络密码    [********]  [保存密码]                     │
│ │           已保存密码:长度 18,指纹 a3f91c82             │
│ │ [连接] [断开] [准备环境] [本机自测]                     │
│ └───────────────────────────────────────────────────────┘│
│ 日志  [环境体检] [复制日志] [清空]            42 行        │
│ ┌───────────────────────────────────────────────────────┐│
│ │ ...                                                   ││
│ └───────────────────────────────────────────────────────┘│
│ 已连接     连接正常。                       [进度条]      │
└──────────────────────────────────────────────────────────┘
```

### 设计取舍

**日志面板是两个功能页共享的,不是 n2n 专属的。**
放成全局面板有两个好处:切标签页时日志不丢;而且它成了整个工具的诊断中枢 ——
"环境体检"的结果、edge 的实时输出、supernode 的输出都汇到同一处。

**只用最基础的控件,没有 MVVM 框架、没有值转换器、没有第三方控件库。**
界面层目前只做"把已有 Core 能力接起来"这一件事,而 Core 里的模块都已经是可独立测试的。
多一层绑定框架只会让第一次跑通变难。**结构整理留到两条链路都稳定之后。**

**`app.manifest` 里刻意不声明 `requireAdministrator`。**
理由与提权模型一致:界面要常驻、要显示状态、要读写用户配置。
让一个整天开着的界面全程持有管理员权限,会把攻击面扩大到整个系统。
需要特权的操作走"准备环境"按钮,由提权进程完成。

实测边界的诚实说明:界面本身不会自动提权。
**点「准备环境」时若当前不是管理员,会明确告诉你需要以管理员身份重启**,而不是静默失败。
命令行版的 `tap ensure` 会自动请求提权;界面版暂时需要手动重启 —— 这是已知的待改进项。

**「本机自测」强制要求填写虚拟 IP。**
因为它会真实启动 edge;不指定 IP 时 edge 会接受超级节点分配的地址并写进网卡,
从而改动用户的网络配置。这条限制与 CLI 的 `--local-supernode` 完全一致。



### 最有用的两条命令

```powershell
# 1. 环境体检:19 项检查,不启动任何外部进程
twice1200 doctor

# 2. 实测:真的启动一次 edge,告诉你它卡在哪一步
#    (以管理员身份运行,否则会如实报告"权限不足")
twice1200 doctor --probe-edge --verbose
```

`--probe-edge` 是排查 n2n 问题时最有力的工具。它使用**不存在的网卡名与社区名**,
所以不会改动任何真实网络配置,但能真实地告诉我们 edge 走到了哪一步:

```
—— 启动流程 ——
  读取加密配置 : AES
  枚举 TAP 设备: 成功
  打开指定网卡 : 未到达此步
  向超级节点要 IP: 是(未指定 -a 时走这条路径)
  发起注册     : 是
```

当它报「本机配置正常,但超级节点没有分配虚拟 IP」时,
说明**问题在服务端,继续在本机排查不会有结果** ——
这个区分能力是这一轮探测最大的收获。


---

## 目录结构

```
Twice1200/
├── Twice1200.sln / .slnx        解决方案(两种格式,兼容不同 IDE)
├── global.json                  锁定 .NET 9 SDK
├── Directory.Build.props        共享构建设置
├── .gitignore                   【安全】排除密钥与本地配置
├── assets/                      运行时外部资源(第三方二进制与驱动)
│   ├── README.md                资源说明与填充方法
│   ├── CHECKSUMS.txt            完整性校验清单
│   ├── n2n/                     edge.exe、supernode.exe
│   ├── tap/                     TAP 虚拟网卡驱动安装包
│   └── frp/                     frpc.exe、frps.exe、LICENSE
├── docs/
│   └── 安全说明.md              威胁模型与安全设计
├── src/
│   ├── Twice1200.Core/          全部无界面逻辑(可单元测试)
│   │   ├── Assets/              资源目录定位
│   │   ├── Configuration/       配置模型、校验、密钥引用
│   │   ├── Diagnostics/         体检框架与 19 项检查
│   │   ├── Net/                 网段、端点、连通性探测、网卡枚举
│   │   └── Runtime/             PE 解析、哈希、脱敏、进程快照、路径约定
│   ├── Twice1200.Cli/           命令行入口(开发期主力工具)
│   └── Twice1200.App/           WPF 图形界面(待实现)
└── tests/
    └── Twice1200.SelfTest/      无第三方依赖的自检程序
```

---

## 架构决策

| 决策 | 结论 | 理由 |
|---|---|---|
| 技术栈 | **.NET 9 + WPF**(C#) | 提权、DPAPI、子进程 IO、网卡查询都是 .NET 的标准能力 |
| 程序形态 | 一个程序,两个功能页 | 共用提权、密钥、日志等基础设施 |
| 工作流 | **先 Core 后 GUI** | 网络与权限问题必须在没有界面干扰的情况下调试 |
| 密钥存储 | **DPAPI**(绑定当前用户与机器) | 密文被拷走也解不开。代价:不可跨机器共享 |
| 服务端 | **只做客户端**,服务端手工部署 | 用户只有云厂商网页控制台,自动化部署性价比低 |
| 提权模型 | 平时标准权限,需要时提权辅助进程 | 避免长效管理员权限的界面 |
| 测试框架 | 暂用自建 SelfTest,**不引入 NuGet 依赖** | 见"已知环境问题" |

### 为什么 Core 是纯 `net9.0` 而不是 `net9.0-windows`

`Twice1200.Core` 刻意只依赖 BCL 中跨平台的部分:网卡枚举用 `NetworkInterface`,
权限检测用 `WindowsPrincipal`,注册表用 `Microsoft.Win32.Registry`(BCL 自带)。
**不需要 `System.Management` / WMI,也不依赖任何 NuGet 包** ——
这让 Core 永远可以构建与测试,不受包还原问题影响。

代价是拿不到"驱动服务名(如 `tap0901`)"这类 WMI 专有信息,
因此 TAP 识别改为按驱动描述匹配(见 `NetworkInterfaceInspector`)。
这对本项目足够准确,而且更快、更少受系统策略限制。

---

## 安全设计

### 三层防护

**1. 存储层 —— 密钥不落明文**

配置文件(`config.json`)里**永远不出现密码或 token**,只保存"引用":

```json
{
  "n2n": {
    "community": "某个组名",
    "key": { "handle": "n2n/key", "source": "Dpapi" }
  }
}
```

真正的密文存放在 `%APPDATA%\Twice1200\secrets.dat`,由 DPAPI 加密,
密钥材料绑定"当前 Windows 用户 + 当前机器"。

**2. 传递层 —— 消除命令行明文**

现有的手工方案有个真实漏洞:

```
edge.exe -d "以太网 2" -c mygroup -k mysecretpassword ...
                                          ↑ 命令行对本机任何进程公开
```

任何程序执行一句 `Get-CimInstance Win32_Process` 就能读到**所有进程的完整命令行**。
这是 Windows 的既有行为,不是理论风险。处理方式:

- frp 配置写入临时目录并收紧 ACL,进程启动后立即删除
- 日志与错误信息永不打印密钥(统一走 `Redaction` 脱敏)
- **本项目刻意不使用 WMI 读进程命令行** —— 既是性能考虑,也是安全纪律

**3. 配置层 —— 主动拦截危险值**

`ConfigurationValidator` 会把以下情况报为错误,而不是放行:

- `mygroup` / `mysecretpassword` / `mynet` / `mypasswd` 等 **n2n 官方示例值**
- 过短的组名
- 虚拟 IP 与网段不匹配
- frp 服务端地址带协议前缀
- 多条规则争用同一个服务端端口
- 关闭 frp TLS

---

## 客户端已验证(2026-10-12)

**这是本项目最重要的一次验证结论。**

### 本机回环验证通过

```powershell
.\verify.cmd          # 双击即可,会自己请求提权
```

结果:

```
[*] 在本机启动超级节点(UDP 7777)…
[+] 超级节点已开始监听。
[*] 启动 edge(超级节点 127.0.0.1:7777)…
[+] n2n 已启动(网卡「n2n-TAP」)。
[+] 握手成功 —— 已与超级节点建立通信。
[√] 本机回环验证通过:客户端功能正常。
```

底层完整握手:

```
Open device [name={2952F71C-...}][ip=10.236.167.114][ifName=n2n-TAP][MTU=1290]
[edge.c:1258] created local tap device IP: 10.236.167.114, Mask: 255.255.255.0
[edge.c:1356] edge started
[edge_utils.c:2449] Rx REGISTER_SUPER_ACK from ... [127.0.0.1:7777]
[edge_utils.c:2508] [OK] edge <<< ================ >>> supernode
```

### 由此得到的确定结论

| | |
|---|---|
| DNS 解析 | ✅ 正常 |
| TAP 驱动与网卡 | ✅ 正常(`n2n-TAP`) |
| 参数传递(含空格/非 ASCII) | ✅ 正常 |
| 密钥经 DPAPI 存储并读取 | ✅ 正常 |
| edge 启动与握手 | ✅ 正常 |
| **远程超级节点** | ❌ **不响应** |

**同一个 edge.exe、同一块网卡、同一套参数、同一个密码,唯一的差别是超级节点地址。**
本机能通 → 客户端没问题。所以 `1200s.top:7777` 沉默是**服务端现象**。

**具体的排查步骤见 [docs/服务端排查清单.md](docs/服务端排查清单.md)**
(需要云控制台:确认 supernode 进程、UDP 7777 安全组规则、端口监听地址)。

---

## 已知环境问题(开发实录)

以下是本项目搭建过程中实际遇到并已记录的问题,**换台机器时大概率还会碰到**。
其中几条是在真实使用界面时暴露出来的 —— 记录得比较细,因为它们都属于
"看起来像 A,实际是 B"那类最难排查的问题。

### 1. 沙箱/受限会话下 `dotnet` 的两个故障

| 现象 | 原因 | 处理 |
|---|---|---|
| `dotnet new` 报 `Access to the path ...\.templateengine\... is denied` | 模板引擎缓存目录不可写 | 设 `$env:DOTNET_CLI_HOME` 到可写目录后再运行 |
| `dotnet build <解决方案>` 只构建第一个项目后报"失败",但**0 错误 0 警告** | MSBuild 节点复用与编译器服务器需要命名管道,受限环境建不起来 | **逐个构建项目**;或加 `-m:1 /nodeReuse:false` 并接受较慢 |

**结论:`dotnet build` 单个项目始终正常,解决方案级构建在受限环境下不可靠。**

### 2. `dotnet restore` 无法访问 nuget.org

TLS 到 `api.nuget.org` 被拦截(`NU1301` + SSL 握手失败),而纯 TCP 探测却显示可达。
**影响:任何需要 NuGet 包的项目(xunit、System.Management、WPF)都无法在本环境还原。**

应对:

- `Twice1200.Core` / `Cli` / `SelfTest` 做到**零 NuGet 依赖**,因此始终可构建
- 测试暂时用自建的 `Twice1200.SelfTest` 控制台程序代替 xUnit
- `Twice1200.App`(WPF)需要包还原,只能在正常会话里构建

### 3. Windows Defender 会删除 `frps.exe`
**实测:复制 `frps.exe` 后约 3 秒,文件被 Defender 实时防护删除,源文件保留。**

原因:frp 因被大量恶意软件滥用做隧道,其二进制长期在杀软灰名单上。
这是"灰名单误杀",不代表文件被篡改,但**无法绕过**。

对分发的影响:

- ✅ 客户端只需要 `frpc.exe`,它**不会被删**,不受影响
- ⚠️ 若 `assets/` 里带了 `frps.exe`,某些杀软可能删文件甚至隔离整个安装目录
- ⚠️ **用户安装时必须提前告知**,否则会被当成病毒

**当前处置:客户端资源里不包含 `frps.exe`。** 服务端部署时单独从原始目录取用。

### 4. 脱敏启发的过度匹配会毁掉日志(已修,值得记住)

**现象:** 界面日志里网卡名显示成 `??̫?? 2` 或 `[已脱敏]`,
而且本机自测报 `Cannot find tap device "??̫?? 2"` —— 看起来像编码问题或参数被截断。

**真实原因:** `ProcessSupervisor` 会把 `request.Environment` 里的**所有**值
都登记为待脱敏密钥,而 `SecretSet` 的"看起来像密钥"启发式是
"含大写字母或数字,且长度 ≥ 5" —— 于是:

| 值 | 是否被误判为密钥 | 原因 |
|---|---|---|
| `以太网 2` | **是** | 长度 5,含数字 `2` |
| `10.236.167.114` | **是** | 含数字 |
| `n2n-TAP` | 是 | 含大写 |

**后果比不脱敏更糟:** 生产日志里的网卡名和 IP 全被抹掉,
而排查 n2n 问题恰恰完全依赖这两样东西。

**修法:改成显式声明。** 只有调用方明确登记到 `SecretSet` 的值才会被脱敏;
环境变量里的值不再被自动登记。语义因此确定:**声明什么就抹什么。**

**这条的普遍教训:** "尽量多抹一点"在脱敏上是错误策略。
过度匹配的代价是日志失去诊断价值,而诊断价值正是加日志的目的。

**尚未查清的部分(如实记录):** 从 `cmd.exe` / `edge.exe` 读回中文输出时观察到过乱码,
而且**强制 UTF-8 解码与平台默认解码得到相同结果** —— 说明损坏可能在更早的环节。
这一点没有完全定位。实测同时确认了**参数传递本身是完好的**(见第 5 条),
所以对功能没有影响,但日志里可能出现难读的中文。

**规避方式:** 把虚拟网卡命名为纯 ASCII(例如 `n2n-TAP`)。
项目本身已为零中文代码页系统准备了 `FallbackAdapterName`,所以 ASCII 名是受支持的配置。
用 `twice1200 tap ensure --name n2n-TAP` 即可。

### 5. 参数传递的正确性已被逐字验证

因为第 4 条会让人怀疑"参数到底有没有正确送达",所以专门验证过:

```
方法:自建辅助子进程,用 P/Invoke 读自己的 GetCommandLineW(),再用十六进制核对
结果:
  「探测名 2」  → argv 里的名称与传入的完全相同
  「以太网 2」  → 同上,而且空格被正确引号包住
```

**结论:`.NET` 的 `ProcessStartInfo.ArgumentList` 会正确转义含空格与非 ASCII 的值。**
这方面不需要额外处理 —— 前提是**不要自己去拼命令行字符串**。

### 6. 以管理员运行过一次之后,普通权限写不进配置(已修)

**现象:**

```
[×] 保存 token 失败:写入凭据库失败:UnauthorizedAccessException:
    Access to the path 'C:\Users\...\AppData\Roaming\Twice1200\secrets.dat.tmp' is denied.
```

**关键特征:只有在以管理员身份运行过一次之后才会出现。** 干净的机器上不会。

**排查到的事实:**

| 项目 | 观察结果 |
|---|---|
| 目录 ACL | `PHA\Phamoce` 有 `FullControl`,而且是**继承来的** |
| 文件 ACL | 同上,三条规则都是继承的 |
| **所有者** | **`BUILTIN\Administrators`**,而不是当前用户 |
| 报错位置 | `File.WriteAllText` 打开 `secrets.dat.tmp` 时,不是后面的 `File.Move` |

**诚实说明:根因我没有在本地复现。** 观察到的 ACL 里当前用户其实是有
`FullControl` 的,所以"权限不足"并不能完全解释这个现象。受限环境不允许写
`%APPDATA%`,因此无法做对照实验 —— 这里不编造解释。

**最可能的机制**(假设,非结论):上一次写入被中断(进程被杀、异常退出)时
留下了一个孤儿 `secrets.dat.tmp`。那个文件由当时那次运行创建,所有者与权限
未必对现在这个进程友好;而 `File.WriteAllText` 是"打开或创建",
它会去写那个**已存在**的文件,于是直接 Access Denied。
注意报错发生在写入临时文件这一步,而不是替换那一步 —— 这和该假设吻合。

**修法(两层,覆盖两种可能的原因):**

1. **写入前主动删除可能残留的 `.tmp`。** 临时文件本来就没有保留价值,
   删掉比尝试修它的权限更彻底。
2. **写入前显式设定所有者与权限。** 新增 `OwnershipHelper`,
   在我们自己的文件上把所有者设为当前用户,并显式授予 `FullControl`,
   **不再依赖继承**。只动我们自己的文件,不碰系统或别人的东西。

此外还给 `config.json` 的写入加上了同样的处理(之前只有凭据库有)。

**提供了一次性修复脚本:**

```powershell
twice1200 diagnose write
```

它按顺序做四件事:列出当前内容 → 删除残留 `.tmp` → `takeown` 取回所有权 →
`icacls` 授予当前用户完全控制。**普通权限运行即可**;
若因 Access Denied 失败,再以管理员身份跑一次。

**一条使用纪律:** 平时以标准权限运行;只有「准备环境」这类需要装驱动、
改网卡的操作才用管理员。不需要管理员权限却用管理员跑,就是这类
"文件所有者错位"问题的来源。

### 7. 脚本文件的编码约定(踩过三次,已用构建守门固定下来)
Windows 上不同脚本宿主读文件的方式不一样,这个坑踩了三次:

| 宿主 | 读取方式 | 后果 |
|---|---|---|
| `cmd.exe` 读 `.cmd` / `.bat` | 按 **ANSI 代码页**(简体中文系统上是 GBK) | 乱码字节破坏命令本身,报一堆「不是内部或外部命令」 |
| Windows PowerShell 5.1 读 `.ps1` | **无 BOM 时**同样按 ANSI | 中文字符串终止符丢失,报语法错误 |

**判定规则:**

```
.cmd / .bat  →  必须纯 ASCII(注释也用英文)
.ps1         →  可以含非 ASCII,但必须带 UTF-8 BOM
```

**这条已由构建守门强制执行。** `Twice1200.App.csproj` 里的 `CheckScriptEncodings` 目标
会在构建前调用 `tests/scripts-check.ps1`;违反约定时**构建直接失败**,而不是等用户双击才发现。

那个检查脚本**本身刻意写成纯 ASCII** —— 一个会被自己要检查的问题搞坏的检查器不算检查器。
它支持 `-Fix` 自动给 `.ps1` 补 BOM:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\scripts-check.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\scripts-check.ps1 -Fix
```

**注意:编辑工具可能静默去掉 BOM。** 改完 `.ps1` 后如果出现奇怪的语法错误,
先确认 BOM 还在 —— 或者干脆跑一次上面的 `-Fix`。

**一个相关的元教训:** 这三次踩坑有两次是"用 PowerShell 的 `Set-Content` 写源码"造成的
(它按 GBK 重写文件)。**在任何 shell 里都不要用 `Set-Content` 写源码或脚本**,
用 `[System.IO.File]::WriteAllText` 并显式指定编码。

---


## 待办事项

### 阻塞项 / 待验证

| 编号 | 事项 | 状态 |
|---|---|---|
| ~~V2~~ | **`edge.exe` 能否不通过命令行传密钥** | ✅ **已解决** —— 用 `N2N_KEY` 环境变量 |
| ~~V8~~ | **`edge.exe` 是否需要管理员权限** | ✅ **已解决** —— **必须提权**。根因还包括命令行引号缺失,见"探针结论 3/4" |
| **V7** | **超级节点 `1200s.top:7777` 不响应** | ⚠️ **待解决(已确认为服务端问题)** —— 见下方"客户端已验证"与 `docs/服务端排查清单.md` |
| V9 | `frps` 是否开启 dashboard 并暴露公网 | 待通过云控制台核实 |

---

## edge.exe 探针结论(2026-05 实测)

这一节是实测结果,**不是从文档抄的**。探测脚本保留在 `.probe-edge/` 下可供复现。

### 结论 1:密钥可以用环境变量传,不必走命令行 ✅

`edge.exe` 是 **n2n v3.1.0(2022-02-28 构建)**。它的帮助里有:

```
 N2N_KEY           | encryption key (ASCII), not with '-k ...'
 N2N_COMMUNITY     | community name (ASCII), overwritten by '-c ...'
 N2N_PASSWORD      | password (ASCII) for user-password authentication
```

注意用词差异:`N2N_KEY` 是 **"not with"**(与 `-k` 互斥),
而 `N2N_COMMUNITY`/`N2N_PASSWORD` 是 **"overwritten by"**(可被命令行覆盖)。
说明环境变量路径在 n2n 里是一等公民,不是附加功能。

**对照实验(四次运行,只改密钥来源):**

| 运行 | 密钥来源 | edge 自己的日志 |
|---|---|---|
| B1 | **无密钥** | `using null cipher.` + `WARNING: encryption is disabled in edge` |
| B2 | **`N2N_KEY` 环境变量** | `using AES cipher.` |
| B3 | `-k` 命令行 | `WARNING: switching to AES as key was provided` + `using AES cipher.` |
| B4 | `N2N_KEY`,不传 `-d` | `using AES cipher.` |

**为什么这个对照是决定性的:** B1 证明"没有密钥时 edge 会明确说加密已禁用",
因此 B2 的 `using AES cipher.` 只可能来自环境变量。

**额外证据:** B3 比 B2 多一行 `WARNING: switching to AES as key was provided`。
这条警告只在命令行路径上打印 —— 说明环境变量走的是**另一条代码路径**,
完全绕开了 `-k` 参数处理。

**方法可靠性说明:** 探测前先用一个必然存在的标记变量验证了"子进程能读到继承的环境变量",
否则"读不到 `N2N_KEY`"这个结果本身不能说明任何问题。

### 结论 2:命令行确实会泄露秘密 ✅(前提成立)

让子进程用 `kernel32.GetCommandLineW()` 自报:

```
GetCommandLineW() : "... -Note 'ARGV-LEAK-PROOF-24680'"     ← 明文可见
CommandLineArgs   : ... -Note 'ARGV-LEAK-PROOF-24680'
```

对照:把秘密改放环境变量后,同一位置变成干净的 `-Note 'clean-note'`,
而 `$env:N2N_KEY` 仍然可读。**即:argv 与 env 的差别是实质性的。**

顺带验证了 `GetCommandLineW()` 的 P/Invoke 在本机可用 —— 将来实现可以直接用。

### 结论 3:根因是**命令行引号缺失**,不是 TAP 驱动问题 ✅

**这是整轮探测最重要的发现,而且推翻了之前所有的猜测。**

在提权环境下用真实网卡名运行时,edge 报的是:

```
Cannot find tap device "以太网"
```

注意:**名字被截断了 —— `以太网 2` 变成了 `以太网`,空格后面的 `2` 丢了。**

原因是调用方式。`Start-Process -ArgumentList` 会把数组元素直接拼成命令行字符串,
**不会为含空格的元素自动加引号**。所以:

```powershell
Start-Process edge.exe -ArgumentList @('-d', '以太网 2', ...)
# 子进程实际收到两个参数:  -d 以太网      和      2
```

用子进程自报 argv 的方式验证(见 `.probe-edge/elevated-edge3.ps1` 的 `[0]` 段):

```
[unquoted]  argv[0]=<-d>   argv[1]=<以太网>    argv[2]=<2>        ← 分裂成两个
[quoted]    argv[0]=<-d>   argv[1]=<以太网 2>                     ← 正确
```

**修法是把引号写进参数字符串本身:**

```powershell
Start-Process edge.exe -ArgumentList @('-d', '"以太网 2"', ...)
```

加上引号之后,edge 立刻就通了:

```
Open device [name={2952F71C-796E-4095-ADEC-17B69F870498}][ip=10.236.167.114][ifName=以太网 2][MTU=1290][mac=00:FF:29:52:F7:1C]
[edge.c:1258] created local tap device IP: 10.236.167.114, Mask: 255.255.255.0, MAC: 00:FF:29:52:F7:1C
[edge.c:1356] edge started
[edge_utils.c:1147] successfully joined multicast group 224.0.0.68:1968
```

**顺带被证伪的几个猜测**(记录在此以免以后重犯):

| 曾经的猜测 | 实际情况 |
|---|---|
| TAP 网卡被禁用 | ❌ `AdminStatus = Up` |
| 驱动处于"半注册"状态 | ❌ `tapinstall status` 说 `Driver is running`,`Get-PnpDevice` 说 `Status=OK` |
| TAP 驱动 9.21.2 与新系统不兼容 | ❌ 驱动工作完全正常 |
| 残留的 `169.254.x` 地址导致失败 | ❌ 不是原因(不过 edge 成功运行时顺手清掉了它) |

**一条元教训:** 我先后三次基于不完整证据下了错结论("网卡被禁用"、"半注册"、"驱动不兼容"),
每次都写进了文档又推翻。真正定位问题靠的是**让子进程自报它实际收到了什么**,
而不是从外部现象反推。这也是为什么 `.probe-edge/` 里的脚本值得保留 ——
它们记录了"怎么问出真相",而不只是"真相是什么"。

### 结论 4:`edge.exe` 确实需要管理员权限 ✅

同一条命令、同一块网卡、同样正确的引号,只差权限:

| 条件 | 结果 |
|---|---|
| **提权** + 正确引号 | `Open device` → `created local tap device` → `edge started` ✅ |
| **非特权** + 正确引号 | `Cannot find tap device "以太网 2"` 立即退出 ❌ |

**结论:启动 `edge.exe` 必须提权。** 这不是可以回避的实现细节,而是架构约束。

**因此提权辅助进程是必需品,而且它的生命周期会变长** ——
不再是"装一次驱动就退出",而是**每次连接都要以管理员身份启动 edge**。
攻击面因此扩大,实现时必须:

- 辅助进程只接受主程序通过命名管道的指令,管道 ACL 限制到当前用户
- 不解析任何外部输入
- 用完即退,不常驻

**对用户体验的影响:** 每次连接都会弹 UAC。必须向用户解释原因 ——
原手工方案让用户"以管理员身份打开 cmd"其实就是在做同一件事,只是从未说明为什么。

### 结论 5:超级节点 `1200s.top:7777` 当前不响应 ⚠️

提权下用真实组名与密码(`mygroup` / `mysecretpassword`)连接,edge 反复重试但始终拿不到回包:

```
[edge_utils.c:1595] registering with supernode [1200s.top:7777][attempts left 2]
[n2n.c:279] supernode2sock successfully resolves supernode IPv4 address for 1200s.top
[edge_utils.c:1556] WARNING: supernode not responding, now trying [1200s.top:7777]
```

**两种独立途径都确认了这一点:**

1. 上面的手工探测(edge + 真实 `-a`)
2. **`doctor --probe-edge` 的自动化实测**(edge + 不指定 `-a`,走要 IP 路径):
   ```
   [edge.c:1149] automatically assign IP address by supernode
   [edge.c:1221] send REGISTER_SUPER to supernode [1200s.top:7777] asking for IP address
   ```
   反复发送,始终没有分配到 IP。

**已排除的原因:**

| 原因 | 是否成立 |
|---|---|
| DNS 解析失败 | ❌ `1200s.top` 正常解析到 `39.105.139.45` |
| TAP 网卡问题 | ❌ 同一进程里网卡已成功创建(手工探测),或明确走到了要 IP 这一步 |
| 加密/密钥问题 | ✅→❌ `using AES cipher.` 正常 |
| 本地 UDP 被拦 | ❌ 包已发出(见 `send REGISTER` / `REGISTER_SUPER`) |

**未排除的原因:** 服务器上 supernode 进程没在运行、UDP 7777 未放行、
或 n2n 版本与超级节点不兼容。

**注意:** 同一台服务器的 TCP 7000 端口(frps)是**可达**的,
所以服务器本身在线,问题局限在 supernode 的 UDP 7777 上。

**一个重要的诊断方法论收获:** 这是**服务端故障,不是客户端故障**。
`doctor --probe-edge` 现在能明确区分这两者 ——
当它报"本机配置正常,但超级节点没有分配虚拟 IP"时,
说明设备层和参数传递都没问题,继续在本机排查不会有结果。


### 结论 6:`n2n-connect.ps1` 的 ASCII 回退逻辑是巧合的正确

原脚本里有一段:若系统代码页不是 936,就把中文网卡名改成 `n2n-TAP`。
从注释看,作者认为问题出在"编码转换导致乱码"。

**实际情况是:那个改动确实能让脚本工作,但原因不是编码。**
`"以太网 2"` 里的**空格**才是真正的杀手 —— 换成不含空格的 `n2n-TAP`,
正好绕开了引号缺失的问题。**纯属巧合。**

这个区别很重要:如果按"编码问题"去理解,就会以为只要网卡名是纯 ASCII 就安全;
而实际上**任何含空格的网卡名都会触发同一个 bug**,
包括 `Local Area Connection 2` 这类英文默认名。

**实现要求:网卡名参数必须加引号,不能依赖"名字是 ASCII"这个假设。**

### 结论 7:启动顺序被证实

从日志看,edge 的初始化顺序是:

```
1. 解析超级节点地址              adding supernode = 1200s.top:7777
2. 读取加密配置(此时已读 N2N_KEY)  using AES cipher.
3. 创建 DNS 解析线程             successfully created resolver thread
4. 处理 -a 指定的 IP             use manually set IP address
5. 打开 TAP 网卡                Open device [...]  ← 引号缺失时在这一步失败
6. 分配 IP 并启动               created local tap device IP: ... / edge started
7. 注册到超级节点                registering with supernode [...]
```

**关键:密钥在第 2 步就被读取,而引号问题在第 5 步才暴露。**
两件事互不相干 —— 这也说明"环境变量生效"这个结论不受网卡问题影响。

### 结论 8:本机跑超级节点可以把整条链路验证完 ✅

因为 `supernode.exe` 就在 `assets/` 里,可以**完全不用别人的服务器**验证客户端:

```powershell
# 1) 本机启动超级节点(UDP 7777)
supernode.exe -p 7777 -v

# 2) 让 edge 连本机(需要管理员权限)
edge.exe -d "以太网 2" -c local-probe-community -a 10.236.167.114 -l 127.0.0.1:7777 -v
```

实测结果(完整握手):

```
edge:      [edge_utils.c:2449] Rx REGISTER_SUPER_ACK from ... [127.0.0.1:7777]
           [edge_utils.c:2508] [OK] edge <<< ================ >>> supernode
           [edge_utils.c:2611] Rx PONG from supernode version '3.1.0'
supernode: [sn_utils.c:1914] new community: local-probe-communi
           [sn_utils.c:1296] assigned sub-network 10.235.158.0/24 to community
           [sn_utils.c:1098] created edge  00:FF:29:52:F7:1C ==> 127.0.0.1:53897
```

**这是本轮最有价值的排障手段。** 它的意义在于:

- **把"服务端不可用"从关键路径上摘掉。** 客户端工作不必等服务器恢复
- **万一将来出问题,可以立刻判断是客户端还是服务端** —— 本地能连、远端不能连,就是服务端
- 脚本在 `.probe-edge/local-roundtrip.ps1`(非提权,验证超级节点本身)与
  `.probe-edge/local-roundtrip-elevated.ps1`(提权,验证完整握手)

**两个实测踩到的坑:**

1. **超级节点日志里没有 "REGISTER" 这个字面词。** 它的措辞是
   `new community` / `assigned sub-network` / `created edge`。
   第一版判定表按 "REGISTER" 匹配,结果在一次**完全成功**的运行上报了假阴性。
2. **日志里的社区名被截断到 16 个字符。** `local-probe-community` 显示为
   `local-probe-communi`。按全名匹配同样会失败。
   **这意味着社区名的前 16 个字符必须唯一** —— 两个社区名前 16 字符相同会被超级节点视为同一个。


### 结论 8:实现必须注意的几点(汇总)

上面各条结论落到代码上,变成这些硬性要求:

| 要求 | 来源 |
|---|---|
| **网卡名参数必须自带引号**:`'"' + name + '"'` | 结论 3。含空格的网卡名会分裂成两个参数 |
| **不能假设"ASCII 名就安全"** | 结论 6。`Local Area Connection 2` 同样含空格 |
| **密钥走 `N2N_KEY` 环境变量,不用 `-k`** | 结论 1、2 |
| **环境变量设置后必须立即恢复** | 否则密钥扩散到整个进程树 |
| **启动 edge 必须提权** | 结论 4 |
| **不要把"supernode 无响应"当成客户端故障** | 结论 5。需要能区分"本地配置对但服务端没回" |


### 后续开发

1. **TAP 网卡管理**:检测 / 安装 / 重命名 / 启用,含 `tapinstall` 兜底路径
2. **进程管理**:启停 `edge.exe` / `frpc.exe`,stdout 实时捕获与脱敏
3. **凭据存储**:DPAPI 加解密、凭据库读写
4. **连接向导**:自动挑选空闲虚拟 IP、输入密钥、生成配置
5. **WPF 界面**:两个功能页、状态机、实时日志面板、托盘常驻
6. **提权辅助进程**:命名管道 IPC,用完即退
7. **打包分发**:单文件或 MSIX,含杀软白名单说明

### 工程改进

- [ ] 引入 xUnit 后把 `Twice1200.SelfTest` 的用例迁移过去
- [ ] 把 `AnalysisLevel` 提到 `latest-recommended`,分阶段清理 CA 警告
- [ ] 为 `Twice1200.App` 与 `Twice1200.Helper` 建立项目骨架

---

## 许可

**本项目的源代码采用 [MIT 许可证](LICENSE)。**

```
Copyright (c) 2026 Fisphame
```

可以自由使用、修改、再分发,包括用于商业目的,条件是保留版权声明。
软件按"原样"提供,不附带任何担保。

### 但 `assets/` 里的第三方二进制不是 MIT

这是**最需要注意的一点**:本仓库同时包含几个第三方二进制,
**它们各自适用自己的许可证,不受上面的 MIT 覆盖。**

| 组件 | 许可证 | 说明 |
|---|---|---|
| `assets/frp/frpc.exe` | **Apache-2.0** | 允许再分发,**必须保留 LICENSE 文件**。已放 `assets/frp/LICENSE-frp.txt` |
| `assets/n2n/edge.exe`、`supernode.exe` | **GPL-3.0-or-later** | 见下方说明 |
| `assets/tap/tap-windows-9.21.2.exe` | **GPLv2**(OpenVPN 项目的一部分) | 随包分发前需确认条款 |

n2n 的许可证经查证为 GPL-3.0-or-later,来源是
[GNU Guix 的 n2n 软件包元数据](https://packages.guix.gnu.org/packages/n2n/)
(标注 "Licenses: GPL 3+")。二进制本身经检查确认由 ntop 官方源码构建:
内嵌字符串为 `Copyright 2007-2021 - ntop.org and contributors`,版本 `3.1.0`。

**当前仓库里缺 n2n 与 TAP 的许可证文件**,分发前需要补上。

### 如果只想用 MIT 的部分

有一个需要知道的后果:**把 GPL 的二进制与 MIT 的源代码一起分发,
整体分发需要同时满足 GPL 的要求** —— 保留许可证与版权声明是底线。

两种做法:

1. **只分发源代码,让使用者自行获取第三方二进制** —— 这样 MIT 就是唯一约束。
   本项目的 `assets/README.md` 说明了各个二进制的来源与放置方式。
2. **随包分发二进制** —— 必须补齐所有许可证文件,并保留它们的版权声明。

**这不是法律意见。** 如果计划正式对外发布,建议就 "MIT 代码 + GPL 二进制"
的组合方式咨询一下。上面写的是查证到的事实与一般性要求。

### 代码签名

**所有第三方二进制都没有代码签名。** Windows SmartScreen 首次运行会拦截并显示
"未知发布者"的警告,这是给无基础用户分发时必然会遇到的问题,需要提前准备好说明文案。

