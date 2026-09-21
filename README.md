# GAssetForge

面向游戏美术资产制作的 Windows 桌面工作台，也是一个分享 WPF、图像处理与 AI 工作流实现的个人练手项目。

A Windows game-art workbench built with .NET 8 and WPF. An early-stage learning project with local image tools, durable generation tasks, and an Agent/MCP integration.

**开发中 / Early alpha · Windows · .NET 8 · [MIT](LICENSE)**

[![CI](https://github.com/juanmao233/GAssetForge/actions/workflows/ci.yml/badge.svg)](https://github.com/juanmao233/GAssetForge/actions/workflows/ci.yml)

功能尚未完整，界面和内部接口仍会变化。欢迎学习、改造、提出问题和贡献代码；希望其中的实现能给你一些参考。

[快速启动](docs/GETTING-STARTED.md) · [代码导读](docs/CODE-TOUR.md) · [详细手册](docs/WORKBENCH-GUIDE.md) · [贡献指南](CONTRIBUTING.md)

## 目前能做什么

| 方向 | 当前状态 |
| --- | --- |
| 本地图像处理 | 背景剔除、假透明棋盘格识别、图片描边、像素对齐、透视矫正与批处理已有实现 |
| 桌面工作台 | AvalonDock 停靠面板、主题、布局保存、项目隔离已有实现 |
| 页面 UI 编辑 | 原生画布、图层/文件夹、文字、基础 Prefab、属性、Undo/Redo 已有实现，编辑能力仍在完善 |
| 美术工作区 | 参考图、局部框选、图片指代和输出框已有实现；AI 生成与局部编辑依赖外部运行环境 |
| 资产生成 | 持久任务队列、批次、认领租约、中断恢复及产物校验已有实现；需配置 Agent 和图像模型 |
| Tilemap / Icon | 网格、几何约束、相机预览和生成合同已有实现，仍是实验性工作流 |
| 蓝图 | 节点编辑、执行计划和审计目录已有实现，尚未形成完整通用工作流系统 |
| 卡牌 / 平行透视物体 | 业务模板目前主要是占位入口 |
| 其他 Agent | 主要接入 Codex；OpenCode 仅保留 Adapter 入口 |

本地编辑与图像处理不需要 API Key。AI 功能需要另外配置运行环境和账户，克隆仓库不会自动获得图像模型服务。

## 从源码运行

需要 Windows 10/11、.NET 8 SDK 和 PowerShell。Git 用于克隆；Visual Studio 2022 可选。

克隆仓库后执行：

```powershell
git clone https://github.com/juanmao233/GAssetForge.git
cd GAssetForge
dotnet restore .\GAssetForge.sln --locked-mode
dotnet build .\GAssetForge.sln -c Debug --no-restore
dotnet run --project .\src\GAssetForge.App\GAssetForge.App.csproj -c Debug --no-build
```

需要可双击的开发入口时：

```powershell
.\scripts\build-engineering-entry.ps1 -Verify
```

脚本在仓库根目录生成 `GAssetForge.exe`。它依赖 .NET 8 SDK，并会构建当前 App 和 MCP Server 后启动桌面端。生成物不提交到 Git。

AI 配置、第一次使用及常见问题见[快速启动](docs/GETTING-STARTED.md)。

## 可以从哪些代码开始读

- **本地持久任务队列**：JSON 原子保存、跨进程锁、认领租约和恢复。
- **图像处理**：色键剔除、棋盘格检测、Alpha 描边、像素采样与量化。
- **Agent + MCP**：子进程通信、就绪判断、事件归一化和工具边界。
- **生成交付**：冻结任务约束、保存模型原图、确定性后处理、校验后提交。
- **WPF 编辑器**：停靠布局、画布坐标、图层树、Prefab 与 Undo/Redo。

具体入口和配套测试见[代码导读](docs/CODE-TOUR.md)。

```text
src/GAssetForge.Core/                 合同、验证、几何与本地图像处理
src/GAssetForge.App/                  WPF 界面与桌面服务
src/GAssetForge.McpServer/            HTTP / stdio MCP 和生成交付
src/GAssetForge.EngineeringLauncher/ 源码工程启动器
tests/GAssetForge.SmokeTests/         隔离 smoke / 回归测试
```

## 验证与打包

```powershell
.\scripts\verify-text-format.ps1
dotnet restore .\GAssetForge.sln --locked-mode
dotnet build .\GAssetForge.sln -c Release --no-restore
dotnet run --project .\tests\GAssetForge.SmokeTests\GAssetForge.SmokeTests.csproj -c Release --no-build
```

测试使用临时数据目录，不调用付费图像 API。GitHub Actions 在 Windows 上运行构建与 smoke suite。

自行生成 Windows x64 便携包：

```powershell
.\scripts\publish-distribution-windows-x64.ps1
```

产物位于 `dist/GAssetForge-win-x64`，包含 App、MCP Server、文档、许可及校验和。两个 EXE 为自包含程序；当前没有安装器和代码签名。完整说明见[构建与打包](scripts/README.md)。

## 文档与交流

- [领域上下文](CONTEXT.md) / [仓库结构](docs/REPOSITORY.md) / [架构说明](docs/ARCHITECTURE.md)
- [项目与数据目录](docs/PROJECTS.md) / [Agent 与 MCP](docs/MCP.md) / [生成诊断](docs/GENERATION-DIAGNOSTICS.md)
- [更新记录](CHANGELOG.md) / [贡献指南](CONTRIBUTING.md) / [支持](SUPPORT.md)
- [数据与隐私](DATA_AND_PRIVACY.md) / [安全报告](SECURITY.md)

欢迎通过 Issues 交流问题和建议。提交日志、图片或项目文件前，请去掉令牌、私人路径和不打算公开的素材。

## 许可证

项目原创代码采用 [MIT License](LICENSE)，欢迎学习、修改和复用，包括商业使用；分发时请保留版权与许可声明。
第三方组件继续适用各自的许可证，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
外部模型服务、用户导入素材和生成产物不因本仓库采用 MIT 而自动获得相同授权。

这是独立个人项目，与 OpenAI、Codex、Kenney Asset Forge 或其他相近名称项目没有隶属关系。
