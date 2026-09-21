# Windows 桌面版发布

在仓库根目录执行：

```powershell
.\scripts\publish-windows-x64.ps1
```

默认产物：

```text
src\GAssetForge.App\bin\Release\net8.0-windows\win-x64\publish-single-file\GAssetForge.exe
```

这是 Windows x64 自包含单文件版本，目标电脑无需预先安装 .NET 8。WPF 与 AvalonDock 不进行裁剪，以保证主题、资源字典、停靠布局和反射加载的兼容性。

需要写到其他目录时：

```powershell
.\scripts\publish-windows-x64.ps1 -OutputDirectory "D:\Releases\GAssetForge"
```

也可以直接调用发布配置：

```powershell
dotnet publish .\src\GAssetForge.App\GAssetForge.App.csproj -c Release -p:PublishProfile=Windows-x64
```

当前发布生成的是未签名的便携 EXE，不包含安装程序或代码签名证书。

如需同时发布桌面端、MCP Server 和接入说明，执行：

```powershell
.\scripts\publish-distribution-windows-x64.ps1
```

默认输出到：

```text
dist\GAssetForge-win-x64
```

完整发行包还会包含 MCP 接入说明、生成诊断、配置示例、项目 MIT `LICENSE`、第三方许可文本和 `SHA256SUMS.txt`。

脚本可直接从本仓库构建便携包，不依赖仓库外的发布工具。建议每次选择新的输出目录，避免混入旧文件。
`dist/` 与其他生成目录不提交到 Git；若分享二进制包，请一并保留许可文本和校验和。
