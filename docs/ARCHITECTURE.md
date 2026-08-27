# WPF 桌面架构

## 总体结构

```text
GAssetForge.Core
  Projects/          项目 Schema、校验、序列化与默认项目
  Workflows/         五类业务入口定义
  ArtStyles/         风格组、参考素材元数据、统一提示词样本、稳定指纹、校验与 JSON 契约
  Generation/        AI 约束包、Tilemap 规则、单图任务状态机、Run Schema 与阶段日志
  Imaging/           sRGB/HSV 颜色模型、背景/棋盘格剔除、Alpha 描边、像素对齐、几何矫正、自动调色板与原子 PNG 输出

GAssetForge.App
  ViewModels/        Shell 与 UI 编辑器状态/命令
  Views/             原生 WPF 面板和画布
  Services/          项目、布局、主题、设置、共享取色器、图像预览缓存、风格分析协议、生成 Harness、Run 工作区、日志与 Agent Runtime
  Services/AgentRuntime/
                     Codex App Server stdio JSONL、codex exec JSONL 降级与统一会话契约
  Themes/            暗亮调色板与控件资源
  MainWindow         AvalonDock 外壳及面板注册

GAssetForge.McpServer
  Transports         loopback Streamable HTTP 与兼容 stdio
  Dispatcher         共享 JSON-RPC 握手、会话状态和工具目录
  Context            读取桌面端当前设置与有效 Assets 根目录
  Generation Tools   暴露认领、模型源文件提交、兼容后处理、完成与失败回传
  Submission         软件导入模型原图、归一化、剔色、校验并原子提交最终资产
```

## 核心原则

### AvalonDock 只管理外壳

AvalonDock 的模块接口只承担面板拓扑、大小、停靠位置、隐藏状态和活动页签。图层、Prefab、画布元素及工作流数据不进入布局 XML。

稳定 `ContentId` 是布局反序列化的接口：

| ContentId | 内容 | 默认位置 |
| --- | --- | --- |
| `ui-tools` | 工具栏 | 左侧 |
| `ui-canvas` | 设计画布 / 工作流文档 | 中央 |
| `art-workspace` | 美术工作区制作 | 中央 `LayoutDocument`；跨工作流、可关闭并从视图菜单恢复 |
| `ui-properties` | 属性 | 右上 |
| `ui-layers` | 图层 | 右下 |
| `ui-project` | Project / Prefab | 画布下方 |
| `art-style-library` | 美术风格素材库 | 与 Project 共用画布下方标签停靠区 |
| `asset-generation` | 素材生成 | 与 Project 共用画布下方标签停靠区 |
| `background-removal` | 背景剔除 / 假透明棋盘格恢复 | 与 Project 共用画布下方标签停靠区 |
| `image-outline` | 图片描边工具 | 与 Project 共用画布下方标签停靠区 |
| `icon-design` | Icon 指定角度与镜头构图主视图 | 与 Project 共用画布下方标签停靠区；激活时接管共享属性栏 |
| `pixel-grid-alignment` | 像素点对齐工具 | 与 Project 共用画布下方标签停靠区 |
| `workflow-blueprint` | Agent 与工具工作流蓝图 | 与 Project 共用画布下方标签停靠区 |
| `mcp-agent-connection` | MCP Agent 连接管理 | 默认隐藏；拉起后位于宽底部区域 |

`MainWindow` 维护面板注册表。布局恢复时，`AvalonDockLayoutService` 仅根据 `ContentId` 把真实 WPF View 重新挂接到布局模型；恢复失败则重建默认布局。工具栏上下文和属性栏上下文相互独立：稳定的 `ui-properties` host 根据当前活动 dock 页装载单实例属性模块。UI、Tilemap、美术工作区、Icon、背景剔除、图片描边和像素对齐分别使用自己的 Properties View；无专用属性的页面显示稳定空状态。因此属性切换不需要创建第二个属性 ContentId，也不会改变已保存布局结构。`art-workspace` 是独立中央文档而不是 `WorkflowKind`，激活它不会改写项目 manifest 中的初始业务模板。

### 独立状态生命周期

| 状态 | 适配器 | 生命周期 |
| --- | --- | --- |
| 项目清单 | `ProjectWorkspaceService` | `<ProjectRoot>\ProjectSettings\gassetforge.project.json`；项目身份、工作流与 Tilemap 参数 |
| UI 编辑数据 | `JsonProjectRepository` | `<ProjectRoot>\ProjectSettings\ui-project.pfui.json`；自动保存、备份、导入、导出、Undo / Redo |
| 美术工作区 | `JsonArtWorkspaceRepository` | `<ProjectRoot>\ProjectSettings\ArtWorkspace\art-workspace.v1.json`；画布、视口、输出框、受管图片摆放与创作意图；参考图复制到 `<ProjectRoot>\Assets\ArtWorkspace\References` |
| 停靠布局 | `AvalonDockLayoutService` | 窗口关闭保存、启动恢复、可独立重置 |
| 用户设置 | `UserSettingsService` | 主题、最近项目、Agent/MCP 配置、Codex thread ID + 工作目录、素材生成草稿、背景/棋盘格剔除、图片描边、Icon 相机参数与像素对齐偏好 |
| 美术风格素材库 | `JsonArtStyleLibraryRepository` | 风格组、人工提示词、统一提示词样本与当前选择即时原子保存；参考图片复制到受管目录 |
| 风格提示词分析请求 | `AgentArtStylePromptAnalysisService` | Agent 工作目录下 `.gassetforge\style-analysis\<requestId>` 的一次性 request/result/diagnostics；不作为风格库真相源 |
| AI 生成任务 | `JsonGenerationTaskStore` | 本地跨进程持久化单图任务、原子状态迁移、风格快照与冻结交付计划 |
| 生成 Run | `GenerationRunWorkspaceService` | `<Assets>\Generated\GAssetForge\Runs\<batchId>`；manifest、提示、参考图、模型原图、中间结果与 timeline |
| 图像工具预览 | `ImageProcessingPreviewCache` | `%LocalAppData%\GAssetForge\Desktop\Workspace\ImageProcessingPreviews`；短期、唯一、非项目资产的 PNG 缓存 |
| 工作台日志 | `WorkbenchConsoleService` | `%LocalAppData%\GAssetForge\Desktop\Logs\workbench-*.jsonl`；Agent、Turn、工具和调度阶段的结构化会话日志 |

Shell 会在一次项目激活中协调项目清单、UI repository、Project Browser 和全局设置，但项目内容不会复制进
用户设置或 AvalonDock 布局。用户设置只保存最近项目指针，以及供无项目状态和 MCP 跨进程读取使用的状态
镜像。删除布局数据不会影响项目文件；删除用户设置会丢失最近项目自动恢复、主题、MCP 和生成页偏好，
但不会删除磁盘上的正式项目。

### 美术工作区制作

`ArtWorkspaceView` 是 ContentId 为 `art-workspace` 的中央 `LayoutDocument`，提供大尺寸二维画布、可摆放的
受管参考图片和一个明确的输出框。`ArtWorkspacePropertiesView` 承载输出框参数与对话输入；两者绑定 Shell
持有的同一个 `ArtWorkspaceViewModel`，因此关闭并从“视图”菜单恢复文档不会复制状态。活动文档上下文与
`CurrentWorkflow` 分离：切到美术工作区会切换工具栏和属性栏，但不会把它注册成新建项目模板，也不会修改
项目 manifest 的 workflow key。

项目工作区 JSON 保存画布尺寸、视口、输出框、图片变换、选择状态和对话记录。导入图片先复制到
`Assets\ArtWorkspace\References`，JSON 只记录位于受管项目根内的相对路径、哈希、媒体类型和像素尺寸；
无项目状态使用应用 Workspace 下的 `ArtWorkspaceDrafts` 作为隔离草稿。主文件和 `.bak` 独立恢复，路径会
拒绝绝对地址、目录逃逸和重解析点。导入采用可取消后台流水线，项目切换和保存不会与在途复制竞态；
工作区最多承载 128 个活动图片对象，预览按 640 px 最大边解码并使用有界版本缓存，避免合法大型素材把
WPF 进程推入不可控内存占用。属性页的画布图片列表是选择与可见性的权威入口，因此隐藏对象仍可恢复。

当前“提交创作意图”只把用户文本记录为 `captured` 状态，并保存工作区；它不会启动 Agent、调用图像模型、
写出输出框图片或伪装成已生成资产。`OutputRelativeDirectory` 目前只是未来交付目标，默认值为
`Assets/Generated/ArtWorkspace`。后续接入 AI 时必须先把提示、参考图快照、输出框几何和项目 Assets 根冻结成
可审计合同，再由 Agent/图像模型完成创作步骤；Harness 负责路径校验、中间物落盘、结果验证和原子提交。
任何可见预览或创作意图都不能绕过现有生成任务所有权与受控 `Assets` 交付边界成为 Final asset。

`ArtStyleLibraryView` 是 ContentId 为 `art-style-library` 的跨工作流共享面板。每个风格组保存稳定 ID、名称、
描述、人工 AI 风格提示词、可选统一提示词样本和多张参考图片元数据；图片导入后以稳定文件名复制到
`Assets\StyleLibrary\assets\<group-id>`。清单只保存受控相对路径，并校验扩展名、媒体类型、哈希和目录边界。
顶部“视图”菜单使用可勾选开关同步该面板的显示/隐藏状态。当前选中的风格组与素材会随清单持久化，
后续生成流程可直接把它们转换为 provider-neutral 风格约束。统一样本不会覆盖人工提示词；它保存提示词、
摘要、SHA-256 来源指纹、UTC 生成时间、分析器身份/版本、请求 ID 和分析时覆盖的素材 ID。

`ArtStylePromptFingerprint` 对规范化描述、人工提示词及按稳定 ID 排序的素材 ID / SHA-256 / 媒体类型计算带
domain separation 的稳定 SHA-256。重命名风格组不会令样本过期；修改描述、人工提示词、素材集合、素材哈希
或媒体类型会改变当前指纹，使旧样本保留但标记为 stale。分析完成时 ViewModel 会再次核对当前指纹并原子
保存；分析期间发生编辑、保存失败、取消或 Agent 错误都不会覆盖旧样本。

### 共享取色器、预览导航与单图发布边界

颜色换算位于 Core 的 `RgbColor`、`HsvColor` 与 `ColorConversion`，只描述不透明 sRGB、HSV 和规范化
`#RRGGBB`，不依赖 WPF。App 层的 `IColorPickerService` / `DesktopColorPickerService` 打开共享模态窗口：
`HsvSaturationValueSurface` 提供二维饱和度/明度选择，`HueSpectrumSlider` 提供 Hue 色条，ViewModel 负责保持
HSV、RGB 与 Hex 双向同步并保留灰色状态下最近有效 Hue。窗口同时展示原色和新色；只有确认才返回结果，取消返回
`null`，调用方不会收到临时编辑。当前描边颜色、背景剔除自定义色和棋盘格 A/B 色使用同一服务。

拾色器内部的 `IScreenEyedropperService` 是另一条输入 seam，不改变外层 `IColorPickerService` 的确认边界。
`DesktopScreenEyedropperService` 保持 `ShowDialog` 模态窗口存活，仅把其 Opacity 暂时设为零；随后由无边框 Topmost
覆盖层接管整个虚拟桌面的鼠标与键盘。虚拟桌面范围与光标位置来自 Win32 物理坐标，应用清单固定
Per-Monitor-V2 DPI awareness，因此负坐标和不同缩放屏幕不会经过 WPF DIP 反推。预览阶段通过
`IScreenColorSampler` 读取当前 `COLORREF`，确认前先让覆盖层退出桌面合成并 `DwmFlush`，再提交权威 RGB。
右键、Escape、失焦、鼠标捕获丢失或显示器拓扑变化均取消；每次 `GetDC(NULL)` 都在 `finally` 中配对
`ReleaseDC`。吸管确认只更新拾色器的新色，宿主工具仍需外层“确定”才会收到结果。

普通源图/结果预览通过 `ImagePreviewControl` 与 `ImageViewportSurface` 共享 `ImageViewportState`。滚轮缩放以
鼠标所在图片坐标为锚点，中键拖拽改变平移量，右键或双击恢复 fit-to-view；缩放范围与视口边界统一约束。
`PixelAlignmentSurface` 使用同一 viewport 状态，同时保留裁切线、透视顶点和采样网格交互。假透明棋盘格叠加由
`CheckerGridRenderer` 使用同一个缩放和平移变换绘制，避免预览移动后网格相位与底图错位。

图像工具 dock 文档只承载紧凑标题、参数编辑（像素对齐的几何/采样参数）和图像预览；文件选择、输出路径、
刷新/保存、预览缓存位置和批处理队列属于活动工具的 Properties 模块。这一职责划分让窄窗口仍保留足够预览空间，
并保证属性页始终跟随当前活动 dock 页。

`IImageProcessingPreviewCache` 把背景剔除、图片描边和像素对齐单图模式的渲染结果与正式项目输出分离。ViewModel
向 `Workspace/ImageProcessingPreviews/<tool-id>` 申请唯一 `.png` 路径，再调用现有 Core ImageSharp 服务直接
生成完整预览文件；缓存不是 `Assets` 产物，也不会自动进入项目浏览器。每次刷新使用新路径，成功后才替换当前
预览引用；失败时旧预览可继续用于比较，但不能被误认为当前结果。

预览状态与生成参数绑定：输入或任何影响像素的设置发生变化时，旧预览标记 stale 并禁用保存，输出路径变化不使
像素结果过期。保存通过目标目录内临时文件复制并原子重命名，发布的是同一缓存 PNG 的原始字节，不重新调用 Core
算法；这也保证像素工具的 Palette PNG 不会在保存阶段被二次编码。缓存服务校验受管目录并完整解码 PNG；每个
ViewModel 会 pin 当前采用或正在渲染的缓存路径，按年龄、数量和总容量执行的机会式清理会跳过这些活动预览，替换、
切换输入或主动释放时再解除保护。批量背景剔除、批量描边与批量像素对齐均由 App 队列逐项直接写入正式目录，
不经过单图人工确认。

### 像素对齐与批处理边界

`ImageSharpPixelAlignmentService` 保持单图、确定性和原子落盘：先执行裁切与几何矫正，再生成逻辑格子采样结果，
最后按可选颜色索引策略保存 PNG。单图 UI 可把该正式结果写入上述预览缓存，批量 UI 则直接规划最终路径；Core
不区分二者。自动索引只作用于最终逻辑图片，不改写源图或最近邻检查工作图；它使用无抖动
Wu 量化并输出 Palette PNG，关闭时使用 RGBA PNG。完全透明像素在建立调色板前规范化为透明黑，半透明 Alpha
作为 RGBA 索引的一部分保留。

多图编排属于 App 层而不是 Core 图像算法。`PixelAlignmentViewModel` 在开始批次时冻结第一张参考图的设置，将
裁切边界和四个透视顶点归一化为 `0..1` 坐标，再逐项映射到各自源尺寸，并串行调用同一个单图服务。每项拥有
独立 Pending / Running / Completed / Failed 状态；失败只影响当前项，已经完成的原子文件继续保留。输出规划
默认不覆盖现有文件，名称冲突使用稳定数字后缀。批量文件清单属于瞬时 UI 状态，不写入用户设置；颜色索引开关
与最大颜色数则随其它像素工具偏好持久化。

### 背景剔除与图片描边边界

`ImageSharpBackgroundRemovalService` 保留旧单色路径，并在显式启用时切换为双颜色棋盘格路径。未对齐模式对每个
像素选择距离更近的 A/B 底色；对齐模式根据正方形 cell、X/Y offset 和数学 FloorDiv 计算预期相位，只有格线
容差带内才允许回退到两色最近距离。Matte、收缩、羽化和 Alpha 上限仍共享原有实现，despill 则使用当前像素的
局部底色。单幅棋盘格合成图无法精确反演原始半透明前景，因此 UI 把它定义为显式本地兜底，而不是素材生成模型
可以主动选择的输出格式；生成合同继续冻结单一纯色底。

背景候选范围由兼容字段 `EdgeConnectedOnly` 冻结：`false` 会在全图匹配底色，因而可删除被主体轮廓包围的镂空
内部底色；`true` 只从画布四边做四邻域泛洪，可保护主体内部有意使用的同色细节。产品 UI 暴露正向的
`RemoveEnclosedBackground` 并默认开启，内部映射为 `!EdgeConnectedOnly`。用户设置同时保存新的正向字段和旧的
反向字段；旧设置缺少正向字段时迁移为开启，新任务沿用既有 MCP/JSON 字段而不升级生成 Schema，历史任务中显式
冻结的值保持原行为。

`ImageSharpImageOutlineService` 也是单图、确定性、原子落盘服务。它先按 Alpha threshold 清理极弱透明噪点，再用
可分离滑动窗口 max/min 计算方形形态学轮廓：outside 为 `dilate(A)-A`，inside 为 `A-erode(A)`，center 把总宽度
拆成外 `ceil/2` 与内 `floor/2`。外轮廓在源图后方 SourceOver，内轮廓只替换边缘颜色且保持源 Alpha；可选 canvas
expansion 防止外描边被裁切。

`ImageOutlineViewModel` 负责单图与最多 256 项的顺序批处理、碰撞安全文件名、逐项状态和进度。与像素工具一致，
批量清单属于会话瞬态状态，不进入用户设置；描边种类、宽度、颜色、Alpha threshold、扩画布开关和最近目录即时
持久化。单图刷新把同一个 Core 服务的输出写入预览缓存，确认保存时仅发布缓存；批量继续直接调用 Core 服务写入
正式目录。Core 服务不依赖 AvalonDock、项目 Schema、Agent 或 MCP。

`BackgroundRemovalViewModel` 使用同样的 App 层批处理边界：批次开始时冻结当前色键/棋盘格与边缘设置，逐项规划
不覆盖现有文件的输出路径并调用单图 Core 服务。每项独立记录 Pending / Processing / Completed / Failed；单项
失败只进入该项诊断并继续后续文件。批处理选择器允许多选输入和独立输出目录，队列与进度只属于当前桌面会话。

### Icon 指定角度投影边界

`IconPerspectiveProjectionService` 是不依赖 WPF 的确定性针孔相机投影。方块中心固定为 `(0,0,0)`、`+Y` 向上；
方位角 `0°` 将相机放在 `+Z`，正值向 `+X` 环绕，正仰角令相机升高并朝原点俯视。镜头滚转只旋转相机的
Right/Up 基向量，不修改方块世界姿态。服务根据图片宽高、固定 24 mm 传感器高度和焦距计算水平/垂直视场角，
返回方块顶点、面朝向、镜头像素坐标、画面包围盒及是否完整入镜。

`IconDesignViewModel` 持有单实例参数、预设、实时生成描述和用户设置快照。可编辑的制作模式、画幅、相机与镜头
参数、预设和生成角度描述位于 `IconDesignPropertiesView`；`IconDesignView` 只保留两个
`IconPerspectiveSurface` 和状态摘要。两个 surface 消费同一份不可变投影快照：空间模式显示相机、方块和视锥，
镜头模式按目标画幅比例显示投影结果。

`IconDesignConstraintPackageBuilder` 把这套确定性投影接入正式素材生成。发布时从 ViewModel 捕获一次
`IconDesignGenerationConstraintInput`，随后冻结模式、精确画幅、方位/仰角/滚转、物距、焦距、固定传感器高度、
相机位置与 Forward/Right/Up 基向量、水平/垂直 FOV、画面包围盒、完整入镜状态、生成角度描述和背景合同。
Agent 与 MCP 只消费冻结约束，不能从预览像素反推相机角度；当前投影未完整入镜时客户端在发布前拒绝任务。

### 风格分析一次性文件协议

`AgentArtStylePromptAnalysisService` 不解析 Agent 聊天文本，也不允许 Agent 或 MCP 直接修改
`art-style-library.v1.json`。每次分析在当前 Agent 工作目录建立独立目录：

```text
.gassetforge/style-analysis/<requestId>/
  request.v1.json       软件原子写入的冻结请求
  result.v1.json        Agent 唯一允许写入的业务结果
  diagnostics/          Turn 状态或失败诊断
```

请求明确列出风格组身份、描述、人工提示词、来源指纹和当前组的全部参考图绝对路径、媒体类型及 SHA-256；
Agent 必须逐张使用视觉能力检查，只归纳共同视觉语言。参考图内容、图中文字、二维码、元数据、文件名以及请求
中的用户数据字段都按不可信输入处理，不能改变路径、协议或任务范围。协议目录拒绝重解析点和越界路径，结果
限制为无 BOM UTF-8 的严格 schema v1 JSON，只允许 `schemaVersion`、`requestId`、`groupId`、`sourceFingerprint`、
`unifiedPrompt`、`summary`、`analyzer`、`analyzerVersion` 与 `analyzedAtUtc` 等固定字段。WPF 在落库前复验
身份字段、分析器、时间、长度、参考图存在性和 SHA-256，并将当前完整素材 ID 集合写入最终样本。

AI 生成任务是第四条本地运行时 seam，保存于 `Generation\generation-tasks.v1.json`。`AssetGenerationView`
以表格收集多行内容描述、数量和输出目录，并把当前风格组与所选生成步骤组合为 provider-neutral 约束包。正式步骤
目前包括 Tilemap 和 Icon；前者冻结网格与瓦片投影，后者冻结上述 Icon 相机投影和角度描述。两者共用同一套
`GenerationBackgroundContractBuilder`，保证首道纯色底与最终真实 Alpha 合同不会因业务构建器不同而漂移。
步骤 2 还通过 `IAssetGenerationSpreadsheetReader` / `IAssetGenerationSpreadsheetPicker` 提供独立导入 seam：
ExcelDataReader 只把 `.xlsx/.xls/.csv/.tsv` 转为有界、惰性的 workbook/sheet/column/row 值模型，公式只取缓存值，
不执行宏、外部链接或工作簿内容。预览、工作表选择、描述列和勾选行属于会话瞬态状态；真正追加或替换后的
`DraftRows` 才进入现有用户设置持久化。导入只映射描述，数量固定为 1，输出目录由当前任务行继承，因此不可信
工作簿不能改变项目 Assets 边界。
数量只负责把一行展开成若干个彼此独立的单图任务：新合同固定 `output.count=1`、`outputIndex=0`，任务间并发
交给当前 Agent Host 的默认调度，不再把页面“并行图片数”作为生成协议的一部分。页面默认开启统一提示词
优先：选中组存在新鲜样本时，任务冻结 `sourceMode=unified-prompt-only`、样本文本、来源指纹和生成时间，参考图
集合为空，也不会创建或镜像参考图快照；样本缺失、过期或用户关闭开关时，明确回退到描述、人工提示词和当前
参考图快照。交付计划始终冻结任务创建时的绝对 Assets 根、项目内运行路径和唯一最终目标路径。Assets 根作为行
输出位置时，Tilemap 与 Icon 分别交付到 `Generated/Tilemap/<batchId>/Final` 和
`Generated/Icon/<batchId>/Final`；选择 Assets 内子目录时在该目录下追加同样的业务分类。任务队列不写入 UI
项目 Schema、Undo/Redo 或 AvalonDock 布局，也不依赖 Agent 或 MCP HTTP 会话持续在线。

### 客户端 + Harness + Skill + MCP/ACP 分层

生成工作台按职责而非按进程名分为四层：

| 层 | 当前职责 | 明确不负责 |
| --- | --- | --- |
| WPF 客户端 | 收集风格、描述、数量和项目输出位置；显示任务、产物、阶段耗时与风险状态 | 不直接调用图像模型，不让 UI 状态充当任务真相源 |
| 本地 Harness | 展开单图任务、创建 Run、冻结合同、归档中间物、归一化画布、背景剔除、验证、原子提交与结构化日志 | 不生成或伪造美术内容 |
| Skill / 图像模型 | 根据冻结提示和参考素材生成一张真实模型图片 | 不建项目目录，不搬运、缩放、抠图、改 Alpha 或决定最终交付路径 |
| Agent Control + MCP | Codex thread/Turn 负责会话与调度；MCP 负责最小工具调用、认领与结果提交 | MCP 不负责会话持久化，Agent 不重复实现 Harness 的确定性步骤 |

项目内部的“MCACP”仍只是 Agent Control 与 MCP 组合的架构简称，不声明为标准协议。风格素材库已经通过
一次性分析协议把稳定提示词持久化在客户端；新鲜样本模式会收窄 Skill 输入并省去重复参考图分析，而不改变
任务队列、Run manifest 与最终提交合同。更多确定性步骤仍可按同一原则继续下沉为客户端工具。

### Agent Runtime、MCP 与进程边界

`视图 → Agent 连接` 拉起 ContentId 为 `mcp-agent-connection` 的单实例
`AgentConnectionView`。它与属性、图层和项目区域使用同一 AvalonDock 生命周期，可隐藏、浮动、重新停靠
并随布局 XML 持久化；重复执行菜单命令只会恢复并聚焦同一面板。隐藏面板不会中断会话，主窗口退出时按
Agent Runtime → MCP Server 的顺序回收桌面端启动的子进程。

```text
AgentConnectionView / LayoutAnchorable(mcp-agent-connection)
  ↕ binding / command
AgentConnectionViewModel
  ├─ Codex profile；OpenCode Adapter 明确标记待接入
  ├─ 认证、Runtime、MCP、thread、Turn 与日志状态
  └─ 组合启动/停止与 Turn 调度
          ├──────────────────────────────────────┐
          ↓                                      ↓
McpServerProcessService                 CodexAgentRuntimeService
  ├─ 启动发行 EXE / 开发 DLL               ├─ 首选 CodexAppServerTransport
  ├─ loopback 端口与 /health               │    └─ codex app-server --stdio
  ├─ 256 bit 临时 Bearer Token              ├─ 降级 CodexExecTransport
  └─ Endpoint + Token 环境变量              │    └─ codex exec --json
          ↓                                 └─ thread / Turn / 认证状态
GAssetForge.McpServer                         ↓
  └─ POST/DELETE /mcp + GET /health          本机 Codex CLI
          ↑                                      │
          └────────── MCP tools + Bearer ─────────┘
```

启动组合操作先让 `McpServerProcessService` 进入健康状态，再调用
`CodexAgentRuntimeService.EnsureSessionAsync`。受管工作目录取正式项目根；没有正式项目时使用 Project 面板
根目录。App Server 通道通过 stdio JSONL 执行 `initialize` / `initialized`、`account/read`、
`thread/start` / `thread/resume`、`turn/start` / `turn/interrupt`，并等待
`mcpServer/startupStatus/updated` 将 `gasset_forge` 标记为 ready。认证 Required 是可恢复的账号状态：保留
App Server 供用户完成 ChatGPT device-code 登录，不把它错误降级为 exec。

App Server 不兼容、协议异常、启动超时或进程故障时，统一 Runtime 明确切换到 `ExecFallback`，按 Turn 启动
`codex exec --json` 或 `codex exec resume --json`。降级通道可从 JSONL 事件更新 thread ID，但没有常驻进程、
账号管理和 App Server 的完整 MCP startup 通知。用户取消不触发降级；Turn 使用串行 gate，避免同一 thread
并发执行。

Codex thread ID 必须与规范化工作目录成对保存。Shell 仅在当前工作目录与保存值一致时请求
`thread/resume`；新建或切换项目清空旧 thread，失效 thread 则由 Runtime 创建新 thread。thread 是 Agent
上下文 checkpoint，不是本地生成任务的状态容器。

MCP Endpoint 通过临时 Codex `-c` 配置注入，配置只引用 `bearer_token_env_var`。WPF 启动时生成的 256 bit
Base64Url Token 只存在于 MCP 与 Codex 子进程环境，且所有 Runtime / 进程日志均执行脱敏；Token 不落盘、
不进入项目文件、静态配置或 `/health`。HTTP 仍采用 `Mcp-Session-Id` 隔离，90 秒 TTL 只影响临时工具连接，
不会删除 Codex thread 或本地 `Pending` 任务。stdio MCP 复用同一 dispatcher，但不引入 HTTP Token、
Session Header 或 Agent 类型限制。

MCP 负责工具调用和授权边界，不负责 Agent 持久会话。项目内部的“MCACP”只是对 Agent Control + MCP
组合的架构简称，不作为标准协议、产品兼容性或外部配置格式宣传。OpenCode 当前只有不可启动的 profile 与
Adapter seam；在实现并验证真实 Adapter 前不能呈现为可用连接。

`get_current_context` 把“当前项目工作流”和“当前素材生成步骤”分离。`activeGenerationStep` 是生成约束选择的
权威字段，`iconDesign` 提供规范化后的 Icon 画幅与相机输入；Core 根据步骤返回对应的
`generationConstraints` / `generationMetrics`。`activeWorkflow` 与 `tilemap` 继续保留以兼容旧 MCP 客户端，
但新调用方不得再用 `activeWorkflow` 推断 Icon，因为 Icon 是共享工具页和生成步骤，不是项目模板。

实现以官方 OpenAI 文档为基线：

- [Codex App Server](https://developers.openai.com/codex/app-server/)
- [Codex CLI reference](https://developers.openai.com/codex/cli/reference/)
- [Codex MCP guide](https://developers.openai.com/codex/mcp/)

### Agent 与 AI 生成边界

本地队列负责排队与状态持久化，MCP Server 只把工作台状态、约束、任务操作和结果校验暴露为 tools；两者
都不直接假装拥有图像模型。批次成功落盘后，`AssetGenerationViewModel.BatchPublished` 把稳定任务 ID 交给
Shell。每个批次先显式创建独立 Codex thread，再由 `GenerationBatchDispatchCoordinator` 按最多五个任务切成
有序 Turn 分块；同一批次分块共享 thread 且串行执行，任一分块失败或中断后不启动后续分块。分发失败只记录
dispatch 状态，任务继续保持 `Pending`，因此 Runtime 故障不会吞掉本地待办或同时污染整批剩余任务。

批量表格单行允许 1–16 张、最多 256 行、整批最多 256 张；发布时每个数量变体都创建一个独立单图任务，
而不是让一条任务内部管理多张图片和自定义并行槽位。某一图片失败不阻塞同批其它任务。桌面端默认开启
自动重做并允许初次生成后再尝试 3 次，即每条谱系最多 4 次尝试；用户可以关闭，或把重做次数设置为
0–10。该上限在首次任务创建时冻结为 `RetryLineage.MaxAttempts`，不会被后续设置变化改写。新任务的 Agent
执行链固定为：

```text
claim_generation_task
  → 使用 preferredSkill 调用真实内置 ImageGen
  → savedPath 有值：submit_generation_output(taskId, agentId, outputIndex=0, sourcePath)
  → savedPath=null：run_image_delivery_fallback(taskId, agentId)
       → Harness 调官方 image_gen.py 直写 recommendedSourcePath 并自动 submit
  → 校验通过：软件原子提交最终文件并自动完成任务登记
  → 可恢复几何未通过：保留中间文件并返回 warning outcome，桌面端创建下一次尝试
```

Agent 不再自行创建目录、扫描目标、搬运文件、调整尺寸、补底色、抠图、修改 Alpha，或重复调用旧
`process_generation_output` / `complete_generation_task`。这些确定性步骤由
`GeneratedOutputSubmissionService` 和本地后处理器执行。只有没有 `delivery.workspace` 的历史任务继续使用
旧多图/staging 合同。

每次发布先在当前项目 Assets 内创建以下 Run；中间文件保留供人工检查和性能分析：

```text
Assets/Generated/GAssetForge/Runs/<batchId>/
  00-manifest/run.v1.json
  10-style-reference/
  20-prompts/
  30-model-output/<itemId>/*.model.png
  35-normalized-input/<itemId>/*.normalized.png
  40-background-removed/<itemId>/*.transparent.png
  50-final/
  90-diagnostics/stages.v1.jsonl
```

`00-manifest/run.v1.json` 将每个 `itemId`、`taskId`、表格行、变体序号、模型原图、归一化图、剔色图和最终
目标关联起来。`50-final` 是 Run 内预建的最终阶段分类 seam；权威交付文件仍按用户选定位置写入
`<输出目录>/Tilemap/<batchId>/Final/*.png` 或 `<输出目录>/Icon/<batchId>/Final/*.png`，并由任务冻结的
`delivery.targetPaths` 决定，避免回落到 Codex 默认 `generated_images` 或用户后来切换的项目。

“生成产物”并不等待整个批次结束才刷新。轮询会同时检查 `30-model-output`、`35-normalized-input`、
`40-background-removed` 和正式 `targetPaths`；任一文件真实落盘后就立即创建对应的“模型原图 / 画布归一化 /
背景处理 / 最终产物”卡片。卡片按处理中、成功、可重做警告、人工介入失败使用不同视觉状态，并显示尝试
编号与失败摘要。只有正式 `targetPaths` 计入完成进度，中间文件只用于过程可见性和诊断。

`GenerationRunJournal` 以 append-only JSONL 记录 `runId`、`batchId`、`taskId`、`outputIndex`、阶段、状态、
起止时间、`elapsedMilliseconds`、Agent、期望/观察路径和错误码。当前稳定阶段包括 `workspace.prepare`、
`style.snapshot.project_copy`、`task.claim`、`model_output.import`、`model_output.normalize`、
`output.process_and_commit`、`output.geometry_validation`、`task.complete` 与 `task.fail`。可恢复几何失败会写入
`output.geometry_validation/retryable_warning`，并附带结构化几何诊断。同时，Workbench 会话日志记录调度
阶段和 Agent 工具活动；控制台命令 `generation timeline` 可直接查看当前阶段、当前耗时、最长阶段和图片
落盘/验证数量。

可恢复失败以“尝试谱系”管理，而不是复用或重开旧 task ID。当前 Agent Turn 收束后，桌面端只选择
`FailureRetryable=true`、尚未被接替且仍有额度的 `Failed` 尝试，原子创建新的 task、batch、Run、manifest、
提示和唯一交付路径；父任务同时写入 `SupersededByTaskId`，子任务保存 `RootTaskId`、`PreviousTaskId`、
`AttemptNumber` 与冻结的 `MaxAttempts`。同一父任务的并发重做请求是幂等的，不会产生两个后继任务。旧 Run
和全部中间文件不被覆盖，后继提示会带入上一轮的具体几何诊断进行定向修正。没有 `RetryLineage` 的历史任务
不会在升级后被意外自动重做；`agent_transport_*` 与历史 `agent_turn_incomplete` 即使曾错误带有
`FailureRetryable=true` 也被排除。只有 MCP/Harness 明确落盘的可恢复几何或临时交付失败能创建 child；额度
耗尽、关闭自动重做或非可恢复错误则保留产物并转为人工处理入口。

每个新任务还冻结可选 `DispatchIntent`。只有明确记录 `Requested=true` 的 Pending 批次才允许在应用重启、
项目切换或 Agent 再次 Ready 后恢复自动派发；旧任务和“仅发布本地队列/人工接力”任务不会被升级后意外拉起。
恢复器复用原 task ID 与冻结交付路径，补齐缺失的 Run 分类目录、prompt 和 manifest，再按 batch ID 幂等入队；
入队返回 `Enqueued`、`AlreadyScheduled` 或 `QueueStopped`，只有真实新入队才记录一次恢复日志；恢复扫描仅在
Agent 连接 false→true 时触发，不响应每秒健康轮询或普通 thread 书签变化。同一进程中的正常发布与恢复扫描即使
并发，也只会启动一个调度生命周期。若自动重做在 child 提交前因路径、权限
或 Run 准备失败，父任务会持久化 `AutomaticRetryBlockedReason`，界面切换为“自动重做受阻，可人工接管”，
不再占用人工重试入口。若 child 已经原子入库而 manifest/通知随后失败，child 仍保持可见并可由恢复器继续派发。

Agent Turn 收束器不再用桌面端调度代理 Claim/Fail 未被领取的 Pending task。`Completed` Turn、启动异常或
Responses/SSE 在 `response.completed` 前中断，都只能汇总任务库中已经存在的 Pending/Claimed/Completed/Failed
真相。流中断稳定分类为 `agent_transport_stream_disconnected`，UI 显示 `Interrupted`，Workbench 记录分块
序号/总数、当前任务数、Turn 耗时、`response.completed=否` 和安全错误摘要；原 task ID 等待重新连接后续派。

完成校验使用任务创建时的 `delivery.assetRoot`，不读取用户后来切换的新 Project 根；Icon 与 Tilemap 都按冻结
画幅、真实 Alpha 和背景合同进入同一确定性交付链。Tilemap PNG 还会真实
解压 IDAT、校验精确尺寸，并要求瓦片有效轮廓外 Alpha 为 0。Core 同时冻结 A/B 两条边的绝对屏幕角度；
90°/0° 被明确解释为画布轴对齐正方形，Agent 不得二次概括为 diamond/rhombus，校验器也会拒绝实际呈
上下尖角、中心最宽且四个角部缺失的内接 45° 钻石形。任务认领使用 30 分钟租约，异常退出的 Agent 不会
让任务永久停留在 `Claimed`。这样桌面端持久化、Agent 编排、MCP 工具和具体图像供应商保持解耦。

90°/0° 的前景主体几何校验使用统一外层错误码 `output_geometry_validation_failed`，并按实际原因写入子错误：

- `tilemap_geometry_aspect_ratio_mismatch`：主体外接包围盒宽高比超出允许偏差。
- `tilemap_geometry_rotation_mismatch`：主体边缘方向没有与画布水平/垂直轴对齐。
- `tilemap_geometry_diamond_detected`：轮廓确实呈上下尖角、中心最宽的内接菱形。

缺少角部覆盖本身不再等价于“检测到 45° 菱形”。诊断同时保存 `foregroundBoundingBox`、`actualWidth`、
`actualHeight`、`actualAspectRatio`、`expectedAspectRatio`、`allowedDeviationPercent`、四角
`cornerOccupancy`、`rotationAngleEstimate`、`diamondScore` 与实际校验文件路径。几何问题发生时，
`submit_generation_output` 将本次尝试持久化为可恢复失败并正常返回
`outcome=retryable_validation_failure`；这表示图片生成、导入和中间处理已完成，不是
`image_delivery_fallback_failed`，Agent 不应再次调用 `fail_generation_task`。

产物校验对 Tilemap 的参数、派生 metrics、轴向量和背景合同执行 fail-closed；瓦片有效轮廓内必须存在可见像素。
PNG 解析同时限制文件、解压像素、chunk、IDAT 与非图像数据预算，避免畸形任务或压缩文件占满内存与 CPU。

第一版 Tilemap 约束构建器统一输出画布尺寸、菱形角、旋转、投影、像素密度、结构化轴向量与真实
Alpha 透明规则。桌面属性面板和 MCP 共用同一 Core 构建器，避免两份提示词数学发生漂移。
旋转新契约以屏幕 A/X 轴向右为 `0°`、正值顺时针；Validator 仍识别旧版菱形对称轴零角契约，保证
已持久化任务可以继续完成。

风格参考图在任务创建时异步流式复制并增量计算 SHA-256；Core、风格库和快照服务共享最多 128 张、
单图 512 MB、总量 2 GB 的边界，避免大风格组阻塞 UI 或在不同层产生不一致结果。

HTTP 传输只允许绑定和访问 loopback 地址，请求体限制为 4 MiB，并拒绝非 JSON 的 MCP POST。
WPF 启动时生成的 256 bit Bearer Token 不落盘、不写日志；`/health` 无需 Token 以供桌面轮询，但仍严格限制
loopback 且不返回认证信息。该端口仍不得经代理或端口转发发布到局域网或公网。

### 正式项目工作区

`文件 → 新建项目…`（`Ctrl+N`）是业务模板的正式入口，顶部不再保留“业务模板”菜单。新建页收集项目
名称、已存在的父目录和工作流；选择模板只更新待创建配置，创建成功后才切换中央业务主视图。

`ProjectWorkspaceService` 先在所选父目录创建唯一 staging 目录，写入并验证完整项目，再通过同父目录
`Directory.Move` 激活最终目录。项目名必须符合 Windows 文件名规则；目标必须是所选父目录的直接子目录；
磁盘根目录、已存在目标、Windows 保留设备名和 reparse point 会被拒绝，失败时清理 staging 目录。

正式项目激活后，`JsonProjectRepository` 切换到项目内 UI 文件，Project 面板固定以
`<ProjectRoot>\Assets` 为根并默认选中 `Assets\Generated`，且禁止从 Project 面板跳出项目资产边界。
`user-settings.json` 保存 `lastProjectPath`。启动时若项目目录、manifest、UI 数据和必要子目录通过验证，Shell
会恢复业务主视图、项目级 Tilemap 参数、UI repository、美术工作区 repository 与 Project 面板；路径失效或主文件和备份均无效时
不会激活该项目，也不会删除原目录。更完整的目录与恢复契约见 [项目工作区说明](PROJECTS.md)。

### 业务工作流

`WorkflowCatalog` 是模板注册模块。业务工作流在新建项目页中选择；创建前不改变当前工作区，创建成功后把
模板 key 写入项目 manifest，并由 `ShellViewModel` 将中央文档切换为 `EditorWorkspaceViewModel`、
`TilemapWorkspaceViewModel` 或 `WorkflowPlaceholderViewModel`。未实现的业务仍使用相同基础业务面板位置；
美术工作区制作不属于 `WorkflowKind`，与美术风格素材库、蓝图窗口和 MCP 连接面板一样作为跨工作流共享模块独立复用。蓝图窗口提供系统起始、说明、
Harness 工具、功能集合与 If 逻辑节点，支持端点和有向无环连线、画布平移缩放、节点整理及项目级 JSON
持久化。图文件显式版本化、限制为 8 MiB，并拒绝越界路径与重解析点。开始执行时先冻结带类型化路由的版本化计划并独立复验，
再把计划和审计时间线落入项目 `Assets/Generated/GAssetForge/BlueprintRuns`；`run-state.v1.json` 最后写入并作为 planned 提交标记，
在真实 Adapter 接入前不会误触发 Agent 或图片处理。详细合同见 [蓝图 Harness 协议](blueprint-harness-protocol.md)。

蓝图编辑器把持久图与交互瞬态分离：节点放置虚影和端口牵引线不会写入 Graph，第二次左键确认后才创建节点；输入、输出端点均可作为牵线起点，最终统一规范为 Output → Input。默认鼠标只负责选择、滚轮缩放及右/中键平移，右键双击恢复 `(0,0,1)` 视口；对象移动工具会拦截节点整卡输入并拖动对象。添加按钮与画布空白区共享四类型右键工具组，常驻工具栏不再占用独立类型下拉框。
目前尚无手动“打开已有项目”入口，已有项目只通过 `lastProjectPath` 在重启时自动恢复。

Tilemap 第一阶段由 `TilemapWorkspaceView` 和 `TilemapGridSurface` 组成。网格使用单个 WPF `FrameworkElement.OnRender` 绘制可见范围内的主/次网格、XY 轴与悬停格，不创建海量 `Line` 控件；当前提供中键平移、光标锚点缩放、格坐标换算以及暗亮主题资源。瓦片数据、笔刷和图层模型将在此渲染 seam 上继续扩展。

### UI 编辑器

`EditorWorkspaceViewModel` 当前集中处理第一阶段编辑事务：

- 选择、移动和文字创建
- 图层与文件夹变更
- 图层树归组、同级排序与循环引用防护
- Prefab 实例化及 ID 重映射
- 属性修改
- 历史快照和自动保存

View 只负责 WPF 输入适配与渲染。连续拖动通过 `BeginMove` / `PreviewMove` / `EndMove` 形成一次历史事务，避免每个鼠标像素产生一条 Undo 记录。

文件夹的 `Visible`、`Locked` 与 `Opacity` 会递归计算为子图层的 `EffectiveVisibility`、`EffectiveLocked` 和 `EffectiveOpacity`。画布移动与属性修改使用有效锁定状态，而不是只检查当前节点自身值。

Shell 的 UI 编辑命令通过 `GatedCommand` 接到稳定 seam：非 UI 工作流或业务选择层打开时，保存、导入导出、撤销重做、删除和缩放都无法穿透到隐藏的 UI 项目。

## 验证

`GAssetForge.SmokeTests` 是无第三方测试框架的 STA WPF 验证程序，使用隔离临时目录。发布前的覆盖
清单包括：

- 正式业务文档、独立 `art-workspace` 文档及默认隐藏 MCP 面板的拓扑与稳定 ContentId
- AvalonDock 保存、恢复、内容重绑和备份恢复
- 暗亮主题与非法设置回退
- 五业务入口、Tilemap 独立网格工作区及 UI 命令门控
- 文件菜单的新建项目入口、旧业务模板入口移除及选择模板不提前切换工作区
- Unity 式目录创建、非法名称、重复目录、staging 清理和 Project Browser Assets 边界锁定
- 项目内 UI repository 与全局兼容工作区隔离，以及工作流、Tilemap 参数和最近项目的重启恢复
- 美术工作区项目 JSON、参考图受管复制、主备恢复、路径边界、活动文档属性切换与创作意图持久化
- 图层、文件夹、归组、排序、锁定、透明度、文字与 Prefab
- Undo / Redo、项目保存重载和轻量会话退出保存
- MCP 菜单与单实例停靠面板、隐藏/浮动/恢复、Codex profile，以及 OpenCode 不可用状态不被伪装
- HTTP 子进程启动停止、initialize / initialized / tools/list 握手、健康状态和日志
- Bearer Token 注入、401 拒绝、日志脱敏与 `/health` 不泄密
- fake process 下的 App Server initialize、账号读取、thread start/resume、MCP ready、Turn 完成与异常退出
- `codex exec --json` 降级、thread ID + 工作目录恢复，以及切换项目不误用旧 thread
- 数量展开为一图一任务、页面并行数不进入 Agent prompt、5/5/2 Turn 分块、批次独立 thread、断流仍保留 `Pending`
- Responses 中断分类、基础设施失败不创建 retry child、恢复入队幂等结果与连接上升沿日志去重
- 风格库统一提示词样本持久化、稳定指纹过期判断、一次性 Agent request/result 协议与严格结果校验
- 生成页统一提示词优先、`unified-prompt-only` 冻结合同、跳过参考图快照及缺失/过期回退旧链路
- 项目内 Run 目录、manifest、提示与参考图镜像、中间图保留和结构化阶段 timeline
- `claim → ImageGen → submit_generation_output`、源图归档、尺寸归一化、底色补齐、背景剔除、最终校验与自动完成
- `submit_generation_output` 的幂等、认领归属、路径边界与单图合同，以及旧多图 staging/process/complete 兼容
- Workbench Agent 活动持久化、阶段耗时、最长步骤与 `generation timeline` 控制台输出
- 原有 stdio MCP 工具目录与历史任务流程兼容性

这些自动测试不得启动真实模型 Turn 或产生图像生成费用；图像产物验证使用隔离测试文件，只验证合同与
状态机。

## 项目 Schema

正式项目包含彼此独立的版本化契约：

- Project manifest Schema v1：项目 ID、名称、工作流、创建/更新时间及项目级 Tilemap 参数。
- UI Project Schema v2：documents、layers、Prefab library、editor session 和预留 `workflowGraphs`。
- Art Workspace Schema v1：画布与视口、输出框、受管图片素材及其摆放、选择状态和创作意图对话。

manifest、UI 与 Art Workspace 文件分别回退读取自己的 `.bak`，不能用其中一个文件代替另一个。UI Project Schema v2
当前包含：

- `documentsById` / `activeDocumentId`
- Canvas、`rootIds`、归一化 `layersById`
- component / text / group 图层
- 资源文件夹与 Prefab payload
- 预留 `workflowGraphs`

图层顺序保持“最上层在前”；WPF 渲染前转换成 bottom-first，并为每个可绘制图层设置 `ZIndex`。

## 后续可扩展 seam

- `workflowGraphs`：ComfyUI 式节点图、Node Registry、执行队列
- 统一提示词样本的人工审核/锁定、历史版本对比与显式回滚
- 可插拔图像模型 Skill Adapter，同时保持单图 sourcePath 提交合同不变
- Run `50-final` 镜像、清理/归档策略和按阶段重放工具
- Photoshop MCP：PSD 打开、图层同步、后处理
- OpenCode Agent Runtime / ACP Adapter（完成前保持不可启动）
- 远程部署所需的独立认证与细粒度权限（仅在出现真实远程需求后评估；当前 loopback Bearer 不适用公网）
- Prefab 关联实例：Apply / Revert / Variant / Nested Prefab
- 多文档、浮动窗口和多显示器布局

只有出现第二个真实适配器时再抽象新的公共接口，避免为尚未变化的实现提前制造浅模块。
