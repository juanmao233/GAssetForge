# 代码导读

这个项目主要分享实现过程和工程经验。可以单独阅读或提取模块，不必先理解所有界面。
原创代码采用 [MIT](../LICENSE)，依赖仍遵守[各自许可](../THIRD-PARTY-NOTICES.md)。

## 建议阅读顺序

1. [领域上下文](../CONTEXT.md)：项目、任务、Run、最终产物分别是什么。
2. `Core`：先读一个不依赖 WPF 的确定性模块及其测试。
3. `McpServer`：看外部 Agent 如何通过窄工具边界驱动本地逻辑。
4. `App/Services` 和 `ViewModels`：看界面、任务派发、状态回传如何连接。

## 按兴趣选择入口

| 想学习什么 | 实现入口 | 测试入口 |
| --- | --- | --- |
| JSON 持久队列、跨进程锁、原子写入与租约 | [JsonGenerationTaskStore](../src/GAssetForge.Core/Generation/JsonGenerationTaskStore.cs) | [LeaseRegressionTests](../tests/GAssetForge.SmokeTests/LeaseRegressionTests.cs) |
| 色键剔除与假透明棋盘格处理 | [ImageSharpBackgroundRemovalService](../src/GAssetForge.Core/Imaging/ImageSharpBackgroundRemovalService.cs) | [BackgroundRemovalSmokeTests](../tests/GAssetForge.SmokeTests/BackgroundRemovalSmokeTests.cs) |
| Alpha 图片描边 | [ImageSharpImageOutlineService](../src/GAssetForge.Core/Imaging/ImageSharpImageOutlineService.cs) | [ImageOutlineSmokeTests](../tests/GAssetForge.SmokeTests/ImageOutlineSmokeTests.cs) |
| 像素采样、量化与透视校正 | [ImageSharpPixelAlignmentService](../src/GAssetForge.Core/Imaging/ImageSharpPixelAlignmentService.cs) | [PixelAlignmentSmokeTests](../tests/GAssetForge.SmokeTests/PixelAlignmentSmokeTests.cs) |
| 图片局部选区和旋转坐标转换 | [ArtWorkspaceImageRegionGeometry](../src/GAssetForge.Core/ArtWorkspaces/ArtWorkspaceImageRegionGeometry.cs) | [ArtWorkspaceRegionEditRegressionSmokeTests](../tests/GAssetForge.SmokeTests/ArtWorkspaceRegionEditRegressionSmokeTests.cs) |
| Agent 子进程与 App Server 通信 | [AgentRuntime](../src/GAssetForge.App/Services/AgentRuntime) | [AgentRuntimeTransportSmokeTests](../tests/GAssetForge.SmokeTests/AgentRuntimeTransportSmokeTests.cs) |
| 批次串行派发与中断恢复 | [GenerationBatchDispatchCoordinator](../src/GAssetForge.App/Services/GenerationBatchDispatchCoordinator.cs) | [GenerationBatchDispatchCoordinatorSmokeTests](../tests/GAssetForge.SmokeTests/GenerationBatchDispatchCoordinatorSmokeTests.cs) |
| 模型原图导入、校验和幂等交付 | [GeneratedOutputSubmissionService](../src/GAssetForge.McpServer/GeneratedOutputSubmissionService.cs) | [GeneratedOutputSubmissionSmokeTests](../tests/GAssetForge.SmokeTests/GeneratedOutputSubmissionSmokeTests.cs) |
| 蓝图验证与执行计划 | [Blueprints](../src/GAssetForge.Core/Blueprints) | [BlueprintWorkflowSmokeTests](../tests/GAssetForge.SmokeTests/BlueprintWorkflowSmokeTests.cs) |
| WPF 停靠、图层、Prefab 与撤销重做 | [Views](../src/GAssetForge.App/Views) / [ViewModels](../src/GAssetForge.App/ViewModels) | [Smoke runner](../tests/GAssetForge.SmokeTests/Program.cs) |

## 一条生成任务怎样走完

```text
界面冻结参考图、尺寸和输出约束
    → 本地任务队列保存 Pending 任务
    → Agent Runtime 接收任务，通过 MCP 认领
    → 真实图像模型生成原图
    → Harness 导入原图并执行本地后处理
    → 按冻结合同校验、提交最终文件、更新任务状态
```

模型负责创造性生成。目录、认领身份、图像后处理与最终文件提交由本地代码控制。
模型声称成功不会直接让任务完成；软件需要实际存在且通过检查的输出文件。
更多边界见[架构说明](ARCHITECTURE.md)和 [MCP](MCP.md)。

## 复用时需要注意

- `Core` 面向 .NET 8，不引用 WPF；图像处理部分依赖 ImageSharp。
- `App` 与测试 runner 使用 Windows/WPF，不能直接作为跨平台应用运行。
- 队列、项目文件和生成合同包含版本与兼容逻辑，提取代码时同时阅读验证器和模型。
- 一些 ViewModel 和测试文件较大，仍有拆分空间；这里展示的是持续演进中的练习项目。
- 测试采用自定义控制台 runner，执行方式是 `dotnet run`，不是依赖 `dotnet test` 自动发现。

构建和测试命令见[贡献指南](../CONTRIBUTING.md)。
