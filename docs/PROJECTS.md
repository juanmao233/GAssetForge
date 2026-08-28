# 项目工作区

GAssetForge 使用类似 Unity 的独立项目目录。业务模板不再是顶部即时切换入口，而是在创建项目时确定
该项目的初始业务主视图与项目级约束。

## 创建项目

通过 `文件 → 新建项目…` 或 `Ctrl+N` 打开新建项目页，填写：

- 项目名称
- 一个已经存在的父目录
- Tilemap、卡牌、平行透视角物体、页面 UI 或测试模板

选择模板只会修改待创建配置，不会提前改变当前工作区。点击“创建并进入项目”后，应用才创建目录、切换
UI repository、锁定 Project 面板并进入所选业务主视图。

当前版本尚未提供手动“打开已有项目”入口；最后一次有效项目通过全局设置中的 `lastProjectPath` 在重启时
自动恢复。

## 目录契约

```text
<ProjectRoot>\
  Assets\
    ArtWorkspace\
      References\
    Generated\
      ArtWorkspace\
  Packages\
  ProjectSettings\
    ArtWorkspace\
      art-workspace.v1.json
      art-workspace.v1.json.bak
    gassetforge.project.json
    gassetforge.project.json.bak
    ui-project.pfui.json
    ui-project.pfui.json.bak
```

初次创建时只保证项目核心主文件存在；美术工作区目录和 JSON 在该工作区首次激活时按需建立，`.bak` 会在
主文件发生后续原子替换时建立。

- `Assets`：当前项目全部美术资产的边界。
- `Assets\ArtWorkspace\References`：美术工作区导入参考图的受管副本；工作区 JSON 不引用项目外绝对路径。
- `Assets\Generated`：AI 生成内容的默认交付目录。
- `Assets\Generated\ArtWorkspace\<batchId>\Final`：美术工作区 Agent 生成的冻结最终交付目录；模型原图和确定性中间步骤仍保留在同项目的 GAssetForge Run 中。
- `Packages`：为后续包与扩展依赖预留。
- `gassetforge.project.json`：manifest Schema v1，保存项目身份、名称、工作流、时间戳和 Tilemap 参数。
- `ui-project.pfui.json`：UI Project Schema v2，保存页面、图层、Prefab 与编辑器会话数据。
- `ArtWorkspace\art-workspace.v1.json`：Art Workspace Schema v1，保存画布、视口、输出框、图片摆放、当前
  图片的本地归一化框选和创作意图；框选字段可选，旧版工作区可继续加载。

重命名前创建的项目可能仍使用 `perspective-forge.project.json`。加载器会在 canonical 清单不存在时读取并
继续保存旧文件名；不会在后台批量重写用户项目。新建项目始终使用 `gassetforge.project.json`。

Project 面板在项目激活后固定以 `<ProjectRoot>\Assets` 为根，不能从面板中切换到项目外目录。如果
`Assets` 被外部删除或暂时不可访问，面板会保持原项目根并显示不可用状态，不会回退到应用全局 Assets，
也不会允许“创建文件夹”隐式写入其他数据根。

## 创建安全边界

`ProjectWorkspaceService` 会验证：

- 项目名称符合 Windows 文件名规则，且不是 `CON`、`NUL`、`COM1` 等保留设备名。
- 所选父目录存在、不是文件系统根目录，也不是符号链接或目录联接。
- 目标项目是父目录的直接子目录，且目标文件或目录尚不存在。
- manifest、UI 项目和必要目录全部写入并重新验证成功，项目 ID 与名称必须一致。
- manifest 与 UI 项目的主文件、备份文件均不能是符号链接或目录联接。
- 美术工作区 JSON 和参考素材必须留在各自受管目录；只接受规范化相对路径，并拒绝目录逃逸和重解析点。
- 美术工作区框选必须绑定当前所选图片，边界位于该图片未旋转本地空间的 `0..1` 范围；切换、隐藏、删除或
  缺失图片时不得保留悬空框选。用户对话记录只保存图片名称和整图/局部摘要，不暴露源像素或画布坐标。

创建过程先写入同父目录下的唯一 `.creating` staging 目录，验证后通过 `Directory.Move` 激活最终目录。
失败时会清理 staging，不覆盖已有目标。

## 项目级与全局状态

| 范围 | 数据 |
| --- | --- |
| 项目级 | Assets、Packages、manifest、UI 项目、美术工作区 JSON/参考图、工作流、Tilemap 参数 |
| 应用全局 | 主题、`lastProjectPath`、MCP Agent/端口、AvalonDock 布局、生成任务、参考快照、风格素材库 |
| 无项目兼容 | `%LocalAppData%\GAssetForge\Desktop\Projects\default.pfui.json` |

全局用户设置会镜像当前工作流、Tilemap 和 Assets 路径，供独立 MCP Server 跨进程读取，但正式项目的权威
数据仍位于项目 manifest 与项目目录。每个生成任务会冻结创建时的 `delivery.assetRoot`，切换或创建项目不会
把旧任务改投到新项目。

“美术工作区制作”是 ContentId 为 `art-workspace` 的独立中央文档，不属于 `WorkflowKind`，因此打开它不会
改变项目创建时选择的业务模板。主画布和共享属性面板中的对话输入绑定同一个项目级 ViewModel。Agent/MCP
达到可执行状态后，提交对话会冻结文字、输出框、可见参考图及其哈希/变换和项目 `Assets` 根；当前有效可见
选图会自动冻结为本轮整图指代，已有局部框选时同时冻结图片本地归一化范围，并派生源像素边界和旋转后的
画布多边形。软件据此创建独立的 `art-workspace` 单图持久任务并通过共享 Agent 调度器执行，不让 Agent 猜测
用户所说的“这张图”或“这块区域”。只有精确冻结目标中的 PNG 通过 Harness 校验并
完成任务后，才保留 Generated 原件并复制为受管画布预览；项目切换后的迟到结果不会导入新项目。

## 重启恢复

启动时，Shell 读取 `lastProjectPath` 并验证项目目录、manifest、UI 数据和必要子目录。验证成功后会：

1. 将 UI repository 切换到项目内 `ui-project.pfui.json`。
2. 将美术工作区 repository 切换到 `ProjectSettings\ArtWorkspace\art-workspace.v1.json`，恢复其受管图片与创作意图。
3. 恢复 manifest 中的工作流和 Tilemap 参数。
4. 将 Project 面板根锁定为项目 `Assets`，默认位置恢复到项目内有效目录。
5. 恢复中央业务主视图及全局 AvalonDock 标签布局。

manifest 或 UI 主文件损坏时会尝试各自的 `.bak`。主文件和备份均无效、目录失效或结构不完整时，不会激活
该项目，也不会删除或自动改写原目录。美术工作区主文件损坏时单独尝试其 `.bak`；两者均无效时只加载
未落盘的安全默认工作区并保留原文件，不把整个项目误判为无效。
