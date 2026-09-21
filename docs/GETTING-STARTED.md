# 快速启动

GAssetForge 当前是 Windows 桌面学习项目。建议先运行本地图像工具，再按需配置 AI 功能。

## 1. 准备开发环境

- Windows 10/11 x64。
- .NET 8 SDK；`global.json` 允许使用较新的 .NET 8 feature band。
- PowerShell 7 或 Windows PowerShell 5.1。
- Git 用于克隆；也可以下载源码 ZIP 后解压。
- Visual Studio 2022 可选。命令行构建不依赖其他私有仓库或发布工具。

运行 `dotnet --list-sdks`，确认列表里有 `8.0.xxx`。仅安装 .NET Runtime 不足以构建源码。

## 2. 构建并启动

在包含 `GAssetForge.sln` 的仓库根目录打开 PowerShell：

```powershell
dotnet restore .\GAssetForge.sln --locked-mode
dotnet build .\GAssetForge.sln -c Debug --no-restore
dotnet run --project .\src\GAssetForge.App\GAssetForge.App.csproj -c Debug --no-build
```

先构建整个 solution，确保桌面端和 MCP Server 都已生成。某条命令失败时，先处理该错误再执行后续命令。

也可以生成可双击的开发启动器：

```powershell
.\scripts\build-engineering-entry.ps1 -Verify
```

以后双击仓库根目录 `GAssetForge.exe`。它会增量构建当前源码并在成功后启动 Debug 桌面程序。
此入口依赖开发机的 SDK；给其他电脑用的自包含便携包见[打包说明](../scripts/README.md)。

## 3. 先试本地功能

1. 打开 `文件 → 新建项目…`，选择一个已经存在的父目录和业务模板。
2. 想体验图层与画布时选择“页面 UI 设计工具”；卡牌和部分其他模板目前仍是占位入口。
3. 从“视图”菜单打开背景剔除、图片描边或像素对齐工具，导入自己的测试图片。
4. 先查看预览，再保存到一个新的输出目录。

以上本地操作不需要 Agent、Python 或 API Key。项目内容与用户设置的保存位置见[项目工作区](PROJECTS.md)。

## 4. 可选：连接 AI 生成

当前主线实现连接 Codex Runtime，通过本地 MCP 向 Agent 提供任务与交付工具。
这是实验性集成，服务端模型能力和外部工具版本会影响实际结果。

### 运行条件

| 环境 | 用途 |
| --- | --- |
| 可启动且已完成认证的 Codex CLI / Runtime | 建立 Agent 会话；具体可用能力取决于该运行环境 |
| Agent 中可用的图像模型工具 | 普通整图生成的首选路径 |
| Python 和可导入的 `openai` SDK | 本地图像 API CLI 交付及区域编辑 |
| 官方 imagegen skill 的 `scripts/image_gen.py` | CLI fallback 与区域编辑调用入口，本仓库不附带该脚本 |
| `OPENAI_API_KEY` | 本地 CLI 调用 Image API 所需；应用不会把 Codex 登录转换成 API Key |
| 输出目录写权限和可用网络 | 保存真实模型原图并访问配置的模型服务 |

源码当前在 `<CODEX_HOME>/skills/.system/imagegen/scripts/image_gen.py` 查找脚本；
未设置 `CODEX_HOME` 时使用用户目录中的 `.codex`。这是当前实现的路径约束，
不能假设所有 Codex 安装都会自动包含它。请按所用环境提供的官方 imagegen skill 安装说明准备脚本和依赖；
缺少它时，CLI fallback 和区域编辑不能使用。

应用会检测 Python、脚本能否执行 `--help`、OpenAI SDK 能否导入、API Key 是否存在及目录是否可写。
本地预检通过不代表网络、认证、额度或真实生成已经成功。
普通任务是否可派发由预检结果决定，缺失 CLI fallback 时部分路径仍可能依赖 Agent 内置图像能力。

### 环境变量

| 变量 | 含义 |
| --- | --- |
| `OPENAI_API_KEY` | CLI fallback 的认证凭据，通过本机环境配置，勿写入仓库或日志 |
| `OPENAI_BASE_URL` | 可选的 Image API 地址覆盖；仅在明确使用相应端点时配置 |
| `CODEX_HOME` | 可选的 Codex 主目录，也决定上述 skill 脚本查找位置 |
| `GASSETFORGE_PYTHON_EXECUTABLE` | 可选的 Python 可执行文件完整路径，用于解决多版本环境选择问题 |

本仓库不会自动读取 `.env` 文件。设置环境后从能继承这些变量的进程启动应用。
账户访问、模型服务及实际调用费用由所配置的服务决定。

### 在界面中连接

1. 从 `视图 → MCP Agent 接入` 打开面板，选择 Codex 并启动会话。
2. 等待 Runtime 与 `gasset_forge` MCP 就绪；失败时阅读面板诊断。
3. 先用一个小任务验证输入、原图保存、本地处理与最终交付。
4. 遇到中断时使用当前批次的恢复入口，避免反复创建同一批任务。

完整协议见 [MCP](MCP.md)，故障定位见[生成诊断](GENERATION-DIAGNOSTICS.md)。
本项目的自动测试不执行真实付费图像生成，测试通过不能替代你自己环境中的端到端验证。

## 常见问题

- **找不到 SDK / 不支持目标框架**：检查 `dotnet --list-sdks`；本项目要求 .NET 8 SDK 和 Windows。
- **找不到 MCP Server**：先按上面的命令构建整个 solution，或使用工程启动器。
- **界面打开但无法生成**：本地桌面功能和远程 AI 环境是两组条件，按上方表格逐项确认。
- **找不到 image_gen.py**：确认实际的 `CODEX_HOME` 及 skill 安装路径；单独安装 Python 不会安装该脚本。
- **便携包被提示未签名**：当前没有代码签名，源码构建与便携包的状态都在首页标明。
- **在哪里提问**：普通问题走 Issues；安全问题按 [SECURITY.md](../SECURITY.md) 私下报告。
