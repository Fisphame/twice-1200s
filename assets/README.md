# assets 目录说明

这个目录存放**程序运行时需要的外部资源**(第三方二进制与驱动)。
代码按下面的约定在这里查找它们。

---

## 期望的目录结构

```
assets/
├── n2n/
│   ├── edge.exe                  n2n 边缘节点(必需)
│   ├── supernode.exe             超级节点(仅自建服务器时需要)
│   └── LICENSE                   随 n2n 分发
├── tap/
│   └── tap-windows-9.21.2.exe    TAP 虚拟网卡驱动安装包(必需)
├── frp/
│   ├── frpc.exe                  frp 客户端(必需)
│   ├── frps.exe                  frp 服务端(仅部署服务器时需要)
│   └── LICENSE                   frp 为 Apache-2.0,必须随分发保留
└── supernode.txt                 (可选)自建 supernode 的说明/备注
```

程序按以下顺序解析资源,**先匹配先生效**:

1. 命令行参数 `--assets <目录>`
2. 环境变量 `TWICE1200_ASSETS`
3. 从可执行文件所在目录向上逐级查找 `assets/`
4. 当前工作目录下的 `assets/`
5. 开发期回退:`114514/` 素材目录(仅本地开发用,分发时不存在)

---

## 如何填充这个目录(开发阶段)

素材在 `C:\Users\Phamoce\Desktop\eng\114514\` 下。执行:

```powershell
$src = 'C:\Users\Phamoce\Desktop\eng\114514'
$dst = 'C:\Users\Phamoce\Desktop\eng\program\Twice1200\assets'

New-Item -ItemType Directory -Force -Path "$dst\n2n", "$dst\tap", "$dst\frp" | Out-Null

Copy-Item "$src\n2n\n2n\n2n_v3_win\edge.exe"        "$dst\n2n\" -Force
Copy-Item "$src\n2n\n2n\n2n_v3_win\supernode.exe"   "$dst\n2n\" -Force
Copy-Item "$src\n2n\n2n\tap-windows-9.21.2\tap-windows-9.21.2.exe" "$dst\tap\" -Force
Copy-Item "$src\frp\frp\frpc.exe"                   "$dst\frp\" -Force
Copy-Item "$src\frp\frp\frps.exe"                   "$dst\frp\" -Force
Copy-Item "$src\frp\frp\LICENSE"                    "$dst\frp\LICENSE-frp.txt" -Force
```

---

## 注意事项

### 授权

- **frp** 是 Apache-2.0,**允许再分发**,但必须保留 LICENSE 文件。见 `114514/frp/frp/LICENSE`
- **n2n** 是 GPLv3 系许可证,**分发前务必确认授权条款**,并随包附上其 LICENSE
- **TAP-Windows 驱动**有独立授权,随包分发前同样需要确认

### 安全

- 二进制**没有代码签名**,Windows SmartScreen 首次运行会拦截并弹警告,
  这是"给无基础用户分发"时一定会遇到的体验问题,需要提前想好说法
- 这些二进制来自 `114514/`,**来源与完整性未经验证**。正式分发前建议:
  1. 核对官方发布的哈希值
  2. 在干净环境里确认没有捆绑物
  3. 记录下每个文件的 SHA256 到 `assets/CHECKSUMS.txt`

### 版本现状(2026-05 探测)

| 文件 | 大小 | 时间戳 | 说明 |
|---|---|---|---|
| `edge.exe` | ~176 KB | 2026-05-17 | n2n v3(ntop 版),字符串显示 `Copyright 2007-2021` |
| `supernode.exe` | ~112 KB | 2026-05-17 | 同上 |
| `tap-windows-9.21.2.exe` | ~250 KB | **2020-03-30** | 驱动较旧;TAP-Windows 上游项目已归档 |
| `frpc.exe` | ~16 MB | 2026-03-20 | Go 编译,含 `pkg/vnet` 等较新模块,应为较新版本 |
| `frps.exe` | ~19.7 MB | 2026-03-20 | 同上 |

**已知风险:** TAP-Windows 9.21.2 是 2020 年的驱动,在较新的 Windows 11(本机 build 26200)上
可能安装失败或安装后无法创建网卡实例。程序必须为此提供清晰的错误提示与兜底路径。
