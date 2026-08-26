using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;
using GAssetForge.App;
using GAssetForge.App.Controls;
using GAssetForge.App.Converters;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.ArtStyles;
using GAssetForge.Core.Blueprints;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Projects;
using GAssetForge.Core.Workflows;
using GAssetForge.McpServer;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopApplication = GAssetForge.App.App;

namespace GAssetForge.SmokeTests;

internal static class Program
{
    private static string _temporaryRoot = string.Empty;

    [STAThread]
    private static int Main()
    {
        _temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            Guid.NewGuid().ToString("N"));

        DesktopApplication? application = null;
        try
        {
            application = new DesktopApplication();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            TilemapZeroRotationUsesCartesianScreenAxes();
            Console.WriteLine("PASS  Tilemap 0° 使用水平/垂直屏幕坐标基准，45° 再整体顺时针旋转");
            TilemapGeometryDiagnosticsSmokeTests.Run(_temporaryRoot);
            Console.WriteLine("PASS  Tilemap 几何校验区分长方形、旋转方形与真实菱形并输出结构化诊断");
            IconPerspectiveProjectionSmokeTests.Run();
            Console.WriteLine("PASS  Icon 指定角度投影遵循稳定世界坐标、镜头滚转与构图缩放规则");
            IconDesignGenerationConstraintSmokeTests.Run();
            Console.WriteLine("PASS  Icon 生成步骤冻结画幅、相机、镜头、投影与背景处理合同");
            ProductIdentityCompatibilitySmokeTests.Run();
            Console.WriteLine("PASS  GAssetForge 新标识与旧数据、项目、MCP 和调度身份兼容");
            ReleaseVersionConsistencySmokeTests.RunAsync(_temporaryRoot).GetAwaiter().GetResult();
            Console.WriteLine("PASS  server_info、MCP initialize 与 EXE ProductVersion 使用同一正式程序集版本");
            DefaultDockLayoutHasStableTopology();
            Console.WriteLine("PASS  默认停靠布局包含可中央标签化的工具页、可关闭主视图及顶部标签拓扑");
            IconDesignSettingsLoadAndPersistImmediately();
            Console.WriteLine("PASS  Icon 图标制作相机与画幅参数可从用户设置加载并即时持久化");
            DockPanelsFloatHideAndRestoreFromViewMenu();
            Console.WriteLine("PASS  工具页可停靠中央，主视图可关闭并从视图菜单重新创建聚焦");
            BlueprintDockPanelProvidesWorkflowCanvas();
            Console.WriteLine("PASS  蓝图窗口可停靠、从视图菜单切换并提供 Agent 工作流画布");
            BlueprintWorkflowSmokeTests.RunAsync(_temporaryRoot).GetAwaiter().GetResult();
            Console.WriteLine("PASS  蓝图四类节点、项目隔离、连线校验、稳定 Harness 计划与运行落盘可用");
            BlueprintRegressionSmokeTests.RunAsync(_temporaryRoot).GetAwaiter().GetResult();
            Console.WriteLine("PASS  蓝图激活事务、端点规则、顺序、边界与 JSONL 回归约束稳定");
            DockLayoutPersistsFloatingAndHiddenPanels();
            Console.WriteLine("PASS  浮动与隐藏面板状态可随 AvalonDock 布局持久化恢复");
            ThemeSwitchUpdatesWpfAndAvalonDockResources();
            Console.WriteLine("PASS  暗亮主题同步切换 WPF 与 AvalonDock 资源");
            BusinessWorkspaceContextsSwitchAndGateUiCommands();
            Console.WriteLine("PASS  四个正式业务窗口可共存且活动标签正确切换侧栏上下文");
            NewProjectFlowCreatesIsolatedUnityStyleWorkspace();
            Console.WriteLine("PASS  新建项目可创建独立目录并只聚焦初始业务窗口，不替换共享项目环境");
            ProjectWorkspaceRecoversBackupsAndKeepsLockedAssetBoundary();
            Console.WriteLine("PASS  正式项目可从损坏主文件恢复并在 Assets 丢失时保持项目边界");
            TilemapWorkflowShowsParameterizedDiamondGrid();
            Console.WriteLine("PASS  Tilemap 参数面板驱动可缩放、平移与旋转的菱形网格及 AI 约束");
            ProjectFileSystemBrowserPersistsAndStaysWithinRoot();
            Console.WriteLine("PASS  Project 文件系统浏览、空白区右键菜单、根目录约束与持久化可用");
            ArtStyleLibraryDockPanelImportsPersistsAndRemovesAssets();
            Console.WriteLine("PASS  美术风格素材库可停靠、分组、多图导入、统一提示词入口与本地持久化可用");
            ArtStylePromptSampleSmokeTests.Run();
            Console.WriteLine("PASS  美术风格统一提示词样本可稳定指纹、兼容旧库、原子保存并正确过期");
            ArtStylePromptAnalysisServiceSmokeTests.RunAllAsync(_temporaryRoot)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS  美术风格分析经受限文件协议校验素材、Agent 结果与诊断身份");
            UnifiedStylePromptGenerationSmokeTests.Run();
            Console.WriteLine("PASS  素材生成优先复用新鲜统一提示词并在缺失、过期或关闭时回退参考图流程");
            AssetGenerationDockPanelBindsWorkflowAndTracksMenuState();
            Console.WriteLine("PASS  素材生成停靠页、顶部入口、视图开关与生成工作流绑定可用");
            AssetGenerationSpreadsheetImportSmokeTests.Run();
            Console.WriteLine("PASS  Excel/CSV/TSV 描述读取具备格式边界、缓存公式与恶意容器预检");
            AssetGenerationSpreadsheetWorkflowSmokeTests.Run(_temporaryRoot);
            Console.WriteLine("PASS  Excel 描述预览、列匹配、行选择与任务表追加/替换工作流可用");
            BatchGenerationCoreSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  批次任务原子创建、数量边界、稳定顺序与旧任务兼容");
            GenerationClaimSafetySmokeTests.RunAllAsync(_temporaryRoot)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS  MCP claim 在新旧合同和风格引用路径上保持认领前安全校验");
            ImageDeliveryPreflightSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  图片本地交付预检区分内置可用、CLI 兜底降级与输出目录阻断");
            ImageGenerationCliFallbackSmokeTests.RunAllAsync(_temporaryRoot).GetAwaiter().GetResult();
            Console.WriteLine("PASS  MCP Harness 使用官方 image_gen.py 将模型原图确定性写入项目路径");
            BackgroundRemovalSmokeTests.Run();
            Console.WriteLine("PASS  背景剔除纯色与假透明棋盘格、连通/全局模式、原有 Alpha 与原子 PNG 输出可用");
            ImageOutlineSmokeTests.Run();
            Console.WriteLine("PASS  图片描边外/内/中心模式、Alpha 轮廓、扩展画布与原子 PNG 输出可用");
            ImageProcessingUiSmokeTests.Run(_temporaryRoot);
            Console.WriteLine("PASS  PS 风格 RGB/HSV 取色状态与图像处理预览缓存字节级发布可用");
            ImageToolIntegrationSmokeTests.Run(_temporaryRoot);
            Console.WriteLine("PASS  图片描边批处理、设置持久化与假透明网格工具集成可用");
            PixelAlignmentSmokeTests.Run();
            Console.WriteLine("PASS  像素对齐最小兼容倍率、最近邻放大、裁切标尺与三种采样输出可用");
            BackgroundRemovalGenerationContractSmokeTests.Run();
            Console.WriteLine("PASS  生成约束注入纯色底图并为每张产物分配背景剔除暂存交付路径");
            AssetGenerationBatchSmokeTests.Run();
            Console.WriteLine("PASS  素材生成可离线批量发布、追溯认领者并即时展示进行中落盘产物");
            IconAssetGenerationWorkflowSmokeTests.Run();
            Console.WriteLine("PASS  素材生成可选择 Icon 步骤并冻结相机画幅、独立目录与不可变任务合同");
            AssetGenerationProjectIsolationSmokeTests.Run();
            Console.WriteLine("PASS  素材生成保留本次会话在途批次，并在重载后按项目 Assets 根目录恢复");
            GenerationReferenceSnapshotsFailFastAndStreamHashes();
            Console.WriteLine("PASS  生成参考快照按共享上限零复制拒绝并以异步流式复制计算 SHA-256");
            AssetGenerationRunsImmutableMcpDeliveryAndRejectsInvalidRasterOutputs();
            Console.WriteLine("PASS  素材生成经真实 MCP 握手完成风格快照、固定交付与透明 PNG 校验闭环");
            McpAgentConnectionDockPanelBindsProfilesAndPersistsSettings();
            Console.WriteLine("PASS  MCP Agent 接入已合并到视图菜单且停靠、配置与持久化可用");
            McpServerProcessServiceRunsHttpHandshakeAndStopsCleanly();
            Console.WriteLine("PASS  桌面 MCP 服务可启动真实 HTTP 子进程、握手、列出工具并可靠停止");
            McpBearerAuthenticationSmokeTests.Run();
            Console.WriteLine("PASS  HTTP MCP 使用临时 Bearer Token 认证且 health 不泄密");
            AgentConnectionLifecycleSmokeTests.RunAll(_temporaryRoot);
            Console.WriteLine("PASS  MCP Server 可独立启停，Runtime 失败保留健康服务并可复用重试");
            AgentRuntimeTransportSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  Codex App Server/Exec Agent Runtime 的协议、降级、取消与 Token 隔离可用");
            AgentActivityParserSmokeTests.Run();
            Console.WriteLine("PASS  App Server/Exec 实时事件可规范化为有界且不暴露命令与推理内容的 Agent 活动");
            GenerationAgentHealthSmokeTests.Run();
            Console.WriteLine("PASS  Agent 断连、慢反馈、长时间静默、产物心跳与租约风险可确定性判定");
            GenerationBatchDispatchCoordinatorSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  素材批次通过单消费者队列串行派发 Agent Turn，失败不触发隐式重试");
            GenerationBatchTurnReconcilerSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  Agent Turn 结束后仅按持久化真相收束，未认领与断流任务保持 Pending");
            CodexManualFallbackPromptSmokeTests.Run();
            Console.WriteLine("PASS  Codex 人工兜底提示自包含连接与批次闭环且不泄露令牌");
            DirectToolInvocationSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  既有 Codex 会话可通过受限本地任务桥接认领和回写任务且无需 Bearer Token");
            BackgroundRemovalMcpSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  MCP 本地背景剔除按冻结索引处理 staging、兜底完成并幂等提交最终透明 PNG");
            GeneratedOutputSubmissionSmokeTests.RunAllAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  submit_generation_output 保留 raw/normalized/processed/target 审计链并自动幂等完成单图任务");
            AgentSessionBookmarkSmokeTests.Run();
            Console.WriteLine("PASS  Codex thread 仅在相同项目工作目录恢复，项目切换会清除旧书签");
            CommandConsoleSmokeTests.Run();
            Console.WriteLine("PASS  指令日志结构化持久化、线程回填、白名单命令与正式控制台绑定可用");
            McpServerNegotiatesAndRunsGenerationTaskLifecycle();
            Console.WriteLine("PASS  MCP Server stdio 可握手、读取 Tilemap 约束并执行生成任务生命周期");
            LeaseRegressionTests.GenerationClaimsExpireAndReturnToPendingAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS  生成任务认领租约过期后可回收且旧 Agent 无法直接完成");
            LeaseRegressionTests.GenerationClaimHeartbeatsExtendOnlyTheActiveOwnerLeaseAsync()
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS  生成任务心跳仅为当前所有者续租并拒绝迟到或越权续租");
            LeaseRegressionTests.ActiveGenerationTurnRenewsClaimsAndStopsAfterTurnAsync()
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS  活动素材生成 Turn 自动续租，Turn 结束后遗留任务仍可按期回收");
            LeaseRegressionTests.HttpSessionsExpireAfterInactivityDeterministically();
            Console.WriteLine("PASS  HTTP MCP 会话按最近活动时间过期并回到等待握手状态");
            EditorCreatesLayersTextAndPrefabsWithUndoRedo();
            Console.WriteLine("PASS  图层、文件夹、文字、Prefab 与 Undo/Redo 可用");
            LayerReorderingAndGroupingPreserveTreeRules();
            Console.WriteLine("PASS  图层可归组与同级前排，并拒绝页面移动和层级循环");
            ParentLockAndOpacityFlowToDescendants();
            Console.WriteLine("PASS  父组锁定与透明度正确作用于子图层");
            ProjectSaveReloadRoundTripsThroughJsonRepository();
            Console.WriteLine("PASS  项目可保存并从隔离 JSON 仓库重载");
            SessionOnlyChangesPersistOnShutdown();
            Console.WriteLine("PASS  Zoom、工具和选择等轻量会话状态可在退出时保存");
            InvalidThemeSettingFallsBackToDark();
            Console.WriteLine("PASS  非法主题设置安全回退到暗色");
            ProjectValidatorRejectsDuplicateLayerMounts();
            Console.WriteLine("PASS  项目校验拒绝重复挂载的图层引用");
            AvalonDockLayoutSaveRestoreRoundTripsByContentId();
            Console.WriteLine("PASS  AvalonDock 多业务文档布局可按 ContentId 往返、保留关闭状态并从备份恢复");
            DarkThemePopupItemsKeepReadableContrast();
            Console.WriteLine("PASS  暗色主题菜单与 Agent 下拉项在弹出层保持可读对比度");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL  {exception}");
            return 1;
        }
        finally
        {
            application?.Shutdown();
            DeleteTemporaryRoot();
        }
    }

    private static void TilemapZeroRotationUsesCartesianScreenAxes()
    {
        var grid = new TilemapGridSurface
        {
            CellSize = 100d,
            DiamondAngle = 90d,
            GridRotation = 0d,
            Zoom = 1d
        };
        grid.Measure(new Size(400d, 400d));
        grid.Arrange(new Rect(0d, 0d, 400d, 400d));

        var origin = grid.GetScreenPoint(0d, 0d);
        var axisAEnd = grid.GetScreenPoint(1d, 0d);
        var axisBEnd = grid.GetScreenPoint(0d, 1d);
        Near(100d, axisAEnd.X - origin.X, 0.000000001,
            "Tilemap 0° A/X 轴应水平向右");
        Near(0d, axisAEnd.Y - origin.Y, 0.000000001,
            "Tilemap 0° A/X 轴不应残留固定 45° 纵向分量");
        Near(0d, axisBEnd.X - origin.X, 0.000000001,
            "Tilemap 0° B/Y 轴不应残留固定 45° 横向分量");
        Near(100d, axisBEnd.Y - origin.Y, 0.000000001,
            "Tilemap 0° B/Y 轴应垂直向下");

        grid.DiamondAngle = TilemapGridSurface.DefaultDiamondAngle;
        origin = grid.GetScreenPoint(0d, 0d);
        axisAEnd = grid.GetScreenPoint(1d, 0d);
        axisBEnd = grid.GetScreenPoint(0d, 1d);
        Near(100d, axisAEnd.X - origin.X, 0.000000001,
            "Tilemap 默认控制角在 0° 时 A/X 轴仍应水平向右");
        Near(0d, axisAEnd.Y - origin.Y, 0.000000001,
            "Tilemap 默认控制角在 0° 时不应残留半控制角偏转");
        Near(60d, axisBEnd.X - origin.X, 0.000000001,
            "Tilemap 默认控制角应只控制 B/Y 轴的菱形斜率");
        Near(80d, axisBEnd.Y - origin.Y, 0.000000001,
            "Tilemap 默认控制角的 B/Y 轴应朝屏幕下方");
        grid.DiamondAngle = 90d;

        var zeroMetrics = TilemapConstraintPackageBuilder.CalculateMetrics(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = 100d,
                ArtWidth = 512,
                ArtHeight = 512,
                DiamondAngle = 90d,
                RotationAngle = 0d
            });
        Near(100d, zeroMetrics.AxisAX, 0.000000001,
            "Tilemap AI 几何的 0° A/X 轴应水平向右");
        Near(0d, zeroMetrics.AxisAY, 0.000000001,
            "Tilemap AI 几何的 0° A/X 轴不应倾斜");
        Near(0d, zeroMetrics.AxisBX, 0.000000001,
            "Tilemap AI 几何的 0° B/Y 轴不应倾斜");
        Near(100d, zeroMetrics.AxisBY, 0.000000001,
            "Tilemap AI 几何的 0° B/Y 轴应垂直向下");
        var zeroConstraints = TilemapConstraintPackageBuilder.Build(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = 100d,
                ArtWidth = 512,
                ArtHeight = 512,
                DiamondAngle = 90d,
                RotationAngle = 0d
            });
        var zeroTilemapParameters = zeroConstraints.Parameters["tilemap"] as JsonObject
            ?? throw new SmokeTestException("Tilemap 0° 约束缺少 parameters.tilemap。");
        Equal(
            TilemapConstraintPackageBuilder.RotationConvention,
            zeroTilemapParameters["rotationConvention"]?.GetValue<string>(),
            "Tilemap 新任务应记录 A/X 轴零角对齐契约");
        var zeroFixturePath = Path.Combine(_temporaryRoot, "tilemap-axis-a-zero-square.png");
        WritePngFixture(
            zeroFixturePath,
            512,
            512,
            includeAlpha: true,
            diamondAngle: 90d,
            rotationAngle: 0d);
        GeneratedAssetValidator.Validate(zeroFixturePath, zeroConstraints);
        True(zeroConstraints.Prompt.Contains("A/X 轴向右为 0°", StringComparison.Ordinal),
            "Tilemap AI 提示应明确新旋转零角基准");
        True(zeroConstraints.Prompt.Contains("控制角 90° 表示正方形", StringComparison.Ordinal),
            "Tilemap 90° 控制角必须被明确翻译为正方形，而不是视觉菱形");
        True(zeroConstraints.Prompt.Contains("边 A 相对屏幕 X 轴顺时针 0°", StringComparison.Ordinal) &&
             zeroConstraints.Prompt.Contains("边 B 相对屏幕 X 轴顺时针 90°", StringComparison.Ordinal),
            "Tilemap AI 提示必须冻结两条边的绝对屏幕方向");
        True(zeroConstraints.Prompt.Contains("上下边水平、左右边垂直", StringComparison.Ordinal),
            "Tilemap 90°/0° AI 提示必须明确画布轴对齐轮廓");
        True(zeroConstraints.Prompt.Contains("禁止额外旋转 45°", StringComparison.Ordinal) &&
             zeroConstraints.Prompt.Contains("角点朝上的钻石形", StringComparison.Ordinal),
            "Tilemap 90°/0° AI 提示必须明确排除错误的 on-point diamond 解释");
        False(zeroConstraints.Prompt.Contains("菱形外", StringComparison.Ordinal),
            "Tilemap 方形场景不得继续用‘菱形外’诱导图像模型旋转轮廓");

        var misorientedFixturePath = Path.Combine(
            _temporaryRoot,
            "tilemap-axis-a-zero-wrong-on-point-diamond.png");
        WritePngFixture(
            misorientedFixturePath,
            512,
            512,
            includeAlpha: true,
            diamondAngle: 90d,
            rotationAngle: 0d,
            tilemapRotationConvention: TilemapRotationConvention.LegacyDiamondAxisAligned);
        Throws<TilemapGeometryValidationException>(
            () => GeneratedAssetValidator.Validate(misorientedFixturePath, zeroConstraints),
            "Tilemap 90°/0° 校验必须拒绝内接的 45° 钻石形产物");

        var obliqueConstraints = TilemapConstraintPackageBuilder.Build(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = 100d,
                ArtWidth = 512,
                ArtHeight = 256,
                DiamondAngle = TilemapGridSurface.DefaultDiamondAngle,
                RotationAngle = 0d
            });
        True(obliqueConstraints.Prompt.Contains(
                 "边 A 相对屏幕 X 轴顺时针 0°",
                 StringComparison.Ordinal) &&
             obliqueConstraints.Prompt.Contains(
                 "边 B 相对屏幕 X 轴顺时针 53.13°",
                 StringComparison.Ordinal),
            "非方形 Tilemap 也必须用两条边的绝对方向消除控制角歧义");

        var legacyMetrics = TilemapConstraintPackageBuilder.CalculateMetrics(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = 100d,
                ArtWidth = 512,
                ArtHeight = 512,
                DiamondAngle = 90d,
                RotationAngle = 0d
            },
            TilemapRotationConvention.LegacyDiamondAxisAligned);
        Near(100d / Math.Sqrt(2d), legacyMetrics.AxisAX, 0.000000001,
            "旧 Tilemap 任务应保留菱形对称轴零角解释");
        var legacyParameters = zeroConstraints.Parameters.DeepClone().AsObject();
        var legacyTilemapParameters = legacyParameters["tilemap"] as JsonObject
            ?? throw new SmokeTestException("旧 Tilemap 兼容测试缺少 parameters.tilemap。");
        legacyTilemapParameters["rotationConvention"] =
            TilemapGeometryCalculator.LegacyDiamondAxisAlignedRotationConventionId;
        legacyTilemapParameters["projectedTileWidth"] = legacyMetrics.ProjectedTileWidth;
        legacyTilemapParameters["projectedTileHeight"] = legacyMetrics.ProjectedTileHeight;
        legacyTilemapParameters["rotatedFootprintWidth"] = legacyMetrics.RotatedFootprintWidth;
        legacyTilemapParameters["rotatedFootprintHeight"] = legacyMetrics.RotatedFootprintHeight;
        legacyTilemapParameters["pixelDensityX"] = legacyMetrics.PixelDensityX;
        legacyTilemapParameters["pixelDensityY"] = legacyMetrics.PixelDensityY;
        legacyTilemapParameters["axisA"] = new JsonObject
        {
            ["x"] = legacyMetrics.AxisAX,
            ["y"] = legacyMetrics.AxisAY
        };
        legacyTilemapParameters["axisB"] = new JsonObject
        {
            ["x"] = legacyMetrics.AxisBX,
            ["y"] = legacyMetrics.AxisBY
        };
        var legacyConstraints = zeroConstraints with { Parameters = legacyParameters };
        var legacyFixturePath = Path.Combine(_temporaryRoot, "tilemap-legacy-rotation.png");
        WritePngFixture(
            legacyFixturePath,
            512,
            512,
            includeAlpha: true,
            diamondAngle: 90d,
            rotationAngle: 0d,
            tilemapRotationConvention: TilemapRotationConvention.LegacyDiamondAxisAligned);
        GeneratedAssetValidator.Validate(legacyFixturePath, legacyConstraints);

        grid.GridRotation = 45d;
        origin = grid.GetScreenPoint(0d, 0d);
        axisAEnd = grid.GetScreenPoint(1d, 0d);
        axisBEnd = grid.GetScreenPoint(0d, 1d);
        var rotatedComponent = 100d / Math.Sqrt(2d);
        Near(rotatedComponent, axisAEnd.X - origin.X, 0.000000001,
            "Tilemap 45° A/X 轴水平分量");
        Near(rotatedComponent, axisAEnd.Y - origin.Y, 0.000000001,
            "Tilemap 45° A/X 轴垂直分量");
        Near(-rotatedComponent, axisBEnd.X - origin.X, 0.000000001,
            "Tilemap 45° B/Y 轴水平分量");
        Near(rotatedComponent, axisBEnd.Y - origin.Y, 0.000000001,
            "Tilemap 45° B/Y 轴垂直分量");
    }

    private static void DefaultDockLayoutHasStableTopology()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "default-layout"));
        var settings = new UserSettingsService(paths);
        var repository = new JsonProjectRepository(paths);
        var editor = new EditorWorkspaceViewModel(
            repository,
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);
        var layoutService = new AvalonDockLayoutService(paths);
        var window = new MainWindow(shell, layoutService);

        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            True(window.Icon is { Width: > 0d, Height: > 0d },
                "GAssetForge 主窗口必须加载正式品牌图标资源");
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("MainWindow 未暴露名为 DockManager 的 AvalonDock 容器。");
            AssertDefaultTopology(manager);
            AssertBusinessDocumentViews(manager);
            AssertAnchorableTabsUseTopHeaders(window, manager);
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void IconDesignSettingsLoadAndPersistImmediately()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "icon-design-settings"));
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            IconDesignModeKey = "SpecifiedAngle",
            IconDesignImageWidth = 768,
            IconDesignImageHeight = 640,
            IconDesignCameraAzimuthDegrees = -35d,
            IconDesignCameraElevationDegrees = 22d,
            IconDesignCameraRollDegrees = 8d,
            IconDesignCameraDistance = 6.25d,
            IconDesignFocalLengthMillimeters = 70d
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);

        try
        {
            var iconDesign = shell.IconDesign;
            Equal("SpecifiedAngle", iconDesign.CaptureSettings().Mode,
                "Icon 制作模式应从用户设置加载");
            Equal(768, iconDesign.ImageWidth, "Icon 图片宽度应从用户设置加载");
            Equal(640, iconDesign.ImageHeight, "Icon 图片高度应从用户设置加载");
            Equal(-35d, iconDesign.CameraAzimuthDegrees, "Icon 相机方位角应从用户设置加载");
            Equal(22d, iconDesign.CameraElevationDegrees, "Icon 相机仰角应从用户设置加载");
            Equal(8d, iconDesign.CameraRollDegrees, "Icon 镜头滚转应从用户设置加载");
            Equal(6.25d, iconDesign.CameraDistance, "Icon 相机物距应从用户设置加载");
            Equal(70d, iconDesign.FocalLengthMillimeters, "Icon 镜头焦距应从用户设置加载");

            iconDesign.ImageWidth = 1024;
            iconDesign.ImageHeight = 896;
            iconDesign.CameraAzimuthDegrees = 58d;
            iconDesign.CameraElevationDegrees = 34d;
            iconDesign.CameraRollDegrees = -12d;
            iconDesign.CameraDistance = 7.5d;
            iconDesign.FocalLengthMillimeters = 85d;

            var persisted = new UserSettingsService(paths).Load();
            Equal(1024, persisted.IconDesignImageWidth,
                "Icon 图片宽度应在参数变化后即时持久化");
            Equal(896, persisted.IconDesignImageHeight,
                "Icon 图片高度应在参数变化后即时持久化");
            Equal(58d, persisted.IconDesignCameraAzimuthDegrees,
                "Icon 相机方位角应在参数变化后即时持久化");
            Equal(34d, persisted.IconDesignCameraElevationDegrees,
                "Icon 相机仰角应在参数变化后即时持久化");
            Equal(-12d, persisted.IconDesignCameraRollDegrees,
                "Icon 镜头滚转应在参数变化后即时持久化");
            Equal(7.5d, persisted.IconDesignCameraDistance,
                "Icon 相机物距应在参数变化后即时持久化");
            Equal(85d, persisted.IconDesignFocalLengthMillimeters,
                "Icon 镜头焦距应在参数变化后即时持久化");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void ThemeSwitchUpdatesWpfAndAvalonDockResources()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "theme-switch"));
        var settings = new UserSettingsService(paths);
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);

        try
        {
            shell.SetTheme(AppTheme.Light);
            Equal(AppTheme.Light, shell.CurrentTheme, "亮色主题状态");
            True(shell.DockTheme is Vs2013LightTheme, "亮色主题应使用 Vs2013LightTheme");
            True(CurrentPaletteSource().Contains("Palette.Light.xaml", StringComparison.OrdinalIgnoreCase),
                "WPF 合并资源应切换为亮色 Palette");

            shell.SetTheme(AppTheme.Dark);
            Equal(AppTheme.Dark, shell.CurrentTheme, "暗色主题状态");
            True(shell.DockTheme is Vs2013DarkTheme, "暗色主题应使用 Vs2013DarkTheme");
            True(CurrentPaletteSource().Contains("Palette.Dark.xaml", StringComparison.OrdinalIgnoreCase),
                "WPF 合并资源应切换为暗色 Palette");
            Equal(AppTheme.Dark, settings.Load().Theme, "主题选择应写入隔离用户设置");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void DarkThemePopupItemsKeepReadableContrast()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "popup-contrast"));
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            Theme = AppTheme.Dark
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);
        var mainWindow = new MainWindow(shell, new AvalonDockLayoutService(paths));
        Window? comboHost = null;
        var failures = new List<string>();

        void RecordFailure(Action assertion)
        {
            try
            {
                assertion();
            }
            catch (SmokeTestException exception)
            {
                failures.Add(exception.Message);
            }
        }

        try
        {
            shell.SetTheme(AppTheme.Dark);
            mainWindow.Show();
            mainWindow.UpdateLayout();

            var viewMenu = RequireNamedElement<MenuItem>(
                mainWindow,
                "ViewMenu",
                "顶部菜单缺少视图入口。");
            var showToolsMenuItem = RequireNamedElement<MenuItem>(
                mainWindow,
                "ShowToolsMenuItem",
                "视图菜单缺少工具面板项。");
            RecordFailure(() => AssertPopupItemContrast(viewMenu, showToolsMenuItem, "视图菜单项"));

            var agentMenuItem = RequireNamedElement<MenuItem>(
                mainWindow,
                "ShowAgentConnectionMenuItem",
                "视图菜单缺少 MCP Agent 接入项。");
            RecordFailure(() => AssertPopupItemContrast(viewMenu, agentMenuItem, "MCP Agent 接入菜单项"));

            var agentSelector = new ComboBox
            {
                Width = 240,
                Margin = new Thickness(20),
                ItemsSource = AgentProfile.Supported,
                DisplayMemberPath = nameof(AgentProfile.DisplayName),
                SelectedIndex = 0,
                Style = (Style)Application.Current.FindResource("App.ComboBoxStyle")
            };
            comboHost = new Window
            {
                Width = 320,
                Height = 160,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Content = agentSelector,
                Style = (Style)Application.Current.FindResource("App.WindowStyle")
            };
            comboHost.Show();
            comboHost.UpdateLayout();
            agentSelector.IsDropDownOpen = true;
            PumpDispatcher();

            var comboItem = agentSelector.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem
                ?? throw new SmokeTestException("Agent 下拉菜单未生成 Codex 容器。");
            var comboPopup = agentSelector.Template.FindName("PART_Popup", agentSelector) as Popup
                ?? throw new SmokeTestException("Agent 下拉菜单模板缺少 PART_Popup。");
            True(comboPopup.IsOpen && comboPopup.Child is not null,
                "Agent 下拉菜单弹出层应实际打开。");
            Equal("Codex", ((AgentProfile)comboItem.Content).DisplayName,
                "Agent 下拉菜单第一项内容");
            RecordFailure(() => AssertReadableContrast(
                comboItem.Foreground,
                FindPopupBackground(comboPopup.Child!),
                "Agent 下拉菜单项"));

            True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            comboHost?.Close();
            mainWindow.Close();
            shell.Shutdown();
        }
    }

    private static void AssertPopupItemContrast(
        MenuItem parent,
        MenuItem child,
        string label)
    {
        parent.ApplyTemplate();
        parent.IsSubmenuOpen = true;
        PumpDispatcher();

        try
        {
            var popup = parent.Template.FindName("PART_Popup", parent) as Popup
                ?? throw new SmokeTestException($"{label}模板缺少 PART_Popup。");
            True(popup.IsOpen && popup.Child is not null, $"{label}弹出层应实际打开。");
            True(child.IsEnabled && child.Opacity > 0.9d, $"{label}不应因禁用或透明而不可见。");
            AssertReadableContrast(child.Foreground, FindPopupBackground(popup.Child!), label);
        }
        finally
        {
            parent.IsSubmenuOpen = false;
            PumpDispatcher();
        }
    }

    private static Brush FindPopupBackground(DependencyObject root)
    {
        foreach (var element in VisualDescendants<FrameworkElement>(root))
        {
            var brush = element switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null
            };
            if (brush is SolidColorBrush { Color.A: > 0 } solid)
            {
                return solid;
            }
        }

        throw new SmokeTestException("弹出层没有可解析的非透明背景画刷。");
    }

    private static void AssertReadableContrast(Brush foreground, Brush background, string label)
    {
        var foregroundColor = (foreground as SolidColorBrush)?.Color
            ?? throw new SmokeTestException($"{label}文字画刷必须是 SolidColorBrush。");
        var backgroundColor = (background as SolidColorBrush)?.Color
            ?? throw new SmokeTestException($"{label}背景画刷必须是 SolidColorBrush。");
        var contrast = ContrastRatio(foregroundColor, backgroundColor);
        True(
            contrast >= 4.5d,
            $"{label}文字与弹出层背景对比度不足：foreground={foregroundColor}, " +
            $"background={backgroundColor}, ratio={contrast:F2}，至少应为 4.50。");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double RelativeLuminance(Color color)
        {
            static double Linearize(byte component)
            {
                var value = component / 255d;
                return value <= 0.04045d
                    ? value / 12.92d
                    : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
            }

            return 0.2126d * Linearize(color.R) +
                   0.7152d * Linearize(color.G) +
                   0.0722d * Linearize(color.B);
        }

        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05d) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05d);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

    private static void DockPanelsFloatHideAndRestoreFromViewMenu()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "dock-interaction"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        shell.SelectWorkflow("ui");
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("停靠交互测试未找到 DockManager。");
            True(manager.AllowMixedOrientation, "DockManager 应允许混合方向停靠");
            True(manager.AllowAnchorDoubleClickDock, "DockManager 应允许双击浮窗标题恢复停靠");
            True(manager.AllowMovingFloatingWindowWithKeyboard, "DockManager 应允许键盘移动浮窗");

            var menuEntries = new[]
            {
                (Name: "ShowToolsMenuItem", ContentId: MainWindow.ToolsContentId),
                (Name: "ShowPropertiesMenuItem", ContentId: MainWindow.PropertiesContentId),
                (Name: "ShowLayersMenuItem", ContentId: MainWindow.LayersContentId),
                (Name: "ShowProjectMenuItem", ContentId: MainWindow.ProjectContentId)
            };
            foreach (var entry in menuEntries)
            {
                var menuItem = window.FindName(entry.Name) as MenuItem
                    ?? throw new SmokeTestException($"视图菜单缺少 {entry.Name}。");
                menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                True(ReferenceEquals(shell.ShowPanelCommand, menuItem.Command),
                    $"{entry.Name} 应绑定 ShowPanelCommand");
                Equal(entry.ContentId, menuItem.CommandParameter as string,
                    $"{entry.Name} 的 ContentId");
            }

            var workflowMenuEntries = new[]
            {
                (Name: "ShowUiWorkspaceMenuItem", ContentId: MainWindow.CanvasContentId),
                (Name: "ShowTilemapWorkspaceMenuItem", ContentId: MainWindow.TilemapWorkspaceContentId),
                (Name: "ShowCardDesignWorkspaceMenuItem", ContentId: MainWindow.CardDesignWorkspaceContentId),
                (Name: "ShowParallelPerspectiveWorkspaceMenuItem", ContentId: MainWindow.ParallelPerspectiveWorkspaceContentId)
            };
            foreach (var entry in workflowMenuEntries)
            {
                var menuItem = window.FindName(entry.Name) as MenuItem
                    ?? throw new SmokeTestException($"视图菜单缺少 {entry.Name}。");
                menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                True(menuItem.IsCheckable, $"{entry.Name} 应显示业务窗口开启状态");
                True(ReferenceEquals(shell.TogglePanelCommand, menuItem.Command),
                    $"{entry.Name} 应绑定 TogglePanelCommand");
                Equal(entry.ContentId, menuItem.CommandParameter as string,
                    $"{entry.Name} 的 ContentId");
            }

            var styleLibraryMenuItem = window.FindName("ShowStyleLibraryMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少美术风格素材库开关。");
            styleLibraryMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(styleLibraryMenuItem.IsCheckable,
                "美术风格素材库菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, styleLibraryMenuItem.Command),
                "美术风格素材库菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.StyleLibraryContentId, styleLibraryMenuItem.CommandParameter as string,
                "美术风格素材库开关的 ContentId");

            var assetGenerationMenuItem = window.FindName("ShowAssetGenerationMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少素材生成开关。");
            assetGenerationMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(assetGenerationMenuItem.IsCheckable,
                "素材生成菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, assetGenerationMenuItem.Command),
                "素材生成菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.AssetGenerationContentId, assetGenerationMenuItem.CommandParameter as string,
                "素材生成开关的 ContentId");

            var backgroundRemovalMenuItem = window.FindName("ShowBackgroundRemovalMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少背景剔除工具开关。");
            backgroundRemovalMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(backgroundRemovalMenuItem.IsCheckable,
                "背景剔除工具菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, backgroundRemovalMenuItem.Command),
                "背景剔除工具菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.BackgroundRemovalContentId, backgroundRemovalMenuItem.CommandParameter as string,
                "背景剔除工具开关的 ContentId");

            var imageOutlineMenuItem = window.FindName("ShowImageOutlineMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少图片描边工具开关。");
            imageOutlineMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(imageOutlineMenuItem.IsCheckable,
                "图片描边工具菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, imageOutlineMenuItem.Command),
                "图片描边工具菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.ImageOutlineContentId, imageOutlineMenuItem.CommandParameter as string,
                "图片描边工具开关的 ContentId");

            var iconDesignMenuItem = window.FindName("ShowIconDesignMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少 Icon 图标制作开关。");
            iconDesignMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(iconDesignMenuItem.IsCheckable,
                "Icon 图标制作菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, iconDesignMenuItem.Command),
                "Icon 图标制作菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.IconDesignContentId, iconDesignMenuItem.CommandParameter as string,
                "Icon 图标制作开关的 ContentId");

            var pixelAlignmentMenuItem = window.FindName("ShowPixelAlignmentMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少像素点对齐工具开关。");
            pixelAlignmentMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(pixelAlignmentMenuItem.IsCheckable,
                "像素点对齐工具菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, pixelAlignmentMenuItem.Command),
                "像素点对齐工具菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.PixelAlignmentContentId, pixelAlignmentMenuItem.CommandParameter as string,
                "像素点对齐工具开关的 ContentId");

            var blueprintMenuItem = window.FindName("ShowBlueprintMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少蓝图窗口开关。");
            blueprintMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(blueprintMenuItem.IsCheckable,
                "蓝图窗口菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, blueprintMenuItem.Command),
                "蓝图窗口菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.BlueprintContentId, blueprintMenuItem.CommandParameter as string,
                "蓝图窗口开关的 ContentId");

            var commandConsoleMenuItem = window.FindName("ShowCommandConsoleMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少指令日志开关。");
            commandConsoleMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(commandConsoleMenuItem.IsCheckable,
                "指令日志菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, commandConsoleMenuItem.Command),
                "指令日志菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.CommandConsoleContentId, commandConsoleMenuItem.CommandParameter as string,
                "指令日志开关的 ContentId");
            Equal("F12", commandConsoleMenuItem.InputGestureText,
                "指令日志菜单应提示正式控制台快捷键");

            var agentConnectionMenuItem = window.FindName("ShowAgentConnectionMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少 MCP Agent 接入开关。");
            agentConnectionMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(agentConnectionMenuItem.IsCheckable,
                "MCP Agent 接入菜单应显示当前开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, agentConnectionMenuItem.Command),
                "MCP Agent 接入菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.AgentConnectionContentId, agentConnectionMenuItem.CommandParameter as string,
                "MCP Agent 接入菜单的 ContentId");

            var commandConsoleShortcut = window.InputBindings
                .OfType<KeyBinding>()
                .SingleOrDefault(binding =>
                    binding.Key == Key.F12 &&
                    binding.Modifiers == ModifierKeys.None)
                ?? throw new SmokeTestException("主窗口缺少 F12 指令日志快捷键。");
            BindingOperations.GetBindingExpression(
                    commandConsoleShortcut,
                    InputBinding.CommandProperty)
                ?.UpdateTarget();
            True(ReferenceEquals(shell.ShowPanelCommand, commandConsoleShortcut.Command),
                "F12 应绑定 ShowPanelCommand 以拉起并聚焦指令日志");
            Equal(MainWindow.CommandConsoleContentId, commandConsoleShortcut.CommandParameter as string,
                "F12 指令日志快捷键 ContentId");

            var anchorableIds = new[]
            {
                MainWindow.ToolsContentId,
                MainWindow.PropertiesContentId,
                MainWindow.LayersContentId,
                MainWindow.ProjectContentId,
                MainWindow.StyleLibraryContentId,
                MainWindow.AssetGenerationContentId,
                MainWindow.BackgroundRemovalContentId,
                MainWindow.ImageOutlineContentId,
                MainWindow.IconDesignContentId,
                MainWindow.PixelAlignmentContentId,
                MainWindow.BlueprintContentId,
                MainWindow.AgentConnectionContentId,
                MainWindow.CommandConsoleContentId
            };
            foreach (var contentId in anchorableIds)
            {
                var panel = FindDockContent(manager, contentId) as LayoutAnchorable
                    ?? throw new SmokeTestException($"{contentId} 必须是 LayoutAnchorable。");
                True(panel.CanDockAsTabbedDocument,
                    $"{contentId} 应允许拖入中央文档标签区");
                panel.Hide();
                True(panel.IsHidden, $"{contentId} 应能隐藏");

                shell.ShowPanelCommand.Execute(contentId);
                panel = FindDockContent(manager, contentId) as LayoutAnchorable
                    ?? throw new SmokeTestException($"视图菜单未恢复 {contentId}。");
                False(panel.IsHidden, $"{contentId} 从视图菜单拉起后不应隐藏");
                True(panel.IsVisible, $"{contentId} 从视图菜单拉起后应可见");
                True(panel.IsSelected, $"{contentId} 从视图菜单拉起后应被选中");
                True(panel.IsActive, $"{contentId} 从视图菜单拉起后应被激活");
                True(ReferenceEquals(manager.Layout.ActiveContent, panel),
                    $"{contentId} 应成为当前活动内容");
            }

            var properties = FindDockContent(manager, MainWindow.PropertiesContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到属性面板。");
            var propertiesHost = properties.Content as ContentControl
                ?? throw new SmokeTestException("属性面板必须由 ContentControl 承载。");

            var styleLibrary = FindDockContent(manager, MainWindow.StyleLibraryContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到美术风格素材库。");
            True(styleLibrary.IsVisible && !styleLibrary.IsHidden,
                "美术风格素材库初始应作为 Project 同区标签页存在");
            styleLibraryMenuItem.Command!.Execute(styleLibraryMenuItem.CommandParameter);
            True(styleLibrary.IsHidden, "从视图菜单关闭后美术风格素材库应隐藏");
            False(styleLibraryMenuItem.IsChecked,
                "美术风格素材库隐藏后视图菜单应取消勾选");
            styleLibraryMenuItem.Command.Execute(styleLibraryMenuItem.CommandParameter);
            False(styleLibrary.IsHidden, "再次点击视图菜单应恢复美术风格素材库");
            True(styleLibrary.IsVisible && styleLibrary.IsSelected && styleLibrary.IsActive,
                "重新开启后美术风格素材库应可见、选中并激活");
            True(styleLibraryMenuItem.IsChecked,
                "美术风格素材库重新开启后视图菜单应恢复勾选");

            var assetGeneration = FindDockContent(manager, MainWindow.AssetGenerationContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到素材生成面板。");
            True(assetGeneration.IsVisible && !assetGeneration.IsHidden,
                "素材生成面板初始应作为 Project 同区标签页存在");
            assetGenerationMenuItem.Command!.Execute(assetGenerationMenuItem.CommandParameter);
            True(assetGeneration.IsHidden, "从视图菜单关闭后素材生成面板应隐藏");
            False(assetGenerationMenuItem.IsChecked,
                "素材生成面板隐藏后视图菜单应取消勾选");
            assetGenerationMenuItem.Command.Execute(assetGenerationMenuItem.CommandParameter);
            False(assetGeneration.IsHidden, "再次点击视图菜单应恢复素材生成面板");
            True(assetGeneration.IsVisible && assetGeneration.IsSelected && assetGeneration.IsActive,
                "重新开启后素材生成面板应可见、选中并激活");
            True(assetGenerationMenuItem.IsChecked,
                "素材生成面板重新开启后视图菜单应恢复勾选");

            var backgroundRemoval = FindDockContent(manager, MainWindow.BackgroundRemovalContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到背景剔除工具面板。");
            True(backgroundRemoval.IsVisible && !backgroundRemoval.IsHidden,
                "背景剔除工具面板初始应作为 Project 同区标签页存在");
            backgroundRemovalMenuItem.Command!.Execute(backgroundRemovalMenuItem.CommandParameter);
            True(backgroundRemoval.IsHidden, "从视图菜单关闭后背景剔除工具面板应隐藏");
            False(backgroundRemovalMenuItem.IsChecked,
                "背景剔除工具面板隐藏后视图菜单应取消勾选");
            backgroundRemovalMenuItem.Command.Execute(backgroundRemovalMenuItem.CommandParameter);
            False(backgroundRemoval.IsHidden, "再次点击视图菜单应恢复背景剔除工具面板");
            True(backgroundRemoval.IsVisible && backgroundRemoval.IsSelected && backgroundRemoval.IsActive,
                "重新开启后背景剔除工具面板应可见、选中并激活");
            True(backgroundRemovalMenuItem.IsChecked,
                "背景剔除工具面板重新开启后视图菜单应恢复勾选");

            var imageOutline = FindDockContent(manager, MainWindow.ImageOutlineContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到图片描边工具面板。");
            True(imageOutline.IsVisible && !imageOutline.IsHidden,
                "图片描边工具面板初始应作为 Project 同区标签页存在");
            imageOutlineMenuItem.Command!.Execute(imageOutlineMenuItem.CommandParameter);
            True(imageOutline.IsHidden, "从视图菜单关闭后图片描边工具面板应隐藏");
            False(imageOutlineMenuItem.IsChecked,
                "图片描边工具面板隐藏后视图菜单应取消勾选");
            imageOutlineMenuItem.Command.Execute(imageOutlineMenuItem.CommandParameter);
            False(imageOutline.IsHidden, "再次点击视图菜单应恢复图片描边工具面板");
            True(imageOutline.IsVisible && imageOutline.IsSelected && imageOutline.IsActive,
                "重新开启后图片描边工具面板应可见、选中并激活");
            True(imageOutlineMenuItem.IsChecked,
                "图片描边工具面板重新开启后视图菜单应恢复勾选");

            var imageOutlineHost = imageOutline.Content as ContentControl
                ?? throw new SmokeTestException("图片描边工具缺少稳定 ContentControl host。");
            var imageOutlineView = imageOutlineHost.Content as ImageOutlineView
                ?? throw new SmokeTestException("图片描边工具 host 未承载 ImageOutlineView。");
            True(ReferenceEquals(shell.ImageOutline, imageOutlineView.DataContext),
                "图片描边工具应绑定 Shell 持有的单实例 ViewModel");
            var imageOutlinePropertiesView = propertiesHost.Content as ImageOutlinePropertiesView
                ?? throw new SmokeTestException("激活图片描边工具后属性栏未切换为 ImageOutlinePropertiesView。");
            True(ReferenceEquals(shell.ImageOutline, imageOutlinePropertiesView.DataContext),
                "图片描边属性页与预览主视图必须共享同一个 ViewModel");
            var outlineModeComboBox = RequireNamedElement<ComboBox>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesModeComboBox",
                "图片描边属性页缺少描边种类下拉框。");
            var outlineWidthSlider = RequireNamedElement<Slider>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesWidthSlider",
                "图片描边属性页缺少描边宽度控件。");
            var outlineColorTextBox = RequireNamedElement<TextBox>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesColorTextBox",
                "图片描边属性页缺少描边颜色输入框。");
            var outlineBatchDataGrid = RequireNamedElement<DataGrid>(
                imageOutlineView,
                "ImageOutlineBatchDataGrid",
                "图片描边预览页缺少批处理结果队列。");
            var outlineBatchQueue = RequireNamedElement<ListBox>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesBatchQueue",
                "图片描边属性页缺少批处理操作队列。");
            var outlineProcessButton = RequireNamedElement<Button>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesRefreshPreviewButton",
                "图片描边属性页缺少单图预览刷新按钮。");
            var outlineColorPickerButton = RequireNamedElement<Button>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesColorPickerButton",
                "图片描边属性页缺少共享取色器入口。");
            var outlineResultSaveButton = RequireNamedElement<Button>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesSaveResultButton",
                "图片描边属性页缺少保存结果按钮。");
            var outlineBatchProcessButton = RequireNamedElement<Button>(
                imageOutlinePropertiesView,
                "ImageOutlinePropertiesProcessBatchButton",
                "图片描边属性页缺少批量执行按钮。");
            var outlineInputPreview = RequireNamedElement<ImagePreviewControl>(
                imageOutlineView,
                "ImageOutlineInputPreview",
                "图片描边主视图缺少通用源图预览。");
            var outlineOutputPreview = RequireNamedElement<ImagePreviewControl>(
                imageOutlineView,
                "ImageOutlineOutputPreview",
                "图片描边主视图缺少通用结果预览。");
            outlineModeComboBox.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
            True(ReferenceEquals(shell.ImageOutline.ModeOptions, outlineModeComboBox.ItemsSource),
                "描边种类下拉框应使用 ViewModel 稳定选项");
            Equal(1d, outlineWidthSlider.Minimum, "描边宽度最小值");
            Equal(128d, outlineWidthSlider.Maximum, "描边宽度最大值");
            Equal(nameof(ImageOutlineViewModel.OutlineColorHex),
                BindingOperations.GetBindingExpression(outlineColorTextBox, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "描边颜色输入绑定");
            True(ReferenceEquals(shell.ImageOutline.BatchItems, outlineBatchDataGrid.ItemsSource),
                "主视图批处理表格应绑定图片描边队列");
            True(ReferenceEquals(shell.ImageOutline.BatchItems, outlineBatchQueue.ItemsSource),
                "属性页批处理队列应绑定图片描边队列");
            True(ReferenceEquals(shell.ImageOutline.RefreshPreviewCommand, outlineProcessButton.Command),
                "属性页单图入口按钮应绑定图片描边预览刷新命令");
            True(ReferenceEquals(shell.ImageOutline.ChooseOutlineColorCommand, outlineColorPickerButton.Command),
                "描边色块应绑定共享取色器命令");
            True(ReferenceEquals(shell.ImageOutline.SaveResultCommand, outlineResultSaveButton.Command),
                "属性页应绑定确认保存命令");
            True(ReferenceEquals(shell.ImageOutline.ProcessBatchCommand, outlineBatchProcessButton.Command),
                "批量执行按钮应绑定批量描边命令");
            Equal(BitmapScalingMode.HighQuality, outlineInputPreview.BitmapScalingMode,
                "描边源图预览缩放质量");
            Equal(BitmapScalingMode.HighQuality, outlineOutputPreview.BitmapScalingMode,
                "描边结果预览缩放质量");
            True(imageOutlineView.FindName("ImageOutlinePropertiesModeComboBox") is null,
                "图片描边主视图不应重复承载属性操作控件");

            var iconDesign = FindDockContent(manager, MainWindow.IconDesignContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到 Icon 图标制作面板。");
            True(iconDesign.IsVisible && !iconDesign.IsHidden,
                "Icon 图标制作面板初始应作为 Project 同区标签页存在");
            iconDesignMenuItem.Command!.Execute(iconDesignMenuItem.CommandParameter);
            True(iconDesign.IsHidden, "从视图菜单关闭后 Icon 图标制作面板应隐藏");
            False(iconDesignMenuItem.IsChecked,
                "Icon 图标制作面板隐藏后视图菜单应取消勾选");
            iconDesignMenuItem.Command.Execute(iconDesignMenuItem.CommandParameter);
            False(iconDesign.IsHidden, "再次点击视图菜单应恢复 Icon 图标制作面板");
            True(iconDesign.IsVisible && iconDesign.IsSelected && iconDesign.IsActive,
                "重新开启后 Icon 图标制作面板应可见、选中并激活");
            True(iconDesignMenuItem.IsChecked,
                "Icon 图标制作面板重新开启后视图菜单应恢复勾选");

            var iconDesignHost = iconDesign.Content as ContentControl
                ?? throw new SmokeTestException("Icon 图标制作缺少稳定 ContentControl host。");
            var iconDesignView = iconDesignHost.Content as IconDesignView
                ?? throw new SmokeTestException("Icon 图标制作 host 未承载 IconDesignView。");
            True(ReferenceEquals(shell.IconDesign, iconDesignView.DataContext),
                "Icon 图标制作应绑定 Shell 持有的单实例 ViewModel");

            var iconPropertiesView = propertiesHost.Content as IconDesignPropertiesView
                ?? throw new SmokeTestException("激活 Icon 图标制作后属性栏未切换为 IconDesignPropertiesView。");
            True(ReferenceEquals(shell.IconDesign, iconPropertiesView.DataContext),
                "Icon 属性栏与主视图必须共享同一个 ViewModel");

            var iconModeComboBox = RequireNamedElement<ComboBox>(
                iconPropertiesView,
                "IconDesignModeComboBox",
                "Icon 图标制作缺少制作模式下拉框。");
            iconModeComboBox.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
            True(ReferenceEquals(shell.IconDesign.ModeOptions, iconModeComboBox.ItemsSource),
                "Icon 制作模式下拉框应使用 ViewModel 稳定选项");
            Equal(nameof(IconDesignViewModel.SelectedMode),
                BindingOperations.GetBindingExpression(
                    iconModeComboBox,
                    Selector.SelectedItemProperty)?.ParentBinding.Path.Path,
                "Icon 制作模式选择绑定");

            var iconImageWidthTextBox = RequireNamedElement<TextBox>(
                iconPropertiesView,
                "IconImageWidthTextBox",
                "Icon 图标制作缺少图片宽度输入框。");
            var iconImageHeightTextBox = RequireNamedElement<TextBox>(
                iconPropertiesView,
                "IconImageHeightTextBox",
                "Icon 图标制作缺少图片高度输入框。");
            Equal(nameof(IconDesignViewModel.ImageWidth),
                BindingOperations.GetBindingExpression(
                    iconImageWidthTextBox,
                    TextBox.TextProperty)?.ParentBinding.Path.Path,
                "Icon 图片宽度绑定");
            Equal(nameof(IconDesignViewModel.ImageHeight),
                BindingOperations.GetBindingExpression(
                    iconImageHeightTextBox,
                    TextBox.TextProperty)?.ParentBinding.Path.Path,
                "Icon 图片高度绑定");

            var iconSliderBindings = new[]
            {
                (Name: "IconCameraAzimuthSlider", Path: nameof(IconDesignViewModel.CameraAzimuthDegrees), Minimum: -180d, Maximum: 180d),
                (Name: "IconCameraElevationSlider", Path: nameof(IconDesignViewModel.CameraElevationDegrees), Minimum: -85d, Maximum: 85d),
                (Name: "IconCameraRollSlider", Path: nameof(IconDesignViewModel.CameraRollDegrees), Minimum: -180d, Maximum: 180d),
                (Name: "IconFocalLengthSlider", Path: nameof(IconDesignViewModel.FocalLengthMillimeters), Minimum: 12d, Maximum: 200d),
                (Name: "IconCameraDistanceSlider", Path: nameof(IconDesignViewModel.CameraDistance), Minimum: 1.5d, Maximum: 20d)
            };
            foreach (var sliderBinding in iconSliderBindings)
            {
                var slider = RequireNamedElement<Slider>(
                    iconPropertiesView,
                    sliderBinding.Name,
                    $"Icon 图标制作缺少 {sliderBinding.Name}。");
                Equal(sliderBinding.Path,
                    BindingOperations.GetBindingExpression(
                        slider,
                        RangeBase.ValueProperty)?.ParentBinding.Path.Path,
                    $"{sliderBinding.Name} 参数绑定");
                Equal(sliderBinding.Minimum, slider.Minimum, $"{sliderBinding.Name} 最小值");
                Equal(sliderBinding.Maximum, slider.Maximum, $"{sliderBinding.Name} 最大值");
            }

            var iconSpatialSurface = RequireNamedElement<IconPerspectiveSurface>(
                iconDesignView,
                "IconSpatialSetupSurface",
                "Icon 图标制作缺少左侧相机三维空间预览。");
            var iconCameraFrameSurface = RequireNamedElement<IconPerspectiveSurface>(
                iconDesignView,
                "IconCameraFrameSurface",
                "Icon 图标制作缺少右侧镜头画面预览。");
            foreach (var surface in new[] { iconSpatialSurface, iconCameraFrameSurface })
            {
                Equal(nameof(IconDesignViewModel.Projection),
                    BindingOperations.GetBindingExpression(
                        surface,
                        IconPerspectiveSurface.ProjectionProperty)?.ParentBinding.Path.Path,
                    "Icon 双主视图投影绑定");
                True(ReferenceEquals(shell.IconDesign.Projection, surface.Projection),
                    "Icon 双主视图应共享当前实时投影快照");
            }
            Equal(IconPerspectiveSurfaceViewKind.SpatialSetup, iconSpatialSurface.ViewKind,
                "Icon 左侧主视图类型");
            Equal(IconPerspectiveSurfaceViewKind.CameraFrame, iconCameraFrameSurface.ViewKind,
                "Icon 右侧主视图类型");

            foreach (var emptyPropertiesContext in new[]
                     {
                         (ContentId: MainWindow.StyleLibraryContentId, Title: "美术风格素材库"),
                         (ContentId: MainWindow.AssetGenerationContentId, Title: "素材生成")
                     })
            {
                shell.ShowPanelCommand.Execute(emptyPropertiesContext.ContentId);
                var emptyPropertiesPanel = propertiesHost.Content as Border
                    ?? throw new SmokeTestException(
                        $"激活{emptyPropertiesContext.Title}后，属性栏不应继续显示业务制作项属性。");
                Equal(emptyPropertiesContext.ContentId, emptyPropertiesPanel.Tag as string,
                    $"{emptyPropertiesContext.Title}空属性页上下文 ContentId");
                var emptyPropertyTexts = VisualDescendants<TextBlock>(emptyPropertiesPanel)
                    .Select(text => text.Text)
                    .ToArray();
                True(emptyPropertyTexts.Contains(emptyPropertiesContext.Title, StringComparer.Ordinal),
                    $"{emptyPropertiesContext.Title}空属性页应显示当前窗口名称");
                True(emptyPropertyTexts.Contains("当前窗口暂无可显示的属性信息。", StringComparer.Ordinal),
                    $"{emptyPropertiesContext.Title}空属性页应显示统一默认提示");
            }

            shell.ShowPanelCommand.Execute(MainWindow.IconDesignContentId);
            True(ReferenceEquals(iconPropertiesView, propertiesHost.Content),
                "再次激活 Icon 时应恢复同一个 Icon 属性视图实例");

            shell.ShowPanelCommand.Execute(MainWindow.BackgroundRemovalContentId);
            var backgroundRemovalHost = backgroundRemoval.Content as ContentControl
                ?? throw new SmokeTestException("背景剔除工具缺少稳定 ContentControl host。");
            var backgroundRemovalView = backgroundRemovalHost.Content as BackgroundRemovalView
                ?? throw new SmokeTestException("背景剔除工具 host 未承载 BackgroundRemovalView。");
            var backgroundRemovalPropertiesView = propertiesHost.Content as BackgroundRemovalPropertiesView
                ?? throw new SmokeTestException("激活背景剔除工具后属性栏未切换为 BackgroundRemovalPropertiesView。");
            True(ReferenceEquals(shell.BackgroundRemoval, backgroundRemovalView.DataContext),
                "背景剔除主视图应绑定 Shell 持有的单实例 ViewModel");
            True(ReferenceEquals(shell.BackgroundRemoval, backgroundRemovalPropertiesView.DataContext),
                "背景剔除属性页与预览主视图必须共享同一个 ViewModel");
            var fakeTransparencyCheckBox = RequireNamedElement<CheckBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesFakeTransparencyCheckBox",
                "背景剔除属性页缺少假透明网格开关。");
            var checkerColorA = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerColorATextBox",
                "背景剔除属性页缺少网格颜色 A。");
            var checkerColorB = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerColorBTextBox",
                "背景剔除属性页缺少网格颜色 B。");
            var checkerCellSizeInput = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerCellSizeTextBox",
                "背景剔除属性页缺少浮点网格尺寸输入。");
            var checkerBoundaryToleranceInput = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerBoundaryToleranceTextBox",
                "背景剔除属性页缺少浮点边界容差输入。");
            var checkerOffsetXInput = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerOffsetXTextBox",
                "背景剔除属性页缺少浮点 X 偏移输入。");
            var checkerOffsetYInput = RequireNamedElement<TextBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesCheckerOffsetYTextBox",
                "背景剔除属性页缺少浮点 Y 偏移输入。");
            var checkerPreview = RequireNamedElement<CheckerGridImagePreviewControl>(
                backgroundRemovalView,
                "BackgroundRemovalInputPreview",
                "背景剔除工具缺少可导航的网格源图预览。");
            var backgroundOutputPreview = RequireNamedElement<ImagePreviewControl>(
                backgroundRemovalView,
                "BackgroundRemovalOutputPreview",
                "背景剔除工具缺少可导航的结果预览。");
            var backgroundResultRefreshButton = RequireNamedElement<Button>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesRefreshPreviewButton",
                "背景剔除属性页缺少刷新预览按钮。");
            var backgroundResultSaveButton = RequireNamedElement<Button>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesSaveResultButton",
                "背景剔除属性页缺少保存结果按钮。");
            var backgroundBatchModeCheckBox = RequireNamedElement<CheckBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesBatchModeCheckBox",
                "背景剔除属性页缺少批处理开关。");
            var backgroundBatchQueue = RequireNamedElement<ListBox>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesBatchQueue",
                "背景剔除属性页缺少批处理队列。");
            var backgroundBatchDataGrid = RequireNamedElement<DataGrid>(
                backgroundRemovalView,
                "BackgroundRemovalBatchDataGrid",
                "背景剔除主视图缺少批处理结果队列。");
            var backgroundBatchProcessButton = RequireNamedElement<Button>(
                backgroundRemovalPropertiesView,
                "BackgroundRemovalPropertiesProcessBatchButton",
                "背景剔除属性页缺少批量执行按钮。");
            Equal(nameof(BackgroundRemovalViewModel.UseFakeTransparencyGrid),
                BindingOperations.GetBindingExpression(fakeTransparencyCheckBox, ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path,
                "假透明网格开关绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerColorAHex),
                BindingOperations.GetBindingExpression(checkerColorA, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "网格颜色 A 绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerColorBHex),
                BindingOperations.GetBindingExpression(checkerColorB, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "网格颜色 B 绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerCellSize),
                BindingOperations.GetBindingExpression(checkerCellSizeInput, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "属性页浮点网格尺寸绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerBoundaryTolerance),
                BindingOperations.GetBindingExpression(checkerBoundaryToleranceInput, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "属性页浮点边界容差绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerOffsetX),
                BindingOperations.GetBindingExpression(checkerOffsetXInput, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "属性页浮点 X 偏移绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerOffsetY),
                BindingOperations.GetBindingExpression(checkerOffsetYInput, TextBox.TextProperty)?.ParentBinding.Path.Path,
                "属性页浮点 Y 偏移绑定");
            var checkerCellBinding = BindingOperations.GetBindingExpression(
                checkerPreview,
                CheckerGridImagePreviewControl.CellSizeProperty)?.ParentBinding;
            var checkerOffsetXBinding = BindingOperations.GetBindingExpression(
                checkerPreview,
                CheckerGridImagePreviewControl.OffsetXProperty)?.ParentBinding;
            var checkerOffsetYBinding = BindingOperations.GetBindingExpression(
                checkerPreview,
                CheckerGridImagePreviewControl.OffsetYProperty)?.ParentBinding;
            Equal(nameof(BackgroundRemovalViewModel.CheckerCellSize), checkerCellBinding?.Path.Path,
                "网格源图预览尺寸绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerOffsetX), checkerOffsetXBinding?.Path.Path,
                "网格源图预览 X 偏移绑定");
            Equal(nameof(BackgroundRemovalViewModel.CheckerOffsetY), checkerOffsetYBinding?.Path.Path,
                "网格源图预览 Y 偏移绑定");
            Equal(BindingMode.TwoWay, checkerCellBinding?.Mode ?? BindingMode.Default,
                "标尺修改格宽必须双向回写 ViewModel");
            Equal(BindingMode.TwoWay, checkerOffsetXBinding?.Mode ?? BindingMode.Default,
                "标尺修改 X 相位必须双向回写 ViewModel");
            Equal(BindingMode.TwoWay, checkerOffsetYBinding?.Mode ?? BindingMode.Default,
                "标尺修改 Y 相位必须双向回写 ViewModel");
            Equal(nameof(BackgroundRemovalViewModel.ShowCheckerGridOverlay),
                BindingOperations.GetBindingExpression(
                    checkerPreview,
                    CheckerGridImagePreviewControl.IsRulerEditingEnabledProperty)?.ParentBinding.Path.Path,
                "标尺编辑状态应跟随假透明网格对齐开关");
            Equal(typeof(double), CheckerGridImagePreviewControl.CellSizeProperty.PropertyType,
                "预览格宽 DP 必须支持浮点源像素");
            Equal(typeof(double), CheckerGridImagePreviewControl.OffsetXProperty.PropertyType,
                "预览 X 偏移 DP 必须支持浮点源像素");
            Equal(typeof(double), CheckerGridImagePreviewControl.OffsetYProperty.PropertyType,
                "预览 Y 偏移 DP 必须支持浮点源像素");
            True(((FrameworkPropertyMetadata)CheckerGridImagePreviewControl.CellSizeProperty
                    .GetMetadata(typeof(CheckerGridImagePreviewControl))).BindsTwoWayByDefault,
                "预览格宽 DP 应默认双向绑定");
            Equal(nameof(BackgroundRemovalViewModel.SourcePixelWidth),
                BindingOperations.GetBindingExpression(checkerPreview, CheckerGridImagePreviewControl.SourcePixelWidthProperty)?.ParentBinding.Path.Path,
                "网格源图预览源图宽度绑定");
            Equal(nameof(BackgroundRemovalViewModel.SourcePixelHeight),
                BindingOperations.GetBindingExpression(checkerPreview, CheckerGridImagePreviewControl.SourcePixelHeightProperty)?.ParentBinding.Path.Path,
                "网格源图预览源图高度绑定");
            True(ReferenceEquals(shell.BackgroundRemoval.RefreshPreviewCommand, backgroundResultRefreshButton.Command),
                "背景剔除属性页应绑定刷新缓存预览命令");
            True(ReferenceEquals(shell.BackgroundRemoval.SaveResultCommand, backgroundResultSaveButton.Command),
                "背景剔除属性页应绑定确认保存命令");
            Equal(nameof(BackgroundRemovalViewModel.IsBatchMode),
                BindingOperations.GetBindingExpression(backgroundBatchModeCheckBox, ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path,
                "背景剔除批处理模式绑定");
            True(ReferenceEquals(shell.BackgroundRemoval.BatchItems, backgroundBatchQueue.ItemsSource),
                "背景剔除属性页批处理队列绑定");
            True(ReferenceEquals(shell.BackgroundRemoval.BatchItems, backgroundBatchDataGrid.ItemsSource),
                "背景剔除主视图批处理结果队列绑定");
            True(ReferenceEquals(shell.BackgroundRemoval.ProcessBatchCommand, backgroundBatchProcessButton.Command),
                "背景剔除批量执行按钮绑定");
            Equal(BitmapScalingMode.HighQuality, checkerPreview.BitmapScalingMode,
                "背景剔除源图预览缩放质量");
            Equal(BitmapScalingMode.HighQuality, backgroundOutputPreview.BitmapScalingMode,
                "背景剔除结果预览缩放质量");
            True(backgroundRemovalView.FindName("BackgroundRemovalPropertiesFakeTransparencyCheckBox") is null,
                "背景剔除主视图不应重复承载属性操作控件");

            shell.ShowPanelCommand.Execute(MainWindow.PixelAlignmentContentId);
            var pixelAlignment = FindDockContent(manager, MainWindow.PixelAlignmentContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到像素点对齐工具面板。");
            var pixelAlignmentHost = pixelAlignment.Content as ContentControl
                ?? throw new SmokeTestException("像素点对齐工具缺少稳定 ContentControl host。");
            var pixelAlignmentView = pixelAlignmentHost.Content as PixelAlignmentView
                ?? throw new SmokeTestException("像素点对齐工具 host 未承载 PixelAlignmentView。");
            var pixelAlignmentPropertiesView = propertiesHost.Content as PixelAlignmentPropertiesView
                ?? throw new SmokeTestException("激活像素点对齐工具后属性栏未切换为 PixelAlignmentPropertiesView。");
            True(ReferenceEquals(shell.PixelAlignment, pixelAlignmentView.DataContext),
                "像素点对齐主视图应绑定 Shell 持有的单实例 ViewModel");
            True(ReferenceEquals(shell.PixelAlignment, pixelAlignmentPropertiesView.DataContext),
                "像素点对齐属性页与预览主视图必须共享同一个 ViewModel");
            var pixelSurface = RequireNamedElement<PixelAlignmentSurface>(
                pixelAlignmentView,
                "PixelAlignmentSurface",
                "像素点对齐主视图缺少源图标尺预览。");
            var pixelOutputPreview = RequireNamedElement<ImagePreviewControl>(
                pixelAlignmentView,
                "PixelAlignmentOutputPreview",
                "像素点对齐主视图缺少通用结果预览。");
            var pixelChooseInputButton = RequireNamedElement<Button>(
                pixelAlignmentPropertiesView,
                "PixelAlignmentPropertiesChooseInputButton",
                "像素点对齐属性页缺少选择图片按钮。");
            var pixelRefreshPreviewButton = RequireNamedElement<Button>(
                pixelAlignmentPropertiesView,
                "PixelAlignmentPropertiesRefreshPreviewButton",
                "像素点对齐属性页缺少刷新预览按钮。");
            var pixelSaveButton = RequireNamedElement<Button>(
                pixelAlignmentPropertiesView,
                "PixelAlignmentPropertiesSaveResultButton",
                "像素点对齐属性页缺少保存按钮。");
            var pixelBatchQueue = RequireNamedElement<ListBox>(
                pixelAlignmentPropertiesView,
                "PixelAlignmentPropertiesBatchQueue",
                "像素点对齐属性页缺少批处理队列。");
            var pixelBatchProcessButton = RequireNamedElement<Button>(
                pixelAlignmentPropertiesView,
                "PixelAlignmentPropertiesProcessBatchButton",
                "像素点对齐属性页缺少批量执行按钮。");
            True(pixelSurface is ImageViewportSurface,
                "像素点对齐源图必须复用通用图像视口导航基类");
            Equal(BitmapScalingMode.NearestNeighbor, pixelOutputPreview.BitmapScalingMode,
                "像素点对齐结果预览必须使用最近邻缩放");
            True(ReferenceEquals(shell.PixelAlignment.ChooseInputCommand, pixelChooseInputButton.Command),
                "像素点对齐属性页选择图片命令绑定");
            True(ReferenceEquals(shell.PixelAlignment.RefreshPreviewCommand, pixelRefreshPreviewButton.Command),
                "像素点对齐属性页刷新预览命令绑定");
            True(ReferenceEquals(shell.PixelAlignment.SaveResultCommand, pixelSaveButton.Command),
                "像素点对齐属性页保存结果命令绑定");
            True(ReferenceEquals(shell.PixelAlignment.BatchItems, pixelBatchQueue.ItemsSource),
                "像素点对齐属性页批处理队列绑定");
            True(ReferenceEquals(shell.PixelAlignment.ProcessBatchCommand, pixelBatchProcessButton.Command),
                "像素点对齐属性页批量执行命令绑定");
            True(pixelAlignmentView.FindName("ChoosePixelAlignmentInputButton") is null,
                "像素点对齐主视图不应保留文件操作按钮");
            True(pixelAlignmentView.FindName("PixelAlignmentBatchQueueCard") is null,
                "像素点对齐主视图不应保留批处理操作队列");
            True(pixelAlignmentView.FindName("OpenPixelAlignmentOutputButton") is null,
                "像素点对齐主视图不应保留输出目录操作按钮");

            var commandConsole = FindDockContent(manager, MainWindow.CommandConsoleContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("停靠交互测试未找到指令日志面板。");
            True(commandConsole.IsVisible && !commandConsole.IsHidden,
                "指令日志面板初始应作为 Project 同区标签页存在");
            commandConsoleMenuItem.Command!.Execute(commandConsoleMenuItem.CommandParameter);
            True(commandConsole.IsHidden, "从视图菜单关闭后指令日志面板应隐藏");
            False(commandConsoleMenuItem.IsChecked,
                "指令日志面板隐藏后视图菜单应取消勾选");
            commandConsoleMenuItem.Command.Execute(commandConsoleMenuItem.CommandParameter);
            False(commandConsole.IsHidden, "再次点击视图菜单应恢复指令日志面板");
            True(commandConsole.IsVisible && commandConsole.IsSelected && commandConsole.IsActive,
                "重新开启后指令日志面板应可见、选中并激活");
            True(commandConsoleMenuItem.IsChecked,
                "指令日志面板重新开启后视图菜单应恢复勾选");

            var tools = (LayoutAnchorable)FindDockContent(manager, MainWindow.ToolsContentId)!;
            tools.DockAsDocument();
            PumpDispatcher();
            True(tools.Parent is LayoutDocumentPane,
                "工具页执行 DockAsDocument 后应进入中央 LayoutDocumentPane");
            True(tools.IsVisible && tools.IsSelected,
                "工具页停靠中央后应保持可见并成为当前标签");
            tools.Dock();
            PumpDispatcher();
            True(tools.Parent is LayoutAnchorablePane,
                "工具页应能从中央文档标签区恢复到工具停靠区");

            tools.Float();
            PumpDispatcher();
            True(tools.IsFloating, "工具面板执行 Float 后应进入浮动布局");
            True(manager.Layout.FloatingWindows.Any(windowModel =>
                    windowModel.Descendents().Any(element => ReferenceEquals(element, tools))),
                "浮动窗口集合应包含工具面板");
            tools.Dock();
            PumpDispatcher();
            False(tools.IsFloating, "工具面板测试结束前应恢复停靠，避免遗留浮动 Window");

            var canvas = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
                ?? throw new SmokeTestException("ui-canvas 必须是 LayoutDocument。");
            var canvasHost = canvas.Content;
            True(canvas.CanClose, "主界面应允许通过文档标签关闭");
            canvas.Close();
            PumpDispatcher();
            True(FindDockContent(manager, MainWindow.CanvasContentId) is null,
                "关闭主界面后布局中不应残留 ui-canvas 文档模型");
            var documentPane = manager.Layout.Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault(pane => !pane.IsHostedInFloatingWindow);
            True(documentPane is not null,
                "关闭页面 UI 文档后中央仍应保留可接收任意标签页的 LayoutDocumentPane");
            Equal(3, documentPane!.ChildrenCount,
                "关闭一个业务窗口不应销毁其它 Tilemap、卡牌和平行透视文档");

            shell.ShowPanelCommand.Execute(MainWindow.CanvasContentId);
            var restoredCanvas = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
                ?? throw new SmokeTestException("视图菜单未重新创建 ui-canvas LayoutDocument。");
            False(ReferenceEquals(canvas, restoredCanvas),
                "关闭后的主界面应创建新的 LayoutDocument 模型");
            True(ReferenceEquals(canvasHost, restoredCanvas.Content),
                "恢复主界面时应复用原有工作区 host，避免业务状态丢失");
            True(restoredCanvas.CanClose && restoredCanvas.CanFloat && restoredCanvas.CanMove,
                "恢复后的主界面仍应可关闭、浮动与拖动");
            True(ReferenceEquals(restoredCanvas.Parent, documentPane),
                "恢复后的主界面应重新插入原中央业务文档区");
            True(restoredCanvas.IsSelected, "主界面经视图菜单恢复后应被选中");
            True(restoredCanvas.IsActive, "主界面经视图菜单恢复后应被激活");
            True(ReferenceEquals(manager.Layout.ActiveContent, restoredCanvas),
                "主界面应成为当前活动内容");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void BlueprintDockPanelProvidesWorkflowCanvas()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "blueprint-dock-panel"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        shell.SelectWorkflow("tilemap");
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("蓝图窗口测试未找到 DockManager。");
            var blueprint = FindDockContent(manager, MainWindow.BlueprintContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("默认布局缺少蓝图窗口。");
            var project = FindDockContent(manager, MainWindow.ProjectContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("蓝图窗口测试缺少项目面板。");

            Equal("蓝图窗口", blueprint.Title, "蓝图窗口停靠标题");
            True(ReferenceEquals(blueprint.Parent, project.Parent),
                "蓝图窗口初始应与项目面板处于同一底部标签区");
            True(blueprint.CanDockAsTabbedDocument,
                "蓝图窗口应允许拖入中央文档标签区");

            blueprint.IsSelected = true;
            blueprint.IsActive = true;
            PumpDispatcher();
            window.UpdateLayout();

            var host = blueprint.Content as ContentControl
                ?? throw new SmokeTestException("蓝图窗口缺少稳定 ContentControl host。");
            var view = host.Content as BlueprintView
                ?? throw new SmokeTestException("蓝图窗口未承载 BlueprintView。");
            True(ReferenceEquals(shell.Blueprint, view.DataContext),
                "BlueprintView 应绑定 Shell 持有的单实例 BlueprintViewModel");
            var blueprintCanvas = view.FindName("BlueprintCanvas") as Grid
                ?? throw new SmokeTestException("蓝图窗口缺少根画布。");
            var blueprintWorldCanvas = view.FindName("BlueprintWorldCanvas") as Canvas
                ?? throw new SmokeTestException("蓝图窗口缺少世界画布。");
            True(
                 view.FindName("BlueprintPlacementGhost") is Grid &&
                 view.FindName("BlueprintConnectionPreviewPath") is System.Windows.Shapes.Path,
                "蓝图窗口应提供支持视口变换、节点预放置与临时连线的真实画布");
            view.UpdateLayout();
            var nodeHeader = VisualDescendants<Border>(view)
                .FirstOrDefault(element => element.Name == "NodeDragHandle")
                ?? throw new SmokeTestException("蓝图节点未呈现彩色标题栏。");
            var nodeHeaderTitle = VisualDescendants<TextBox>(nodeHeader)
                .FirstOrDefault(element => element.Name == "NodeHeaderTitleEditor")
                ?? throw new SmokeTestException("蓝图节点标题栏缺少标题编辑器。");
            var nodeHeaderType = VisualDescendants<TextBlock>(nodeHeader)
                .FirstOrDefault(element => element.Name == "NodeHeaderTypeLabel")
                ?? throw new SmokeTestException("蓝图节点标题栏缺少类型说明。");
            var nodeHeaderTextStack = VisualDescendants<StackPanel>(nodeHeader)
                .FirstOrDefault(element => element.Name == "NodeHeaderTextStack")
                ?? throw new SmokeTestException("蓝图节点标题栏缺少文字布局容器。");
            var titleBounds = nodeHeaderTitle.TransformToAncestor(nodeHeader).TransformBounds(
                new Rect(0d, 0d, nodeHeaderTitle.ActualWidth, nodeHeaderTitle.ActualHeight));
            var typeBounds = nodeHeaderType.TransformToAncestor(nodeHeader).TransformBounds(
                new Rect(0d, 0d, nodeHeaderType.ActualWidth, nodeHeaderType.ActualHeight));
            var headerContentBounds = new Rect(
                nodeHeader.Padding.Left,
                nodeHeader.Padding.Top,
                Math.Max(0d, nodeHeader.ActualWidth - nodeHeader.Padding.Left - nodeHeader.Padding.Right),
                Math.Max(0d, nodeHeader.ActualHeight - nodeHeader.Padding.Top - nodeHeader.Padding.Bottom));
            True(PresentationSource.FromVisual(nodeHeader) is not null && nodeHeader.IsVisible,
                "蓝图节点标题栏回归测试必须检查已接入真实窗口的可见元素");
            True(Math.Abs(nodeHeader.ActualHeight - BlueprintNodeLayoutMetrics.HeaderHeight) <= 0.1d,
                "蓝图节点彩色标题栏必须使用统一布局高度");
            True(nodeHeaderTitle.ActualWidth > 0d && nodeHeaderTitle.ActualHeight > 0d &&
                 nodeHeaderType.ActualWidth > 0d && nodeHeaderType.ActualHeight > 0d,
                "蓝图节点标题和类型说明必须完成真实布局");
            True(titleBounds.Left >= headerContentBounds.Left - 0.1d &&
                 titleBounds.Top >= headerContentBounds.Top - 0.1d &&
                 titleBounds.Right <= headerContentBounds.Right + 0.1d &&
                 titleBounds.Bottom <= headerContentBounds.Bottom + 0.1d,
                "蓝图节点标题必须完整位于彩色标题栏内");
            True(typeBounds.Left >= headerContentBounds.Left - 0.1d &&
                 typeBounds.Top >= headerContentBounds.Top - 0.1d &&
                 typeBounds.Right <= headerContentBounds.Right + 0.1d &&
                 typeBounds.Bottom <= headerContentBounds.Bottom + 0.1d,
                "蓝图节点类型说明必须完整位于彩色标题栏内，不能被下一行遮挡");
            True(titleBounds.Bottom <= typeBounds.Top + 0.1d,
                "蓝图节点标题和类型说明不得互相覆盖");
            True(VisualTreeHelper.GetClip(nodeHeaderTextStack) is null,
                "蓝图节点标题文字容器不得因高度不足而被裁剪");
            var interactiveSourceClassifier = typeof(BlueprintView).GetMethod(
                "IsInteractiveSource",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new SmokeTestException("蓝图窗口缺少交互源分类器。");
            False(
                (bool)(interactiveSourceClassifier.Invoke(view, [blueprintWorldCanvas]) ?? true),
                "蓝图世界画布不得因 AvalonDock 外层控件而被误判为输入控件，否则添加左键和右键平移会同时失效");
            True(blueprintCanvas.ActualWidth > 80d && blueprintCanvas.ActualHeight > 80d,
                "蓝图输入回归测试必须运行在真实可见画布上");
            var emptyCanvasPoint = new Point(
                Math.Max(1d, blueprintCanvas.ActualWidth - 24d),
                Math.Max(1d, blueprintCanvas.ActualHeight - 24d));
            var emptyCanvasHit = VisualTreeHelper.HitTest(blueprintCanvas, emptyCanvasPoint)?.VisualHit
                ?? throw new SmokeTestException("蓝图可见空白区域未产生视觉命中。");
            False(
                (bool)(interactiveSourceClassifier.Invoke(view, [emptyCanvasHit]) ?? true),
                "蓝图空白区域的真实命中目标不得被 AvalonDock 容器误判为输入控件");
            var interactiveButton = VisualDescendants<ButtonBase>(view).FirstOrDefault()
                ?? throw new SmokeTestException("蓝图输入回归测试缺少真实按钮控件。");
            True(
                (bool)(interactiveSourceClassifier.Invoke(view, [interactiveButton]) ?? false),
                "蓝图画布内的真实按钮仍必须被识别为输入控件");
            var emptyCanvasInputTarget = emptyCanvasHit as UIElement;
            for (var ancestor = emptyCanvasHit;
                 emptyCanvasInputTarget is null && ancestor is not null;
                 ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                emptyCanvasInputTarget = ancestor as UIElement;
            }
            if (emptyCanvasInputTarget is null)
            {
                throw new SmokeTestException("蓝图空白命中目标不能接收 WPF 输入事件。");
            }

            void RaiseCanvasMouseButton(RoutedEvent routedEvent, MouseButton button)
            {
                var eventArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
                {
                    RoutedEvent = routedEvent,
                    Source = emptyCanvasInputTarget
                };
                emptyCanvasInputTarget.RaiseEvent(eventArgs);
                PumpDispatcher();
            }

            var blueprintViewModel = shell.Blueprint;
            Equal(1, blueprintViewModel.Nodes.Count(node => node.IsStart),
                "每个工作流默认应创建一个系统起始蓝图");
            True(blueprintViewModel.Nodes.Single(node => node.IsStart).InputPorts.Count == 0,
                "系统起始蓝图不能拥有输入端点");

            blueprintViewModel.SelectedAddNodeType = blueprintViewModel.AddNodeTypes.Single(option =>
                option.Type == BlueprintNodeType.Description);
            blueprintViewModel.SelectToolCommand.Execute("add");
            var nodeIdsBeforeCanvasAdd = blueprintViewModel.Nodes
                .Select(node => node.Id)
                .ToHashSet(StringComparer.Ordinal);
            RaiseCanvasMouseButton(Mouse.PreviewMouseDownEvent, MouseButton.Left);
            True(blueprintViewModel.HasPlacementPreview,
                "添加工具在真实 AvalonDock 画布首次左键后应进入虚影预览状态");
            Equal(nodeIdsBeforeCanvasAdd.Count, blueprintViewModel.Nodes.Count,
                "添加工具首次左键不得提前创建节点");
            RaiseCanvasMouseButton(Mouse.PreviewMouseDownEvent, MouseButton.Left);
            True(!blueprintViewModel.HasPlacementPreview,
                "添加工具在真实 AvalonDock 画布第二次左键后应提交虚影");
            Equal(nodeIdsBeforeCanvasAdd.Count + 1, blueprintViewModel.Nodes.Count,
                "添加工具第二次左键应真实创建一个蓝图节点");
            var canvasAddedNode = blueprintViewModel.Nodes.Single(node => !nodeIdsBeforeCanvasAdd.Contains(node.Id));
            blueprintViewModel.RemoveNode(canvasAddedNode);

            blueprintViewModel.SelectToolCommand.Execute("pointer");
            True(blueprintViewModel.IsPointerTool,
                "默认鼠标工具应能在真实 AvalonDock 蓝图页激活");
            False(
                (bool)(interactiveSourceClassifier.Invoke(view, [emptyCanvasHit]) ?? true),
                "默认鼠标右键平移入口不得再次被 AvalonDock 外层 Selector 拦截");

            blueprintViewModel.SelectedAddNodeType = blueprintViewModel.AddNodeTypes.Single(option =>
                option.Type == BlueprintNodeType.Description);
            var sourceNode = blueprintViewModel.AddNode(720d, 180d);
            sourceNode.AddOutputPortCommand.Execute(null);
            sourceNode.AddOutputPortCommand.Execute(null);
            var targetNode = blueprintViewModel.AddNode(1_120d, 240d);
            blueprintViewModel.SelectToolCommand.Execute("connect");
            blueprintViewModel.HandlePortClicked(sourceNode.OutputPorts[2]);
            blueprintViewModel.HandlePortClicked(targetNode.InputPorts[0]);
            var visualOrderConnection = blueprintViewModel.Connections.Single(connection =>
                string.Equals(connection.Model.SourceNodeId, sourceNode.Id, StringComparison.Ordinal) &&
                string.Equals(connection.Model.TargetNodeId, targetNode.Id, StringComparison.Ordinal));
            var sourceYBeforePortRemoval = ((PathGeometry)visualOrderConnection.Geometry).Figures[0].StartPoint.Y;
            var stablePortOrder = sourceNode.OutputPorts[2].Order;
            sourceNode.OutputPorts[1].RemoveCommand.Execute(null);
            Equal(stablePortOrder, sourceNode.OutputPorts[1].Order,
                "删除中间端点后必须保留剩余端点的稳定 Order");
            var sourceYAfterPortRemoval = ((PathGeometry)visualOrderConnection.Geometry).Figures[0].StartPoint.Y;
            Equal(sourceYBeforePortRemoval - 27d, sourceYAfterPortRemoval,
                "连线几何必须使用连续视觉索引，不能使用删除后稀疏的稳定 Order");

            var menuItem = window.FindName("ShowBlueprintMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少蓝图窗口入口。");
            menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(menuItem.IsCheckable, "蓝图窗口视图入口应可显示开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, menuItem.Command),
                "蓝图窗口视图入口应复用 TogglePanelCommand");
            Equal(MainWindow.BlueprintContentId, menuItem.CommandParameter as string,
                "蓝图窗口视图入口 ContentId");
            True(menuItem.IsChecked, "蓝图窗口默认可见时视图入口应勾选");

            menuItem.Command!.Execute(menuItem.CommandParameter);
            PumpDispatcher();
            True(blueprint.IsHidden, "点击视图入口后蓝图窗口应隐藏");
            False(menuItem.IsChecked, "蓝图窗口隐藏后视图入口应取消勾选");

            menuItem.Command.Execute(menuItem.CommandParameter);
            PumpDispatcher();
            False(blueprint.IsHidden, "再次点击视图入口应恢复蓝图窗口");
            True(blueprint.IsVisible && blueprint.IsSelected && blueprint.IsActive,
                "恢复后的蓝图窗口应可见、选中并激活");
            True(menuItem.IsChecked, "蓝图窗口恢复后视图入口应重新勾选");

            var tools = FindDockContent(manager, MainWindow.ToolsContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("蓝图动态工具测试缺少工具面板。");
            var toolsHost = tools.Content as ContentControl
                ?? throw new SmokeTestException("工具面板缺少稳定 ContentControl host。");
            var blueprintTools = toolsHost.Content as BlueprintToolPaletteView
                ?? throw new SmokeTestException("蓝图成为活动页面时工具面板未切换为蓝图工具栏。");
            True(ReferenceEquals(shell.Blueprint, blueprintTools.DataContext),
                "蓝图工具栏应绑定当前蓝图 ViewModel");
            foreach (var controlName in new[]
                     {
                         "BlueprintPointerToolButton",
                         "BlueprintAddToolButton",
                         "BlueprintConnectToolButton",
                         "BlueprintMoveNodeToolButton"
                     })
            {
                True(blueprintTools.FindName(controlName) is FrameworkElement,
                    $"蓝图工具栏缺少 {controlName}");
            }
            True(!VisualDescendants<ComboBox>(blueprintTools).Any(),
                "蓝图工具栏不应常驻占用空间的蓝图类型下拉框");

            var moveNodeToolButton = blueprintTools.FindName("BlueprintMoveNodeToolButton") as ToggleButton
                ?? throw new SmokeTestException("蓝图工具栏缺少对象移动按钮。");
            True(VisualDescendants<TextBlock>(moveNodeToolButton)
                    .Any(text => text.Text.Contains("对象移动", StringComparison.Ordinal)),
                "节点拖拽工具的可视文案应明确显示为对象移动");
            True(moveNodeToolButton.ToolTip?.ToString()?.Contains("对象移动", StringComparison.Ordinal) == true,
                "节点拖拽工具提示应明确说明对象移动语义");

            var addToolButton = blueprintTools.FindName("BlueprintAddToolButton") as ToggleButton
                ?? throw new SmokeTestException("蓝图工具栏缺少添加蓝图按钮。");
            var addTypeMenu = addToolButton.ContextMenu
                ?? throw new SmokeTestException("添加蓝图按钮缺少类型右键菜单。");
            addTypeMenu.PlacementTarget = addToolButton;
            try
            {
                addTypeMenu.IsOpen = true;
                PumpDispatcher();
                addTypeMenu.UpdateLayout();
                Equal(shell.Blueprint.AddNodeTypes.Count, addTypeMenu.Items.Count,
                    "添加蓝图右键菜单应呈现全部四种基础类型");
                Equal(4, addTypeMenu.Items.Count,
                    "添加蓝图右键菜单当前应固定包含四种基础类型");
                for (var index = 0; index < shell.Blueprint.AddNodeTypes.Count; index++)
                {
                    var option = shell.Blueprint.AddNodeTypes[index];
                    True(ReferenceEquals(option, addTypeMenu.Items[index]),
                        $"添加蓝图菜单第 {index + 1} 项应绑定对应类型模型");
                    var typeMenuItem = addTypeMenu.ItemContainerGenerator.ContainerFromIndex(index) as MenuItem
                        ?? throw new SmokeTestException($"添加蓝图菜单第 {index + 1} 项未生成 MenuItem 容器。");
                    typeMenuItem.GetBindingExpression(HeaderedItemsControl.HeaderProperty)?.UpdateTarget();
                    Equal(option.Label, typeMenuItem.Header as string,
                        $"添加蓝图菜单第 {index + 1} 项标题");
                }
            }
            finally
            {
                addTypeMenu.IsOpen = false;
                PumpDispatcher();
            }

            tools.IsSelected = true;
            tools.IsActive = true;
            manager.ActiveContent = tools.Content;
            PumpDispatcher();
            True(toolsHost.Content is BlueprintToolPaletteView,
                "激活工具等辅助面板时应保留最近的蓝图工具上下文");
            blueprint.Hide();
            PumpDispatcher();
            False(toolsHost.Content is BlueprintToolPaletteView,
                "蓝图在非活动状态下隐藏时也必须立即恢复业务工具栏");
            blueprint.Show();
            blueprint.IsSelected = true;
            blueprint.IsActive = true;
            manager.ActiveContent = blueprint.Content;
            PumpDispatcher();
            True(toolsHost.Content is BlueprintToolPaletteView,
                "重新显示并激活蓝图后应恢复蓝图工具栏");

            var canvas = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
                ?? throw new SmokeTestException("蓝图动态工具测试缺少主视图文档。");
            canvas.IsSelected = true;
            canvas.IsActive = true;
            manager.ActiveContent = canvas.Content;
            PumpDispatcher();
            False(toolsHost.Content is BlueprintToolPaletteView,
                "离开蓝图主视图后工具栏应恢复当前业务工具内容");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static string CurrentPaletteSource()
    {
        var palette = Application.Current.Resources.MergedDictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains("/Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true ||
            dictionary.Source?.OriginalString.StartsWith("Themes/Palette.", StringComparison.OrdinalIgnoreCase) == true);
        return palette?.Source?.OriginalString
            ?? throw new SmokeTestException("Application 未加载 Palette 资源字典。");
    }

    private static void DockLayoutPersistsFloatingAndHiddenPanels()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "dock-state-roundtrip"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        var layoutService = new AvalonDockLayoutService(paths);
        var window = new MainWindow(shell, layoutService);

        try
        {
            window.Show();
            window.UpdateLayout();
            var sourceManager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("浮动布局往返测试未找到 DockManager。");
            var sourceTools = FindDockContent(sourceManager, MainWindow.ToolsContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("浮动布局往返测试未找到工具面板。");
            var sourceLayers = FindDockContent(sourceManager, MainWindow.LayersContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("浮动布局往返测试未找到图层面板。");
            sourceTools.Float();
            PumpDispatcher();
            sourceLayers.Hide();
            True(sourceTools.IsFloating, "保存前工具面板应处于浮动状态");
            True(sourceLayers.IsHidden, "保存前图层面板应处于隐藏状态");
            layoutService.Save(sourceManager);

            var restoredContent = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MainWindow.ToolsContentId] = NewPanelHost(),
                [MainWindow.CanvasContentId] = NewPanelHost(),
                [MainWindow.TilemapWorkspaceContentId] = NewPanelHost(),
                [MainWindow.CardDesignWorkspaceContentId] = NewPanelHost(),
                [MainWindow.ParallelPerspectiveWorkspaceContentId] = NewPanelHost(),
                [MainWindow.PropertiesContentId] = NewPanelHost(),
                [MainWindow.LayersContentId] = NewPanelHost(),
                [MainWindow.ProjectContentId] = NewPanelHost(),
                [MainWindow.StyleLibraryContentId] = NewPanelHost(),
                [MainWindow.AssetGenerationContentId] = NewPanelHost(),
                [MainWindow.BackgroundRemovalContentId] = NewPanelHost(),
                [MainWindow.ImageOutlineContentId] = NewPanelHost(),
                [MainWindow.IconDesignContentId] = NewPanelHost(),
                [MainWindow.PixelAlignmentContentId] = NewPanelHost(),
                [MainWindow.BlueprintContentId] = NewPanelHost(),
                [MainWindow.AgentConnectionContentId] = NewPanelHost(),
                [MainWindow.CommandConsoleContentId] = NewPanelHost()
            };
            var restoredManager = new DockingManager();
            True(layoutService.TryRestore(restoredManager, restoredContent),
                "包含浮动与隐藏面板的有效布局应能恢复");

            var restoredTools = FindDockContent(restoredManager, MainWindow.ToolsContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("恢复后缺少工具面板。");
            var restoredLayers = FindDockContent(restoredManager, MainWindow.LayersContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("恢复后缺少图层面板。");
            True(restoredTools.IsFloating, "恢复后工具面板应保持浮动状态");
            True(restoredLayers.IsHidden, "恢复后图层面板应保持隐藏状态");

            foreach (var (contentId, expectedContent) in restoredContent)
            {
                var restored = FindDockContent(restoredManager, contentId)
                    ?? throw new SmokeTestException($"恢复后缺少 {contentId}。");
                True(ReferenceEquals(expectedContent, restored.Content),
                    $"恢复后的 {contentId} 应绑定调用方提供的面板实例");
            }

            sourceTools.Dock();
            PumpDispatcher();
            False(sourceTools.IsFloating, "浮动布局测试结束前应清理真实浮动 Window");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void BusinessWorkspaceContextsSwitchAndGateUiCommands()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "workflow-gating"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        if (shell.IsNewProjectDialogOpen)
        {
            shell.CloseNewProjectCommand.Execute(null);
        }
        shell.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(ShellViewModel.CurrentWorkflow)
                or nameof(ShellViewModel.CurrentWorkspace)
                or nameof(ShellViewModel.PlaceholderWorkspace))
            {
                AssertWorkspaceStateIsConsistent(shell, eventArgs.PropertyName);
            }
        };

        try
        {
            Equal(4, shell.Workflows.Count, "正式业务窗口数量");
            Equal(4, shell.Workflows.Select(workflow => workflow.Key).Distinct(StringComparer.Ordinal).Count(),
                "业务工作流 Key 应稳定且唯一");
            True(shell.Workflows.All(workflow => workflow.Kind != WorkflowKind.Test),
                "测试模板不得注册为正式业务窗口");

            foreach (var workflow in shell.Workflows)
            {
                shell.SelectWorkflowCommand.Execute(workflow);
                Equal(workflow, shell.CurrentWorkflow, $"切换工作流 {workflow.Key}");

                if (workflow.Kind == WorkflowKind.UiDesign)
                {
                    True(shell.IsUiWorkspace, "UI 工作流应进入 EditorWorkspace");
                    Equal(editor, shell.CurrentWorkspace, "UI 工作区实例");
                    True(shell.UiSaveCommand.CanExecute(null), "UI 工作流应允许保存命令");
                    True(shell.UiZoomInCommand.CanExecute(null), "UI 工作流应允许缩放命令");

                    shell.OpenNewProjectCommand.Execute(null);
                    False(shell.UiSaveCommand.CanExecute(null), "新建项目页打开时应暂停 UI 命令");
                    shell.SelectWorkflow(workflow);
                    False(shell.UiSaveCommand.CanExecute(null),
                        "激活业务窗口不应隐式关闭新建项目页");
                    shell.CloseNewProjectCommand.Execute(null);
                    True(shell.UiSaveCommand.CanExecute(null), "关闭新建项目页后应恢复 UI 命令");
                }
                else if (workflow.Kind == WorkflowKind.Tilemap)
                {
                    True(shell.IsTilemapWorkspace, "Tilemap 工作流应进入 TilemapWorkspace");
                    False(shell.IsPlaceholderWorkspace, "Tilemap 工作流不应再进入占位工作区");
                    Equal(shell.TilemapWorkspace, shell.CurrentWorkspace, "Tilemap 工作区实例");
                    Equal<WorkflowPlaceholderViewModel?>(null, shell.PlaceholderWorkspace,
                        "Tilemap 工作流不应创建占位模型");
                    True(workflow.IsImplemented, "Tilemap 工作流应标记为已接入基础网格");
                    True(shell.WorkspaceStatusText.Contains("512 × 256", StringComparison.Ordinal) &&
                         shell.WorkspaceStatusText.Contains("A/B 边夹角", StringComparison.Ordinal),
                        "Tilemap 状态栏应呈现默认美术尺寸与无歧义的边夹角");
                    False(shell.UiSaveCommand.CanExecute(null), "Tilemap 不得执行 UI 保存命令");
                    False(shell.UiImportCommand.CanExecute(null), "Tilemap 不得执行 UI 导入命令");
                    False(shell.UiZoomInCommand.CanExecute(null), "Tilemap 使用自己的网格缩放控件");
                }
                else
                {
                    True(shell.IsPlaceholderWorkspace, $"{workflow.Key} 应进入占位工作区");
                    True(shell.PlaceholderWorkspace is not null, $"{workflow.Key} 应创建占位工作区模型");
                    True(editor.SaveCommand.CanExecute(null), "被门控命令的底层保存命令应可执行");
                    False(shell.UiSaveCommand.CanExecute(null), $"{workflow.Key} 不得执行 UI 保存命令");
                    False(shell.UiImportCommand.CanExecute(null), $"{workflow.Key} 不得执行 UI 导入命令");
                    False(shell.UiZoomInCommand.CanExecute(null), $"{workflow.Key} 不得执行 UI 缩放命令");
                }
            }
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void NewProjectFlowCreatesIsolatedUnityStyleWorkspace()
    {
        var testRoot = Path.Combine(_temporaryRoot, "new-project-flow");
        var paths = new WorkspacePaths(Path.Combine(testRoot, "app-data"));
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "ui",
            CodexThreadId = "thread-before-project-switch",
            CodexThreadWorkingDirectory = paths.AssetDirectory
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));
        var projectsParent = Path.Combine(testRoot, "Projects");
        Directory.CreateDirectory(projectsParent);

        try
        {
            var fileMenu = RequireNamedElement<MenuItem>(
                window,
                "FileMenu",
                "顶部菜单缺少文件入口。");
            var newProjectMenuItem = RequireNamedElement<MenuItem>(
                window,
                "NewProjectMenuItem",
                "文件菜单缺少新建项目入口。");
            Equal("新建项目…", newProjectMenuItem.Header?.ToString(), "新建项目菜单标题");
            True(newProjectMenuItem.GetBindingExpression(MenuItem.CommandProperty) is not null,
                "文件菜单新建项目入口应绑定 OpenNewProjectCommand");
            var topMenu = ((Grid)window.Content).Children.OfType<Menu>().Single();
            False(topMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Header?.ToString()?.Contains("业务模板", StringComparison.Ordinal) == true),
                "顶部菜单不得继续显示旧的业务模板入口");
            True(fileMenu.Items.Contains(newProjectMenuItem), "新建项目必须位于文件菜单内");
            _ = RequireNamedElement<StackPanel>(
                window,
                "CurrentProjectIndicator",
                "顶部旧工作流按钮应替换为不可点击的项目指示器。");

            shell.OpenNewProjectCommand.Execute(null);
            True(shell.IsNewProjectDialogOpen, "文件菜单应打开新建项目页");
            var previousWorkflow = shell.CurrentWorkflow;
            shell.SelectedNewProjectWorkflow = WorkflowCatalog.Find("tilemap");
            Equal(previousWorkflow, shell.CurrentWorkflow,
                "在新建项目页选择模板时不得提前切换业务主视图");
            True(shell.IsNewProjectDialogOpen, "选择模板后新建项目页应保持打开");

            shell.NewProjectParentPath = projectsParent;
            shell.NewProjectName = "Tilemap Demo";
            True(shell.CreateNewProjectCommand.CanExecute(null),
                $"合法项目输入应允许创建：{shell.NewProjectValidationMessage}");
            shell.AssetGeneration.ReportDispatchQueued(
                "project-switch-dispatch-fixture",
                "模拟旧项目仍有生成批次正在调度");
            True(shell.AssetGeneration.IsDispatching,
                "项目切换回归测试应先进入素材生成调度态");
            shell.CreateNewProjectCommand.Execute(null);
            WaitUntilWithDispatcher(
                () => !shell.IsCreatingNewProject,
                TimeSpan.FromSeconds(8),
                "新建项目异步切换未在预期时间内完成");

            False(shell.AssetGeneration.IsDispatching,
                "项目切换取消旧 Coordinator 后必须解除素材生成调度锁");
            True(shell.AssetGeneration.StatusText.Contains("项目切换", StringComparison.Ordinal) &&
                 shell.AssetGeneration.StatusText.Contains("Pending", StringComparison.Ordinal),
                "项目切换后素材生成状态应明确说明旧调度取消且未完成任务保留为 Pending");
            True(shell.CommandConsole.Entries.Any(entry =>
                    entry.Level == WorkbenchLogLevel.Warning &&
                    string.Equals(entry.Source, "Generation", StringComparison.Ordinal) &&
                    entry.Message.Contains("项目切换", StringComparison.Ordinal) &&
                    entry.Message.Contains("Pending", StringComparison.Ordinal)),
                "项目切换取消生成调度后应写入可审计的控制台警告日志");

            var projectRoot = Path.Combine(projectsParent, "Tilemap Demo");
            var projectPaths = new ProjectPaths(projectRoot);
            False(shell.IsNewProjectDialogOpen, "项目创建成功后应关闭新建项目页");
            True(AssetRootService.PathEquals(projectRoot, shell.CurrentProjectPath!),
                "Shell 当前项目路径应切换到新目录");
            Equal("Tilemap Demo", shell.CurrentProjectDisplayName, "顶部当前项目名称");
            Equal("tilemap", shell.CurrentWorkflow.Key, "创建项目后应激活所选 Tilemap 业务窗口");
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("新建项目模板回归未找到 DockManager。");
            var tilemapDocument = FindDockContent(manager, MainWindow.TilemapWorkspaceContentId) as LayoutDocument
                ?? throw new SmokeTestException("创建 Tilemap 项目后缺少 Tilemap 业务窗口。");
            True(tilemapDocument.IsSelected && tilemapDocument.IsActive,
                "新建项目模板只应帮助打开并聚焦对应业务窗口");
            True(FindDockContent(manager, MainWindow.CanvasContentId) is LayoutDocument &&
                 FindDockContent(manager, MainWindow.CardDesignWorkspaceContentId) is LayoutDocument &&
                 FindDockContent(manager, MainWindow.ParallelPerspectiveWorkspaceContentId) is LayoutDocument,
                "创建项目不得替换或销毁其它业务窗口");
            True(Directory.Exists(projectPaths.AssetDirectory), "新项目应创建 Assets");
            True(Directory.Exists(projectPaths.GeneratedAssetDirectory), "新项目应创建 Assets/Generated");
            True(Directory.Exists(projectPaths.PackagesDirectory), "新项目应创建 Packages");
            True(Directory.Exists(projectPaths.ProjectSettingsDirectory), "新项目应创建 ProjectSettings");
            True(File.Exists(projectPaths.ManifestPath), "新项目应创建项目 manifest");
            True(File.Exists(projectPaths.UiProjectPath), "新项目应创建独立 UI 项目文件");
            if (File.Exists(paths.AutosaveProjectPath))
            {
                var preservedLegacyProject = ProjectJson.Deserialize(File.ReadAllText(paths.AutosaveProjectPath));
                False(string.Equals(preservedLegacyProject.Id, editor.Project.Id, StringComparison.Ordinal),
                    "全局 default.pfui.json 只能保留切换前的旧工作区，不得混入新项目 UI 数据");
            }
            True(AssetRootService.PathEquals(projectPaths.AssetDirectory, shell.ProjectBrowser.RootPath),
                "Project 区根应固定为当前项目 Assets");
            True(AssetRootService.PathEquals(projectPaths.GeneratedAssetDirectory, shell.ProjectBrowser.CurrentPath),
                "Project 区默认当前目录应为 Assets/Generated");
            True(shell.ProjectBrowser.IsRootSelectionLocked,
                "正式项目激活后不得从 Project 区跳出项目 Assets");
            False(shell.ProjectBrowser.TrySetRootPath(paths.AssetDirectory),
                "正式项目激活后应拒绝把 Assets 根切换到项目外");
            True(AssetRootService.PathEquals(projectRoot, editor.ProjectRoot!),
                "UI 编辑器仓库应切换到项目目录");
            Equal(projectPaths.UiProjectPath, editor.ProjectFilePath,
                "UI 编辑器应保存到项目内 ProjectSettings");

            var persistedSettings = settings.Load();
            True(AssetRootService.PathEquals(projectRoot, persistedSettings.LastProjectPath!),
                "用户设置应保存最近项目根");
            True(AssetRootService.PathEquals(projectPaths.AssetDirectory, persistedSettings.AssetRootPath!),
                "用户设置应保存当前项目 Assets");
            Equal("tilemap", persistedSettings.ActiveWorkflowKey,
                "用户设置应保存项目业务模板");
            Equal<string?>(null, persistedSettings.CodexThreadId,
                "切换到新项目后不得恢复旧项目 Codex thread");
            Equal<string?>(null, persistedSettings.CodexThreadWorkingDirectory,
                "切换到新项目后应清除旧 thread 工作目录");

            var projectService = new ProjectWorkspaceService();
            True(projectService.TryLoad(projectRoot, out var manifest, out _, out var loadReason),
                $"新项目 manifest 应可重新加载：{loadReason}");
            Equal("tilemap", manifest.WorkflowKey, "manifest 业务模板");
            var uiDocument = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
                ?? throw new SmokeTestException("新建项目模板回归缺少页面 UI 业务窗口。");
            uiDocument.IsSelected = true;
            uiDocument.IsActive = true;
            manager.ActiveContent = uiDocument.Content;
            PumpDispatcher();
            Equal("ui", shell.CurrentWorkflow.Key,
                "激活页面 UI 标签只应切换当前窗口上下文");
            True(AssetRootService.PathEquals(projectRoot, shell.CurrentProjectPath!),
                "切换业务窗口不得替换当前项目环境");
            True(shell.Blueprint.PersistenceText.Contains("tilemap.gafgraph.json", StringComparison.OrdinalIgnoreCase),
                "切换业务窗口不得替换项目级蓝图存储上下文");
            True(projectService.TryLoad(projectRoot, out var unchangedTemplateManifest, out _, out loadReason),
                $"切换业务窗口后的 manifest 应可加载：{loadReason}");
            Equal("tilemap", unchangedTemplateManifest.WorkflowKey,
                "激活其它业务窗口不得改写项目初始模板");
            Equal("tilemap", settings.Load().ActiveWorkflowKey,
                "普通标签切换不得覆盖新建项目选择的初始模板设置");
            shell.TilemapWorkspace.GridUnitSize = 96d;
            True(projectService.TryLoad(projectRoot, out var updatedManifest, out _, out loadReason),
                $"更新 Tilemap 参数后的 manifest 应可加载：{loadReason}");
            Near(96d, updatedManifest.Tilemap.GridUnitSize, 0.000001,
                "Tilemap 参数应按当前项目持久化");
            Equal("tilemap", updatedManifest.WorkflowKey,
                "保存 Tilemap 参数不得用当前活动标签污染项目初始模板");

            Throws<ArgumentException>(
                () => projectService.Create(projectsParent, "CON", "ui"),
                "新建项目必须拒绝 Windows 保留设备名");
            Throws<IOException>(
                () => projectService.Create(projectsParent, "Tilemap Demo", "ui"),
                "新建项目必须拒绝覆盖已有目标目录");
            False(Directory.EnumerateDirectories(projectsParent, "*.creating").Any(),
                "创建失败不得残留 staging 目录");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }

        var restoredEditor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var restoredShell = new ShellViewModel(new ThemeService(), settings, restoredEditor);
        try
        {
            var expectedRoot = Path.Combine(projectsParent, "Tilemap Demo");
            True(AssetRootService.PathEquals(expectedRoot, restoredShell.CurrentProjectPath!),
                "重启后应通过 lastProjectPath 恢复当前项目");
            Equal("tilemap", restoredShell.CurrentWorkflow.Key,
                "重启后应恢复项目业务主视图");
            Near(96d, restoredShell.TilemapWorkspace.GridUnitSize, 0.000001,
                "重启后应恢复项目级 Tilemap 参数");
            False(restoredShell.IsNewProjectDialogOpen,
                "有效最近项目恢复后不应强制打开新建项目页");
        }
        finally
        {
            restoredShell.Shutdown();
        }
    }

    private static void ProjectWorkspaceRecoversBackupsAndKeepsLockedAssetBoundary()
    {
        var testRoot = Path.Combine(_temporaryRoot, "project-recovery-boundary");
        var appPaths = new WorkspacePaths(Path.Combine(testRoot, "app-data"));
        var projectsParent = Path.Combine(testRoot, "Projects");
        Directory.CreateDirectory(projectsParent);

        var service = new ProjectWorkspaceService();
        var workspace = service.Create(projectsParent, "Recovery Project", "tilemap");
        File.Copy(workspace.Paths.ManifestPath, workspace.Paths.ManifestBackupPath, overwrite: true);
        File.WriteAllText(workspace.Paths.ManifestPath, "{\"schemaVersion\":1,\"id\":null}");
        True(service.TryLoad(
                workspace.Paths.RootDirectory,
                out var recoveredManifest,
                out _,
                out var recoveryReason),
            $"损坏的 manifest 主文件应回退有效备份：{recoveryReason}");
        Equal(workspace.Manifest.Id, recoveredManifest.Id, "manifest 备份恢复项目身份");

        File.Copy(workspace.Paths.UiProjectPath, workspace.Paths.UiProjectBackupPath, overwrite: true);
        File.WriteAllText(workspace.Paths.UiProjectPath, "{\"schemaVersion\":2,\"documentsById\":null}");
        True(service.TryLoad(
                workspace.Paths.RootDirectory,
                out _,
                out _,
                out recoveryReason),
            $"损坏的 UI 主文件应回退有效备份：{recoveryReason}");

        var mismatchedUiProject = ProjectJson.Deserialize(File.ReadAllText(workspace.Paths.UiProjectBackupPath));
        mismatchedUiProject.Id = Guid.NewGuid().ToString("N");
        File.WriteAllText(workspace.Paths.UiProjectPath, ProjectJson.Serialize(mismatchedUiProject));
        True(service.TryLoad(
                workspace.Paths.RootDirectory,
                out _,
                out _,
                out recoveryReason),
            $"身份不匹配的 UI 主文件应回退同项目备份：{recoveryReason}");

        File.Delete(workspace.Paths.UiProjectBackupPath);
        False(service.TryLoad(
                workspace.Paths.RootDirectory,
                out _,
                out _,
                out _),
            "没有有效备份时必须拒绝加载属于其他项目的 UI 数据");
        mismatchedUiProject.Id = workspace.Manifest.Id;
        mismatchedUiProject.Name = workspace.Manifest.Name;
        File.WriteAllText(workspace.Paths.UiProjectPath, ProjectJson.Serialize(mismatchedUiProject));

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(appPaths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var browser = new ProjectBrowserViewModel(
            appPaths,
            new DesktopFileDialogService(),
            editor,
            workspace.Paths.AssetDirectory,
            workspace.Paths.GeneratedAssetDirectory);
        browser.SetRootSelectionLock(true);
        try
        {
            Directory.Delete(workspace.Paths.AssetDirectory, recursive: true);
            browser.Refresh();
            True(browser.IsRootSelectionLocked, "Assets 丢失后仍应保持正式项目根锁定");
            True(AssetRootService.PathEquals(workspace.Paths.AssetDirectory, browser.RootPath),
                "Assets 丢失后不得改写 Project Browser 根路径");
            False(AssetRootService.PathEquals(appPaths.AssetDirectory, browser.RootPath),
                "Assets 丢失后不得静默回退全局默认 Assets");
            browser.CreateFolderCommand.Execute(null);
            False(Directory.Exists(workspace.Paths.AssetDirectory),
                "资产根不可用时创建文件夹不得隐式重建或写入错误目录");
            True(browser.StatusText.Contains("不可用", StringComparison.Ordinal),
                "资产根不可用时应向用户展示明确状态");
        }
        finally
        {
            browser.Shutdown();
            editor.Shutdown();
        }
    }

    private static void TilemapWorkflowShowsParameterizedDiamondGrid()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "tilemap-grid"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        shell.SelectWorkflow("tilemap");
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("Tilemap 网格测试未找到 DockManager。");
            var canvas = FindDockContent(manager, MainWindow.TilemapWorkspaceContentId) as LayoutDocument
                ?? throw new SmokeTestException("Tilemap 网格测试未找到中央文档。");
            Equal("Tilemap 制作", canvas.Title, "Tilemap 中央文档标题");
            True(canvas.IsSelected && canvas.IsActive,
                "启动时应根据当前业务上下文选中 Tilemap 文档标签");
            Equal(MainWindow.TilemapWorkspaceContentId, manager.Layout.ActiveContent?.ContentId,
                "默认布局装载后 AvalonDock 全局活动内容应保持为 Tilemap 文档");

            var host = canvas.Content as ContentControl
                ?? throw new SmokeTestException("Tilemap 中央文档内容必须由 ContentControl 承载。");
            var tilemapView = host.Content as TilemapWorkspaceView
                ?? throw new SmokeTestException("Tilemap 工作流未加载 TilemapWorkspaceView。");
            Equal(shell.TilemapWorkspace, tilemapView.DataContext, "Tilemap 视图 DataContext");

            var properties = FindDockContent(manager, MainWindow.PropertiesContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("Tilemap 网格测试未找到属性面板。");
            var propertiesHost = properties.Content as ContentControl
                ?? throw new SmokeTestException("Tilemap 属性面板必须由 ContentControl 承载。");
            var tilemapPropertiesView = propertiesHost.Content as TilemapPropertiesView
                ?? throw new SmokeTestException("Tilemap 工作流未加载 TilemapPropertiesView。");
            Equal(shell.TilemapWorkspace, tilemapPropertiesView.DataContext, "Tilemap 属性面板 DataContext");

            var gridUnitSizeTextBox = RequireNamedElement<TextBox>(
                tilemapPropertiesView,
                "GridUnitSizeTextBox",
                "Tilemap 属性面板缺少网格单位输入框");
            var artImageWidthTextBox = RequireNamedElement<TextBox>(
                tilemapPropertiesView,
                "ArtImageWidthTextBox",
                "Tilemap 属性面板缺少美术图宽度输入框");
            var artImageHeightTextBox = RequireNamedElement<TextBox>(
                tilemapPropertiesView,
                "ArtImageHeightTextBox",
                "Tilemap 属性面板缺少美术图高度输入框");
            var diamondAngleSlider = RequireNamedElement<Slider>(
                tilemapPropertiesView,
                "DiamondAngleSlider",
                "Tilemap 属性面板缺少菱形角度滑块");
            var rotationAngleSlider = RequireNamedElement<Slider>(
                tilemapPropertiesView,
                "RotationAngleSlider",
                "Tilemap 属性面板缺少旋转角度滑块");
            var resetParametersButton = RequireNamedElement<Button>(
                tilemapPropertiesView,
                "ResetTilemapParametersButton",
                "Tilemap 属性面板缺少恢复默认参数按钮");
            True(gridUnitSizeTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "网格单位输入框应绑定参数模型");
            True(artImageWidthTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "美术图宽度输入框应绑定参数模型");
            True(artImageHeightTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "美术图高度输入框应绑定参数模型");
            True(diamondAngleSlider.GetBindingExpression(Slider.ValueProperty) is not null,
                "菱形角度滑块应绑定参数模型");
            True(rotationAngleSlider.GetBindingExpression(Slider.ValueProperty) is not null,
                "旋转角度滑块应绑定参数模型");
            resetParametersButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            True(ReferenceEquals(shell.TilemapWorkspace.ResetParametersCommand, resetParametersButton.Command),
                "恢复默认参数按钮应绑定 ResetParametersCommand");

            var grid = tilemapView.FindName("TilemapGridSurface") as TilemapGridSurface
                ?? throw new SmokeTestException("TilemapWorkspaceView 缺少 TilemapGridSurface。");
            RefreshTilemapGridBindings(grid);
            var uiZoomToolbar = window.FindName("UiZoomToolbar") as StackPanel
                ?? throw new SmokeTestException("主窗口缺少 UI 专用缩放工具栏。");
            uiZoomToolbar.UpdateLayout();
            Equal(Visibility.Collapsed, uiZoomToolbar.Visibility,
                "Tilemap 工作流应隐藏顶部 UI 编辑器缩放工具栏");

            var parameters = shell.TilemapWorkspace;
            Equal(TilemapWorkspaceViewModel.DefaultGridUnitSize, parameters.GridUnitSize,
                "Tilemap 默认网格单位");
            Equal(TilemapWorkspaceViewModel.DefaultArtImageWidth, parameters.ArtImageWidth,
                "Tilemap 默认美术图宽度");
            Equal(TilemapWorkspaceViewModel.DefaultArtImageHeight, parameters.ArtImageHeight,
                "Tilemap 默认美术图高度");
            Near(TilemapWorkspaceViewModel.DefaultDiamondAngle, parameters.DiamondAngle, 0.000000001,
                "Tilemap 默认菱形角度");
            Equal(TilemapWorkspaceViewModel.DefaultRotationAngle, parameters.RotationAngle,
                "Tilemap 默认旋转角度");
            Equal("512 × 256 px", parameters.ArtSizeText, "Tilemap 默认美术图尺寸文本");
            Equal("2:1", parameters.ImageAspectRatioText, "Tilemap 默认美术图宽高比");
            Equal("102.4 × 51.2 视图单位", parameters.ProjectedTileSizeText,
                "Tilemap 默认未旋转投影尺寸");
            Equal("102.4 × 51.2 视图单位", parameters.RotatedFootprintText,
                "Tilemap 默认旋转包围盒");
            Equal("X 5 / Y 5 px/视图单位", parameters.PixelDensityText,
                "Tilemap 默认像素密度");
            var defaultGenerationConstraints = parameters.BuildGenerationConstraints();
            Equal("imagegen", defaultGenerationConstraints.PreferredSkill,
                "Tilemap 默认约束应指向真实图像生成技能");
            Equal(512, defaultGenerationConstraints.Output.Width, "Tilemap 默认 AI 输出宽度");
            Equal(256, defaultGenerationConstraints.Output.Height, "Tilemap 默认 AI 输出高度");
            True(defaultGenerationConstraints.Output.TransparentBackground,
                "Tilemap 默认 AI 输出必须要求透明背景");
            True(defaultGenerationConstraints.Rules.Any(rule =>
                    rule.Key == "tilemap.background.transparent" && rule.Required),
                "Tilemap 默认 AI 约束必须包含瓦片有效轮廓外真实 alpha 规则");
            Equal(defaultGenerationConstraints.Prompt, parameters.AiGenerationConstraint,
                "属性面板应显示 Core 统一生成的 Tilemap AI 提示文本");

            Equal(64d, grid.CellSize, "Tilemap 网格 DP 默认单格边长");
            Near(TilemapWorkspaceViewModel.DefaultDiamondAngle, grid.DiamondAngle, 0.000000001,
                "Tilemap 网格 DP 默认菱形角度");
            Equal(0d, grid.GridRotation, "Tilemap 网格 DP 默认旋转角度");
            Equal(8, grid.MajorLineInterval, "Tilemap 主网格间隔");
            Equal(1d, grid.Zoom, "Tilemap 初始缩放");
            Equal("100%", grid.ZoomText, "Tilemap 初始缩放文本");

            grid.Measure(new Size(800, 600));
            grid.Arrange(new Rect(0, 0, 800, 600));
            AssertDiamondCellCenter(grid, 0, 0, "默认菱形原点格");
            AssertDiamondCellCenter(grid, 1, 0, "默认菱形 A 轴正格");
            AssertDiamondCellCenter(grid, 0, 1, "默认菱形 B 轴正格");
            AssertDiamondCellCenter(grid, -1, -1, "默认菱形双负坐标格");
            AssertDiamondCellCenter(grid, -3, 2, "默认菱形混合负坐标格");

            grid.PanX = 32d;
            grid.PanY = -32d;
            AssertDiamondCellCenter(grid, -2, 3, "平移后的菱形格命中");
            grid.ResetView();

            parameters.GridUnitSize = 100d;
            parameters.ArtImageWidth = 1024;
            parameters.ArtImageHeight = 512;
            parameters.DiamondAngle = 90d;
            parameters.RotationAngle = 45d;
            RefreshTilemapGridBindings(grid);
            Equal(100d, grid.CellSize, "参数模型应驱动网格单格边长 DP");
            Equal(90d, grid.DiamondAngle, "参数模型应驱动网格菱形角度 DP");
            Equal(45d, grid.GridRotation, "参数模型应驱动网格旋转 DP");
            Equal("1024 × 512 px", parameters.ArtSizeText, "变更后的美术图尺寸文本");
            Equal("2:1", parameters.ImageAspectRatioText, "变更后的美术图宽高比");
            Equal("100 × 100 视图单位", parameters.ProjectedTileSizeText,
                "变更后的投影尺寸");
            Equal("141.42 × 141.42 视图单位", parameters.RotatedFootprintText,
                "变更后的旋转包围盒");
            Equal("X 10.24 / Y 5.12 px/视图单位", parameters.PixelDensityText,
                "变更后的像素密度");
            True(shell.WorkspaceStatusText.Contains("1024 × 512", StringComparison.Ordinal) &&
                 shell.WorkspaceStatusText.Contains("A 边方向 45°", StringComparison.Ordinal),
                "Tilemap 参数变化应实时同步主窗口状态栏");
            var changedGenerationConstraints = parameters.BuildGenerationConstraints(
                "苔藓覆盖的古代石质地表瓦片",
                "手绘奇幻游戏美术");
            Equal(1024, changedGenerationConstraints.Output.Width, "变更后的 AI 输出宽度");
            Equal(512, changedGenerationConstraints.Output.Height, "变更后的 AI 输出高度");
            True(changedGenerationConstraints.Prompt.Contains("控制角为 90°", StringComparison.Ordinal) &&
                 changedGenerationConstraints.Prompt.Contains("顺时针旋转 45°", StringComparison.Ordinal) &&
                 changedGenerationConstraints.Prompt.Contains("手绘奇幻游戏美术", StringComparison.Ordinal),
                "Core AI 约束应组合当前几何参数、内容与美术风格");
            True(parameters.AiGenerationConstraint.Contains("alpha 必须为 0", StringComparison.Ordinal),
                "属性面板生成约束应明确瓦片有效轮廓外 alpha 通道规则");
            var persistedTilemapSettings = new UserSettingsService(paths).Load();
            Equal(100d, persistedTilemapSettings.TilemapGridUnitSize,
                "Tilemap 网格单位应即时持久化供外部 Agent 读取");
            Equal(1024, persistedTilemapSettings.TilemapArtImageWidth,
                "Tilemap 美术图宽度应即时持久化供外部 Agent 读取");
            Equal(512, persistedTilemapSettings.TilemapArtImageHeight,
                "Tilemap 美术图高度应即时持久化供外部 Agent 读取");
            Equal(90d, persistedTilemapSettings.TilemapDiamondAngle,
                "Tilemap 控制角应即时持久化供外部 Agent 读取");
            Equal(45d, persistedTilemapSettings.TilemapRotationAngle,
                "Tilemap 旋转角应即时持久化供外部 Agent 读取");

            grid.ResetView();
            AssertDiamondCellCenter(grid, 0, 0, "旋转菱形原点格");
            AssertDiamondCellCenter(grid, 2, -3, "旋转菱形混合坐标格");
            AssertDiamondCellCenter(grid, -4, -2, "旋转菱形负坐标格");

            grid.PanX = 17d;
            grid.PanY = -23d;
            var zoomAnchor = new Point(173, 219);
            var anchoredCell = grid.GetCellAt(zoomAnchor);
            var anchoredCoordinates = grid.GetGridCoordinates(zoomAnchor);
            grid.ZoomAt(zoomAnchor, 1.75d);
            Equal(anchoredCell, grid.GetCellAt(zoomAnchor), "Tilemap 光标锚点缩放不应漂移到其他格");
            var coordinatesAfterZoom = grid.GetGridCoordinates(zoomAnchor);
            Near(anchoredCoordinates.X, coordinatesAfterZoom.X, 0.000000001,
                "Tilemap 光标锚点缩放应保持连续 A 坐标");
            Near(anchoredCoordinates.Y, coordinatesAfterZoom.Y, 0.000000001,
                "Tilemap 光标锚点缩放应保持连续 B 坐标");
            grid.ResetView();

            grid.ZoomBy(2d);
            Equal(2d, grid.Zoom, "Tilemap 放大后的缩放");
            grid.PanX = 96d;
            grid.PanY = -64d;
            grid.ResetView();
            Equal(1d, grid.Zoom, "Tilemap 重置后的缩放");
            Equal(0d, grid.PanX, "Tilemap 重置后的水平偏移");
            Equal(0d, grid.PanY, "Tilemap 重置后的垂直偏移");

            grid.Zoom = 99d;
            Equal(TilemapGridSurface.MaximumZoom, grid.Zoom, "Tilemap 最大缩放限制");
            grid.Zoom = 0.01d;
            Equal(TilemapGridSurface.MinimumZoom, grid.Zoom, "Tilemap 最小缩放限制");
            grid.Zoom = double.NaN;
            Equal(1d, grid.Zoom, "Tilemap 非法缩放应回退默认值");
            grid.PanX = double.NaN;
            Equal(0d, grid.PanX, "Tilemap 非法平移值应回退原点");

            parameters.GridUnitSize = 0d;
            Equal(TilemapWorkspaceViewModel.MinimumGridUnitSize, parameters.GridUnitSize,
                "网格单位下限钳制");
            parameters.GridUnitSize = 999d;
            Equal(TilemapWorkspaceViewModel.MaximumGridUnitSize, parameters.GridUnitSize,
                "网格单位上限钳制");
            parameters.GridUnitSize = 100d;
            parameters.GridUnitSize = double.NaN;
            Equal(TilemapWorkspaceViewModel.DefaultGridUnitSize, parameters.GridUnitSize,
                "NaN 网格单位应回退默认值");
            parameters.GridUnitSize = 100d;
            parameters.GridUnitSize = double.PositiveInfinity;
            Equal(TilemapWorkspaceViewModel.DefaultGridUnitSize, parameters.GridUnitSize,
                "Infinity 网格单位应回退默认值");

            parameters.ArtImageWidth = 0;
            Equal(TilemapWorkspaceViewModel.MinimumArtImageSize, parameters.ArtImageWidth,
                "美术图宽度下限钳制");
            parameters.ArtImageWidth = int.MaxValue;
            Equal(TilemapWorkspaceViewModel.MaximumArtImageSize, parameters.ArtImageWidth,
                "美术图宽度上限钳制");
            parameters.ArtImageHeight = 0;
            Equal(TilemapWorkspaceViewModel.MinimumArtImageSize, parameters.ArtImageHeight,
                "美术图高度下限钳制");
            parameters.ArtImageHeight = int.MaxValue;
            Equal(TilemapWorkspaceViewModel.MaximumArtImageSize, parameters.ArtImageHeight,
                "美术图高度上限钳制");

            parameters.DiamondAngle = 0d;
            Equal(TilemapWorkspaceViewModel.MinimumDiamondAngle, parameters.DiamondAngle,
                "菱形角度下限钳制");
            parameters.DiamondAngle = 999d;
            Equal(TilemapWorkspaceViewModel.MaximumDiamondAngle, parameters.DiamondAngle,
                "菱形角度上限钳制");
            parameters.DiamondAngle = 90d;
            parameters.DiamondAngle = double.NaN;
            Near(TilemapWorkspaceViewModel.DefaultDiamondAngle, parameters.DiamondAngle, 0.000000001,
                "NaN 菱形角度应回退默认值");
            parameters.DiamondAngle = 90d;
            parameters.DiamondAngle = double.NegativeInfinity;
            Near(TilemapWorkspaceViewModel.DefaultDiamondAngle, parameters.DiamondAngle, 0.000000001,
                "Infinity 菱形角度应回退默认值");

            parameters.RotationAngle = -999d;
            Equal(TilemapWorkspaceViewModel.MinimumRotationAngle, parameters.RotationAngle,
                "旋转角度下限钳制");
            parameters.RotationAngle = 999d;
            Equal(TilemapWorkspaceViewModel.MaximumRotationAngle, parameters.RotationAngle,
                "旋转角度上限钳制");
            parameters.RotationAngle = 45d;
            parameters.RotationAngle = double.NaN;
            Equal(TilemapWorkspaceViewModel.DefaultRotationAngle, parameters.RotationAngle,
                "NaN 旋转角度应回退默认值");
            parameters.RotationAngle = 45d;
            parameters.RotationAngle = double.PositiveInfinity;
            Equal(TilemapWorkspaceViewModel.DefaultRotationAngle, parameters.RotationAngle,
                "Infinity 旋转角度应回退默认值");

            var coercionGrid = new TilemapGridSurface();
            coercionGrid.CellSize = 0d;
            Equal(8d, coercionGrid.CellSize, "网格控件单格边长下限钳制");
            coercionGrid.CellSize = 999d;
            Equal(512d, coercionGrid.CellSize, "网格控件单格边长上限钳制");
            coercionGrid.CellSize = double.NaN;
            Equal(TilemapGridSurface.DefaultCellSize, coercionGrid.CellSize,
                "网格控件 NaN 单格边长应回退默认值");
            coercionGrid.DiamondAngle = 0d;
            Equal(15d, coercionGrid.DiamondAngle, "网格控件菱形角度下限钳制");
            coercionGrid.DiamondAngle = 999d;
            Equal(165d, coercionGrid.DiamondAngle, "网格控件菱形角度上限钳制");
            coercionGrid.DiamondAngle = double.PositiveInfinity;
            Near(TilemapGridSurface.DefaultDiamondAngle, coercionGrid.DiamondAngle, 0.000000001,
                "网格控件非法菱形角度应回退默认值");
            coercionGrid.GridRotation = -999d;
            Equal(-180d, coercionGrid.GridRotation, "网格控件旋转角度下限钳制");
            coercionGrid.GridRotation = 999d;
            Equal(180d, coercionGrid.GridRotation, "网格控件旋转角度上限钳制");
            coercionGrid.GridRotation = double.NegativeInfinity;
            Equal(0d, coercionGrid.GridRotation, "网格控件非法旋转角度应回退默认值");

            True(resetParametersButton.Command?.CanExecute(resetParametersButton.CommandParameter) == true,
                "恢复默认参数命令应可执行");
            resetParametersButton.Command!.Execute(resetParametersButton.CommandParameter);
            RefreshTilemapGridBindings(grid);
            Equal(TilemapWorkspaceViewModel.DefaultGridUnitSize, parameters.GridUnitSize,
                "属性面板按钮应恢复默认网格单位");
            Equal(TilemapWorkspaceViewModel.DefaultArtImageWidth, parameters.ArtImageWidth,
                "属性面板按钮应恢复默认美术图宽度");
            Equal(TilemapWorkspaceViewModel.DefaultArtImageHeight, parameters.ArtImageHeight,
                "属性面板按钮应恢复默认美术图高度");
            Near(TilemapWorkspaceViewModel.DefaultDiamondAngle, parameters.DiamondAngle, 0.000000001,
                "属性面板按钮应恢复默认菱形角度");
            Equal(TilemapWorkspaceViewModel.DefaultRotationAngle, parameters.RotationAngle,
                "属性面板按钮应恢复默认旋转角度");
            grid.ResetView();
            grid.Measure(new Size(800, 600));
            grid.Arrange(new Rect(0, 0, 800, 600));

            var renderTarget = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            renderTarget.Render(grid);
            var renderedPixel = new byte[4];
            renderTarget.CopyPixels(new Int32Rect(400, 300, 1, 1), renderedPixel, 4, 0);
            True(renderedPixel[3] > 0, "Tilemap 网格实际渲染结果不应为全透明");

            var darkBackground = (grid.Background as SolidColorBrush)?.Color
                ?? throw new SmokeTestException("暗色 Tilemap 网格背景必须是 SolidColorBrush。");
            shell.SetTheme(AppTheme.Light);
            True(tilemapView.TryFindResource("App.TilemapGridBackgroundBrush") is not null,
                "亮色主题应提供 Tilemap 网格背景资源");
            True(tilemapView.TryFindResource("App.TilemapGuideBrush") is not null,
                "亮色主题应提供 Tilemap 网格引导色资源");
            var lightBackground = (grid.Background as SolidColorBrush)?.Color
                ?? throw new SmokeTestException("亮色 Tilemap 网格背景必须是 SolidColorBrush。");
            False(darkBackground == lightBackground, "暗亮主题应实际切换 Tilemap 网格背景颜色");

            var uiDocument = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
                ?? throw new SmokeTestException("Tilemap 测试缺少独立页面 UI 文档。");
            var uiHost = uiDocument.Content as ContentControl
                ?? throw new SmokeTestException("页面 UI 文档必须由稳定 ContentControl 承载。");
            shell.SelectWorkflow("ui");
            True(uiHost.Content is CanvasDocumentView, "页面 UI 文档应保持独立 UI 画布");
            True(ReferenceEquals(tilemapView, host.Content),
                "切换活动业务上下文不得替换 Tilemap 文档内容");
            Equal(MainWindow.CanvasContentId, manager.Layout.ActiveContent?.ContentId,
                "选择 UI 工作流应同步激活对应的 UI 文档窗口");
            True(propertiesHost.Content is PropertiesView, "切回 UI 工作流应恢复 UI 属性面板");
            uiZoomToolbar.UpdateLayout();
            Equal(Visibility.Visible, uiZoomToolbar.Visibility,
                "UI 工作流应显示顶部 UI 编辑器缩放工具栏");
            shell.SelectWorkflow("tilemap");
            Equal(MainWindow.TilemapWorkspaceContentId, manager.Layout.ActiveContent?.ContentId,
                "选择 Tilemap 工作流应同步激活对应的 Tilemap 文档窗口");
            True(ReferenceEquals(tilemapView, host.Content), "再次进入 Tilemap 应复用独立网格工作区实例");
            True(ReferenceEquals(tilemapPropertiesView, propertiesHost.Content),
                "再次进入 Tilemap 应复用参数属性面板实例");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void McpAgentConnectionDockPanelBindsProfilesAndPersistsSettings()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "mcp-agent-dock-panel"));
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            McpAgentKey = "codex",
            McpPort = AgentConnectionViewModel.DefaultPort
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();

            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("MCP Agent 接入测试未找到 DockManager。");
            var agentPanel = FindDockContent(manager, MainWindow.AgentConnectionContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("默认布局缺少 MCP Agent 接入停靠面板。");
            True(agentPanel.IsHidden, "MCP Agent 接入停靠面板默认应隐藏");
            Equal("MCP Agent 接入", agentPanel.Title, "MCP Agent 接入停靠面板标题");
            True(agentPanel.CanMove && agentPanel.CanFloat && agentPanel.CanHide,
                "MCP Agent 接入面板应支持拖动、浮动与隐藏");
            True(agentPanel.FloatingWidth is >= 820d and <= 900d &&
                 agentPanel.FloatingHeight is >= 520d and <= 600d,
                "MCP Agent 接入面板默认浮动尺寸应适合低分辨率屏幕");

            var agentHost = agentPanel.Content as ContentControl
                ?? throw new SmokeTestException("MCP Agent 接入停靠内容应使用稳定 ContentControl host。");
            var agentView = agentHost.Content as AgentConnectionView
                ?? throw new SmokeTestException("MCP Agent 接入停靠面板未承载 AgentConnectionView。");
            True(ReferenceEquals(shell.AgentConnection, agentView.DataContext),
                "AgentConnectionView 应复用 Shell 持有的连接 ViewModel");
            True(agentView.MinWidth <= 320d && agentView.MinHeight <= 260d,
                "AgentConnectionView 应允许低分辨率停靠区压缩并通过滚动访问内容");

            True(window.FindName("McpAccessMenu") is null,
                "顶部不应再保留独立 MCP 接入菜单");
            var addAgentMenuItem = window.FindName("ShowAgentConnectionMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少 MCP Agent 接入项。");
            var viewMenu = window.FindName("ViewMenu") as MenuItem
                ?? throw new SmokeTestException("顶部菜单缺少视图入口。");
            True(viewMenu.Items.Contains(addAgentMenuItem),
                "MCP Agent 接入项必须合并到视图菜单中");
            Equal("MCP Agent 接入", addAgentMenuItem.Header as string, "视图菜单 Agent 连接标题");
            addAgentMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(addAgentMenuItem.IsCheckable,
                "视图菜单的 MCP Agent 接入项应显示窗口开启状态");
            True(ReferenceEquals(shell.TogglePanelCommand, addAgentMenuItem.Command),
                "MCP Agent 接入项应绑定 Shell.TogglePanelCommand");
            Equal(MainWindow.AgentConnectionContentId, addAgentMenuItem.CommandParameter as string,
                "MCP Agent 接入项 ContentId");
            True(addAgentMenuItem.Command?.CanExecute(addAgentMenuItem.CommandParameter) == true,
                "MCP Agent 接入菜单命令应可执行");

            var windowCountBeforeOpen = Application.Current.Windows.Count;
            addAgentMenuItem.Command!.Execute(addAgentMenuItem.CommandParameter);
            False(agentPanel.IsHidden, "菜单应拉起默认隐藏的 MCP Agent 接入面板");
            True(agentPanel.IsVisible && agentPanel.IsSelected && agentPanel.IsActive,
                "菜单拉起后 MCP Agent 接入面板应可见、选中并激活");
            True(ReferenceEquals(manager.Layout.ActiveContent, agentPanel),
                "MCP Agent 接入面板应成为当前活动布局内容");
            True(ReferenceEquals(manager.ActiveContent, agentPanel.Content),
                "DockManager 应激活 MCP Agent 接入面板 host");
            Equal(windowCountBeforeOpen, Application.Current.Windows.Count,
                "拉起 MCP Agent 接入面板不应创建额外 Window");

            addAgentMenuItem.Command.Execute(addAgentMenuItem.CommandParameter);
            True(agentPanel.IsHidden, "再次点击视图菜单应隐藏 MCP Agent 接入面板");
            addAgentMenuItem.Command.Execute(addAgentMenuItem.CommandParameter);
            False(agentPanel.IsHidden, "第三次点击视图菜单应恢复 MCP Agent 接入面板");
            True(ReferenceEquals(agentPanel, FindDockContent(manager, MainWindow.AgentConnectionContentId)),
                "视图菜单切换应复用同一个 MCP Agent 接入 LayoutAnchorable");
            True(ReferenceEquals(agentView, agentHost.Content),
                "重复点击菜单应复用同一个 AgentConnectionView");

            agentPanel.Float();
            PumpDispatcher();
            True(agentPanel.IsFloating, "MCP Agent 接入面板应可浮动");
            True(manager.Layout.FloatingWindows.Any(floatingWindow =>
                    floatingWindow.Descendents().Any(element => ReferenceEquals(element, agentPanel))),
                "AvalonDock 浮动窗口应包含 MCP Agent 接入面板");
            var compactFloatingWindow = Application.Current.Windows
                .OfType<LayoutAnchorableFloatingWindowControl>()
                .SingleOrDefault(control =>
                    control.Model is LayoutAnchorableFloatingWindow model &&
                    model.Descendents().Any(element => ReferenceEquals(element, agentPanel)))
                ?? throw new SmokeTestException("MCP Agent 接入面板浮动后缺少 AvalonDock 浮动宿主窗口。");
            compactFloatingWindow.MinWidth = 0d;
            compactFloatingWindow.MinHeight = 0d;
            compactFloatingWindow.Width = 640d;
            compactFloatingWindow.Height = 480d;
            PumpDispatcher();
            compactFloatingWindow.UpdateLayout();
            agentView.UpdateLayout();
            True(agentView.ActualWidth <= agentHost.ActualWidth + 0.1d &&
                 agentView.ActualHeight <= agentHost.ActualHeight + 0.1d,
                "MCP Agent 页面在 640×480 AvalonDock 浮窗中不得超出宿主并被裁切");
            var dockedCompactSettingsScroller = agentView.FindName("AgentSettingsScrollViewer") as ScrollViewer
                ?? throw new SmokeTestException("MCP Agent AvalonDock 紧凑回归缺少主滚动区。");
            True(dockedCompactSettingsScroller.ViewportWidth > 0d &&
                 dockedCompactSettingsScroller.ViewportHeight > 0d &&
                 dockedCompactSettingsScroller.ScrollableHeight > 1d,
                "MCP Agent 页面在 640×480 AvalonDock 浮窗中必须保留可用设置视口和纵向滚动范围");
            agentPanel.Hide();
            PumpDispatcher();
            True(agentPanel.IsHidden, "浮动的 MCP Agent 接入面板应可隐藏");
            addAgentMenuItem.Command.Execute(addAgentMenuItem.CommandParameter);
            var restoredAgentPanel = FindDockContent(manager, MainWindow.AgentConnectionContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("菜单未恢复 MCP Agent 接入面板。");
            True(ReferenceEquals(agentPanel, restoredAgentPanel),
                "隐藏后恢复应复用同一个 MCP Agent 接入 LayoutAnchorable");
            False(restoredAgentPanel.IsHidden, "菜单恢复后 MCP Agent 接入面板不应隐藏");
            True(restoredAgentPanel.IsVisible && restoredAgentPanel.IsSelected && restoredAgentPanel.IsActive,
                "菜单恢复后 MCP Agent 接入面板应可见、选中并激活");

            var connection = shell.AgentConnection;
            using (var compactAgentView = new AgentConnectionView(connection))
            {
                var compactHost = new Border
                {
                    Child = compactAgentView
                };
                var compactWindow = new Window
                {
                    Title = "MCP Agent 紧凑布局回归",
                    Width = 600d,
                    Height = 420d,
                    MinWidth = 0d,
                    MinHeight = 0d,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.ToolWindow,
                    Content = compactHost
                };

                try
                {
                    compactWindow.Show();
                    PumpDispatcher();
                    compactWindow.UpdateLayout();
                    compactAgentView.UpdateLayout();

                    True(compactAgentView.ActualWidth <= compactHost.ActualWidth + 0.1d,
                        "MCP Agent 页面在低分辨率窄窗口中不得以最小宽度撑出并被宿主裁切");
                    True(compactAgentView.ActualHeight <= compactHost.ActualHeight + 0.1d,
                        "MCP Agent 页面在低分辨率矮窗口中不得以最小高度撑出并被宿主裁切");

                    var compactSettingsScroller = compactAgentView.FindName("AgentSettingsScrollViewer") as ScrollViewer
                        ?? throw new SmokeTestException("MCP Agent 页面缺少紧凑布局主滚动区。");
                    True(compactSettingsScroller.ViewportWidth > 0d && compactSettingsScroller.ViewportHeight > 0d,
                        "MCP Agent 主滚动区在紧凑窗口中必须获得真实可见视口");
                    True(compactSettingsScroller.ScrollableHeight > 1d,
                        "MCP Agent 主滚动区在低高度下必须开放纵向滚动以访问完整设置");
                    True(compactSettingsScroller.ScrollableWidth > 1d,
                        "MCP Agent 主滚动区在低宽度下必须开放横向滚动以访问完整设置");

                    compactSettingsScroller.ScrollToTop();
                    PumpDispatcher();
                    var compactWheelTarget = compactAgentView.FindName("AgentSelectorComboBox") as ComboBox
                        ?? throw new SmokeTestException("MCP Agent 紧凑滚轮测试缺少 Agent 选择框目标。");
                    var agentBeforePageScroll = connection.SelectedAgent;
                    compactWheelTarget.RaiseEvent(new MouseWheelEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        -120)
                    {
                        RoutedEvent = Mouse.MouseWheelEvent,
                        Source = compactWheelTarget
                    });
                    PumpDispatcher();
                    True(compactSettingsScroller.VerticalOffset > 0d,
                        "MCP Agent 主设置区必须支持鼠标滚轮浏览");
                    True(ReferenceEquals(agentBeforePageScroll, connection.SelectedAgent),
                        "在 Agent 下拉框关闭时滚动页面不得误切换连接 Agent");

                    var finalCompactExpander = compactAgentView.FindName("CodexManualConnectionExpander") as Expander
                        ?? throw new SmokeTestException("MCP Agent 紧凑布局缺少 Codex 手工连接折叠区。");
                    finalCompactExpander.IsExpanded = true;
                    PumpDispatcher();
                    compactAgentView.UpdateLayout();
                    compactSettingsScroller.ScrollToEnd();
                    PumpDispatcher();
                    var finalCompactButton = compactAgentView.FindName("CopyCodexConnectionPromptButton") as Button
                        ?? throw new SmokeTestException("MCP Agent 紧凑布局缺少复制 Codex 连接指令按钮。");
                    var finalButtonBounds = finalCompactButton.TransformToAncestor(compactSettingsScroller)
                        .TransformBounds(new Rect(
                            0d,
                            0d,
                            finalCompactButton.ActualWidth,
                            finalCompactButton.ActualHeight));
                    True(finalCompactButton.IsVisible &&
                         finalButtonBounds.Top >= -0.5d &&
                         finalButtonBounds.Bottom <= compactSettingsScroller.ActualHeight + 0.5d,
                        "MCP Agent 设置区滚动到底后必须能够完整访问复制连接指令按钮");
                }
                finally
                {
                    compactWindow.Close();
                }
            }

            SequenceEqual(new[] { "codex", "opencode" }, connection.Agents.Select(agent => agent.Key),
                "支持的 Agent profile");
            SequenceEqual(new[] { "Codex", "OpenCode" }, connection.Agents.Select(agent => agent.DisplayName),
                "Agent profile 显示名称");
            var endpoint = new Uri($"http://127.0.0.1:{AgentConnectionViewModel.DefaultPort}/mcp");
            var codexProfile = connection.Agents.Single(agent => agent.Key == "codex");
            var openCodeProfile = connection.Agents.Single(agent => agent.Key == "opencode");
            var codexConfig = codexProfile.CreateConfigSnippet(endpoint);
            var openCodeConfig = openCodeProfile.CreateConfigSnippet(endpoint);
            True(codexConfig.Contains("[mcp_servers.gasset_forge]", StringComparison.Ordinal) &&
                 codexConfig.Contains(endpoint.AbsoluteUri, StringComparison.Ordinal) &&
                 codexConfig.Contains(
                     $"tool_timeout_sec = {GenerationMcpToolPolicy.ToolTimeoutSeconds}",
                     StringComparison.Ordinal),
                "Codex profile 应生成 Streamable HTTP MCP 配置");
            True(openCodeConfig.Contains("\"type\": \"remote\"", StringComparison.Ordinal) &&
                 openCodeConfig.Contains(endpoint.AbsoluteUri, StringComparison.Ordinal),
                "OpenCode profile 应生成 remote MCP 配置");

            const string tokenVariableName = McpServerProcessService.DefaultBearerTokenEnvironmentVariableName;
            const string secretTokenValue = "agent-connection-manual-prompt-secret";
            var originalTokenValue = Environment.GetEnvironmentVariable(tokenVariableName);
            Environment.SetEnvironmentVariable(tokenVariableName, secretTokenValue);
            try
            {
                True(connection.CanCopyCodexConnectionPrompt,
                    "有效工作目录和 Endpoint 应允许复制 Codex 连接指令");
                True(connection.CodexConnectionPrompt.Contains(connection.WorkingDirectory, StringComparison.Ordinal),
                    "Codex 连接指令必须包含当前项目工作目录");
                True(connection.CodexConnectionPrompt.Contains(connection.Endpoint, StringComparison.Ordinal),
                    "Codex 连接指令必须包含当前 MCP Endpoint");
                True(connection.CodexConnectionPrompt.Contains("server_info", StringComparison.Ordinal),
                    "Codex 连接指令必须要求真实 MCP 工具探测");
                False(connection.CodexConnectionPrompt.Contains(secretTokenValue, StringComparison.Ordinal),
                    "Codex 连接指令不得泄露 Bearer Token 明文");
            }
            finally
            {
                Environment.SetEnvironmentVariable(tokenVariableName, originalTokenValue);
            }

            var configExpander = VisualDescendants<Expander>(agentView)
                .SingleOrDefault(expander =>
                    expander.Header?.ToString()?.Contains("MCP 参考配置", StringComparison.Ordinal) == true)
                ?? throw new SmokeTestException("Agent 连接面板缺少 MCP 参考配置折叠区。");
            configExpander.IsExpanded = true;
            var codexConnectionExpander = VisualDescendants<Expander>(agentView)
                .SingleOrDefault(expander =>
                    expander.Header?.ToString()?.Contains("Codex 手工连接指令", StringComparison.Ordinal) == true)
                ?? throw new SmokeTestException("Agent 连接面板缺少 Codex 手工连接指令折叠区。");
            codexConnectionExpander.IsExpanded = true;
            PumpDispatcher();
            agentView.UpdateLayout();

            var agentSelector = RequireBoundElement<ComboBox>(
                agentView,
                ComboBox.SelectedItemProperty,
                nameof(AgentConnectionViewModel.SelectedAgent),
                "Agent 连接面板缺少 Agent 下拉菜单绑定。");
            var portTextBox = RequireBoundElement<TextBox>(
                agentView,
                TextBox.TextProperty,
                nameof(AgentConnectionViewModel.Port),
                "Agent 连接面板缺少端口输入绑定。");
            var endpointTextBox = RequireBoundElement<TextBox>(
                agentView,
                TextBox.TextProperty,
                nameof(AgentConnectionViewModel.Endpoint),
                "Agent 连接面板缺少 Endpoint 只读框。");
            var workingDirectoryTextBox = RequireBoundElement<TextBox>(
                agentView,
                TextBox.TextProperty,
                nameof(AgentConnectionViewModel.WorkingDirectory),
                "Agent 连接面板缺少工作目录绑定。");
            var configTextBox = RequireBoundElement<TextBox>(
                agentView,
                TextBox.TextProperty,
                nameof(AgentConnectionViewModel.ConfigSnippet),
                "Agent 连接面板缺少 Agent 配置片段框。");
            var codexConnectionPromptTextBox = RequireBoundElement<TextBox>(
                agentView,
                TextBox.TextProperty,
                nameof(AgentConnectionViewModel.CodexConnectionPrompt),
                "Agent 连接面板缺少 Codex 手工连接指令预览。");
            _ = RequireBoundElement<TextBlock>(
                agentView,
                TextBlock.TextProperty,
                nameof(AgentConnectionViewModel.StatusText),
                "Agent 连接面板缺少服务器状态绑定。");
            _ = RequireBoundElement<TextBlock>(
                agentView,
                TextBlock.TextProperty,
                nameof(AgentConnectionViewModel.LastHandshakeText),
                "Agent 连接面板缺少最近握手绑定。");
            foreach (var runtimeStatusProperty in new[]
                     {
                         nameof(AgentConnectionViewModel.RuntimeModeText),
                         nameof(AgentConnectionViewModel.AuthenticationText),
                         nameof(AgentConnectionViewModel.McpServerStatusText),
                         nameof(AgentConnectionViewModel.McpToolStatusText),
                         nameof(AgentConnectionViewModel.ThreadIdText),
                         nameof(AgentConnectionViewModel.TurnStatusText)
                     })
            {
                _ = RequireBoundElement<TextBlock>(
                    agentView,
                    TextBlock.TextProperty,
                    runtimeStatusProperty,
                    $"Agent 连接面板缺少 {runtimeStatusProperty} 状态绑定。");
            }
            var logList = agentView.FindName("LogListBox") as ListBox
                ?? throw new SmokeTestException("Agent 连接面板缺少实时日志列表。");
            var buttons = VisualDescendants<Button>(agentView).ToArray();
            var startButton = buttons.SingleOrDefault(button =>
                                  ReferenceEquals(button.Command, connection.StartCommand))
                              ?? throw new SmokeTestException("Agent 连接面板缺少启动 Agent 会话按钮。");
            True(buttons.Any(button => ReferenceEquals(button.Command, connection.CopyConfigCommand)),
                "Agent 连接面板缺少复制配置按钮");
            True(buttons.Any(button => ReferenceEquals(
                    button.Command,
                    connection.CopyCodexConnectionPromptCommand)),
                "Agent 连接面板缺少复制 Codex 连接指令按钮");
            True(buttons.Any(button => ReferenceEquals(button.Command, connection.ClearLogCommand)),
                "Agent 连接面板缺少清空日志按钮");
            True(buttons.Any(button => ReferenceEquals(button.Command, connection.LoginCommand)),
                "Agent 连接面板缺少 Codex 登录按钮");
            True(buttons.Any(button => ReferenceEquals(button.Command, connection.RefreshAuthenticationCommand)),
                "Agent 连接面板缺少刷新认证按钮");
            True(buttons.Any(button => ReferenceEquals(button.Command, connection.InterruptTurnCommand)),
                "Agent 连接面板缺少中断 Turn 按钮");

            Equal("DisplayName", agentSelector.DisplayMemberPath, "Agent 下拉菜单显示字段");
            True(ReferenceEquals(connection.Agents, agentSelector.ItemsSource),
                "Agent 下拉菜单应绑定支持列表");
            Equal("codex", ((AgentProfile)agentSelector.SelectedItem).Key, "默认选择 Codex");
            True(endpointTextBox.IsReadOnly, "Endpoint 应只读");
            True(workingDirectoryTextBox.IsReadOnly, "Agent 工作目录应由当前项目控制并保持只读");
            True(codexConnectionPromptTextBox.IsReadOnly && codexConnectionPromptTextBox.AcceptsReturn,
                "Codex 手工连接指令应使用只读多行预览");
            True(configTextBox.IsReadOnly && configTextBox.AcceptsReturn,
                "Agent 配置片段应是支持多行的只读文本框");
            True(ReferenceEquals(connection.Logs, logList.ItemsSource), "实时日志列表应绑定 Logs");
            Equal("启动 Agent 会话", startButton.Content as string, "Agent 会话主按钮初始文案");

            agentSelector.SelectedItem = openCodeProfile;
            agentSelector.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
            Equal("opencode", connection.SelectedAgent.Key, "Agent 下拉菜单应写回 OpenCode profile");
            False(connection.CanStart, "OpenCode Adapter 未接入前不得伪装为可启动 Runtime");
            True(connection.StatusText.Contains("Adapter 待接入", StringComparison.Ordinal),
                "OpenCode 应明确显示 Adapter 待接入");
            Equal("opencode", settingsService.Load().McpAgentKey,
                "Agent 选择改变后应立即写入用户设置");

            var persistedPort = AgentConnectionViewModel.DefaultPort + 37;
            portTextBox.Text = persistedPort.ToString();
            portTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Equal(persistedPort, connection.Port, "端口输入应写回连接 ViewModel");
            var persistedSettings = settingsService.Load();
            Equal("opencode", persistedSettings.McpAgentKey, "即时持久化后的 Agent key");
            Equal(persistedPort, persistedSettings.McpPort, "端口改变后应立即写入用户设置");

            endpointTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            configTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            Equal($"http://127.0.0.1:{persistedPort}/mcp", endpointTextBox.Text,
                "端口改变后 Endpoint 应同步更新");
            True(configTextBox.Text.Contains("opencode.ai/config.json", StringComparison.Ordinal) &&
                 configTextBox.Text.Contains($"http://127.0.0.1:{persistedPort}/mcp", StringComparison.Ordinal),
                "Agent 与端口改变后配置片段应同步更新");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void McpServerProcessServiceRunsHttpHandshakeAndStopsCleanly() =>
        McpServerProcessServiceRunsHttpHandshakeAndStopsCleanlyAsync().GetAwaiter().GetResult();

    private static async Task McpServerProcessServiceRunsHttpHandshakeAndStopsCleanlyAsync()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "mcp-http-process"));
        using var service = new McpServerProcessService(paths);
        var logs = new ConcurrentQueue<McpServerLogEventArgs>();
        var states = new ConcurrentQueue<McpServerProcessState>();
        service.LogReceived += (_, eventArgs) => logs.Enqueue(eventArgs);
        service.StateChanged += (_, eventArgs) => states.Enqueue(eventArgs.State);

        var port = GetAvailableLoopbackPort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/mcp", UriKind.Absolute);
        var healthEndpoint = new Uri($"http://127.0.0.1:{port}/health", UriKind.Absolute);
        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", service.TokenEnvironmentVariableValue);

        try
        {
            await service.StartAsync(port, "codex");
            True(service.IsProcessRunning, "McpServerProcessService 应持有正在运行的真实子进程");
            Equal(endpoint, service.Endpoint, "McpServerProcessService HTTP Endpoint");

            using (var runningHealth = await WaitForHealthStatusAsync(
                       httpClient,
                       healthEndpoint,
                       "waiting",
                       TimeSpan.FromSeconds(12)))
            {
                Equal("codex", runningHealth.RootElement.GetProperty("expectedAgent").GetString(),
                    "HTTP health 预期 Agent");
                Equal(0L, runningHealth.RootElement.GetProperty("totalHandshakes").GetInt64(),
                    "握手前 health 计数");
            }

            using var initialize = await CallHttpMcpAsync(httpClient, endpoint, new
            {
                jsonrpc = "2.0",
                id = 101,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "Codex SmokeTests", version = "1.0.0" }
                }
            });
            Equal(HttpStatusCode.OK, initialize.StatusCode, "HTTP initialize 状态码");
            True(!string.IsNullOrWhiteSpace(initialize.SessionId),
                "HTTP initialize 应返回 Mcp-Session-Id");
            var initializeDocument = initialize.Payload
                ?? throw new SmokeTestException("HTTP initialize 未返回 JSON-RPC 响应体。");
            Equal("2025-06-18",
                initializeDocument.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString(),
                "HTTP MCP 协议协商版本");

            using (var initializedPendingHealth = await WaitForHealthStatusAsync(
                       httpClient,
                       healthEndpoint,
                       "waiting",
                       TimeSpan.FromSeconds(3)))
            {
                Equal(1, initializedPendingHealth.RootElement.GetProperty("activeSessions").GetInt32(),
                    "initialize 后应建立会话");
                Equal(0L, initializedPendingHealth.RootElement.GetProperty("totalHandshakes").GetInt64(),
                    "notifications/initialized 前不得计为握手完成");
            }

            using var initialized = await CallHttpMcpAsync(httpClient, endpoint, new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { }
            }, initialize.SessionId);
            Equal(HttpStatusCode.Accepted, initialized.StatusCode,
                "notifications/initialized 应返回 202");
            True(initialized.Payload is null, "initialized 通知不应返回 JSON-RPC 响应体");

            using var tools = await CallHttpMcpAsync(httpClient, endpoint, new
            {
                jsonrpc = "2.0",
                id = 102,
                method = "tools/list",
                @params = new { }
            }, initialize.SessionId);
            Equal(HttpStatusCode.OK, tools.StatusCode, "HTTP tools/list 状态码");
            var toolsDocument = tools.Payload
                ?? throw new SmokeTestException("HTTP tools/list 未返回 JSON-RPC 响应体。");
            var toolNames = toolsDocument.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var requiredTool in new[]
                     {
                         "server_info",
                         "get_current_context",
                         "create_generation_task",
                         "list_generation_tasks",
                         "get_generation_task",
                         "claim_generation_task",
                         "complete_generation_task",
                         "fail_generation_task"
                     })
            {
                True(toolNames.Contains(requiredTool), $"HTTP MCP tools/list 缺少 {requiredTool}");
            }

            using (var connectedHealth = await WaitForHealthStatusAsync(
                       httpClient,
                       healthEndpoint,
                       "connected",
                       TimeSpan.FromSeconds(8)))
            {
                Equal(1L, connectedHealth.RootElement.GetProperty("totalHandshakes").GetInt64(),
                    "握手后 health 计数");
                Equal(1, connectedHealth.RootElement.GetProperty("activeSessions").GetInt32(),
                    "握手后活跃会话数");
                Equal("Codex SmokeTests",
                    connectedHealth.RootElement.GetProperty("lastClientName").GetString(),
                    "health 最近握手客户端");
                Equal("1.0.0",
                    connectedHealth.RootElement.GetProperty("lastClientVersion").GetString(),
                    "health 最近客户端版本");
                True(connectedHealth.RootElement.GetProperty("lastHandshakeUtc").ValueKind == JsonValueKind.String,
                    "health 应记录最近握手时间");
            }

            await WaitUntilAsync(
                () => service.State == McpServerProcessState.Connected,
                TimeSpan.FromSeconds(8),
                "McpServerProcessService 未同步为 Connected 状态。");
            await WaitUntilAsync(
                () => logs.Any(entry => entry.Message.Contains("handshake completed", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5),
                "McpServerProcessService 未转发服务器握手日志。");
            True(states.Contains(McpServerProcessState.WaitingForAgent),
                "HTTP 服务健康后应发布 WaitingForAgent 状态");
            True(states.Contains(McpServerProcessState.Connected),
                "HTTP 握手后应发布 Connected 状态");
            True(logs.Any(entry => entry.Message.Contains("正在启动 MCP Server", StringComparison.Ordinal)),
                "服务日志应包含子进程启动记录");
            True(logs.Any(entry => entry.Message.Contains("健康检查通过", StringComparison.Ordinal)),
                "服务日志应包含健康检查记录");
        }
        finally
        {
            await service.StopAsync();
        }

        Equal(McpServerProcessState.Stopped, service.State, "Stop 后服务状态");
        False(service.IsProcessRunning, "Stop 后 MCP Server 子进程不应仍在运行");
        True(states.Contains(McpServerProcessState.Stopped), "Stop 应发布 Stopped 状态");
        True(logs.Any(entry => entry.Message.Contains("MCP Server 已停止", StringComparison.Ordinal)),
            "Stop 后日志应记录服务已停止");
        False(await CanReachHealthAsync(httpClient, healthEndpoint),
            "Stop 后 /health 不应继续可达");
    }

    private static void McpServerNegotiatesAndRunsGenerationTaskLifecycle()
    {
        var dataRoot = Path.Combine(_temporaryRoot, "mcp-protocol");
        var paths = new WorkspacePaths(dataRoot);
        new UserSettingsService(paths).Save(new UserSettingsModel
        {
            ActiveWorkflowKey = "ui",
            AssetGenerationStepKey = "tilemap",
            HasCompletedWorkflowSelection = true,
            TilemapGridUnitSize = 100d,
            TilemapArtImageWidth = 1024,
            TilemapArtImageHeight = 512,
            TilemapDiamondAngle = 90d,
            TilemapRotationAngle = 45d
        });

        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var serverAssemblyPath = Path.Combine(
            repositoryRoot,
            "src",
            "GAssetForge.McpServer",
            "bin",
            configuration,
            "net8.0",
            "GAssetForge.McpServer.dll");
        True(File.Exists(serverAssemblyPath), $"MCP Server 程序集不存在：{serverAssemblyPath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(serverAssemblyPath);
        process.StartInfo.ArgumentList.Add("--data-root");
        process.StartInfo.ArgumentList.Add(dataRoot);

        True(process.Start(), "MCP Server 子进程应能启动");
        try
        {
            using var initialize = CallMcp(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "GAssetForge.SmokeTests", version = "1.0.0" }
                }
            });
            Equal("2.0", initialize.RootElement.GetProperty("jsonrpc").GetString(), "MCP initialize JSON-RPC 版本");
            Equal("2025-06-18",
                initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString(),
                "MCP 协议协商版本");

            SendMcp(process, new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { }
            });

            using var tools = CallMcp(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
                @params = new { }
            });
            var toolNames = tools.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var requiredTool in new[]
                     {
                         "server_info",
                         "get_current_context",
                         "create_generation_task",
                         "list_generation_tasks",
                         "get_generation_task",
                         "claim_generation_task",
                         "complete_generation_task",
                         "fail_generation_task"
                     })
            {
                True(toolNames.Contains(requiredTool), $"MCP tools/list 缺少 {requiredTool}");
            }

            using var context = CallMcpTool(process, 3, "get_current_context", new
            {
                contentPrompt = "苔藓覆盖的古代石质地表瓦片",
                stylePrompt = "手绘奇幻游戏美术"
            });
            var contextPayload = context.RootElement.GetProperty("result").GetProperty("structuredContent");
            Equal("tilemap",
                contextPayload.GetProperty("activeWorkflow").GetProperty("key").GetString(),
                "MCP 应读取素材生成步骤，而不是当前可见业务标签");
            var constraints = contextPayload.GetProperty("generationConstraints").Clone();
            Equal("imagegen", constraints.GetProperty("preferredSkill").GetString(),
                "MCP Tilemap 约束应指定图像生成技能");
            Equal(1024, constraints.GetProperty("output").GetProperty("width").GetInt32(),
                "MCP 应读取持久化的 Tilemap 输出宽度");
            True(constraints.GetProperty("prompt").GetString()!.Contains("顺时针旋转 45°", StringComparison.Ordinal),
                "MCP 提示词应包含当前 Tilemap 旋转规则");

            new UserSettingsService(paths).Save(new UserSettingsModel
            {
                ActiveWorkflowKey = "ui",
                AssetGenerationStepKey = IconDesignConstraintPackageBuilder.WorkflowId,
                HasCompletedWorkflowSelection = true,
                IconDesignModeKey = "SpecifiedAngle",
                IconDesignImageWidth = 768,
                IconDesignImageHeight = 640,
                IconDesignCameraAzimuthDegrees = -35d,
                IconDesignCameraElevationDegrees = 22d,
                IconDesignCameraRollDegrees = 8d,
                IconDesignCameraDistance = 6.25d,
                IconDesignFocalLengthMillimeters = 70d
            });
            using var iconContext = CallMcpTool(process, 30, "get_current_context", new
            {
                contentPrompt = "镶嵌蓝色宝石的银质护符 Icon",
                stylePrompt = "手绘奇幻游戏图标"
            });
            var iconContextPayload = iconContext.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent");
            Equal(IconDesignConstraintPackageBuilder.WorkflowId,
                iconContextPayload.GetProperty("activeGenerationStep").GetProperty("key").GetString(),
                "MCP 应把 Icon 识别为独立素材生成步骤而非项目模板");
            Equal(768,
                iconContextPayload.GetProperty("iconDesign").GetProperty("imageWidth").GetInt32(),
                "MCP 应读取持久化 Icon 画幅宽度");
            var iconConstraints = iconContextPayload.GetProperty("generationConstraints");
            Equal(IconDesignConstraintPackageBuilder.WorkflowId,
                iconConstraints.GetProperty("workflowId").GetString(),
                "MCP Icon 当前约束 workflowId");
            Equal(768, iconConstraints.GetProperty("output").GetProperty("width").GetInt32(),
                "MCP Icon 当前约束画幅宽度");
            Equal(-35d,
                iconConstraints.GetProperty("parameters")
                    .GetProperty("iconDesign")
                    .GetProperty("cameraAzimuthDegrees")
                    .GetDouble(),
                "MCP Icon 当前约束方位角");
            True(iconConstraints.GetProperty("prompt").GetString()!
                    .Contains("不得自行改为等距、正投影或其他角度", StringComparison.Ordinal),
                "MCP Icon 当前约束应包含禁止改角规则");

            using var created = CallMcpTool(process, 4, "create_generation_task", new
            {
                displayName = "Smoke Tilemap Generation",
                requestedBy = "smoke-test-agent",
                constraints
            });
            var createdTask = created.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("task");
            var taskId = createdTask.GetProperty("id").GetString()
                ?? throw new SmokeTestException("MCP 创建任务未返回 taskId。");
            Equal("pending", createdTask.GetProperty("status").GetString(), "MCP 新任务初始状态");

            using var claimed = CallMcpTool(process, 5, "claim_generation_task", new
            {
                taskId,
                agentId = "smoke-image-agent"
            });
            var claimedTask = claimed.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("task");
            Equal("claimed", claimedTask.GetProperty("status").GetString(), "MCP 任务认领状态");
            Equal("smoke-image-agent", claimedTask.GetProperty("claimedBy").GetString(), "MCP 任务认领 Agent");

            using var failed = CallMcpTool(process, 6, "fail_generation_task", new
            {
                taskId,
                agentId = "smoke-image-agent",
                failureMessage = "smoke test intentionally stopped before raster generation"
            });
            Equal("failed",
                failed.RootElement.GetProperty("result").GetProperty("structuredContent")
                    .GetProperty("task").GetProperty("status").GetString(),
                "MCP 任务失败回传状态");

            using var listed = CallMcpTool(process, 7, "list_generation_tasks", new
            {
                status = "failed",
                workflowId = "tilemap"
            });
            var listedPayload = listed.RootElement.GetProperty("result").GetProperty("structuredContent");
            Equal(1, listedPayload.GetProperty("returnedCount").GetInt32(), "MCP 失败任务筛选数量");
            Equal(taskId,
                listedPayload.GetProperty("tasks")[0].GetProperty("id").GetString(),
                "MCP 列表应返回刚完成生命周期验证的任务");
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5_000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }

    private static void ProjectFileSystemBrowserPersistsAndStaysWithinRoot()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "project-file-browser"));
        True(Directory.Exists(paths.AssetDirectory), "WorkspacePaths 应创建默认 Assets 目录");
        True(Directory.Exists(paths.GeneratedAssetDirectory), "WorkspacePaths 应创建 Generated 目录");

        var charactersDirectory = Path.Combine(paths.AssetDirectory, "Characters");
        var heroesDirectory = Path.Combine(charactersDirectory, "Heroes");
        Directory.CreateDirectory(heroesDirectory);
        File.WriteAllText(Path.Combine(paths.AssetDirectory, "preview.PNG"), "image-placeholder");
        File.WriteAllText(Path.Combine(paths.AssetDirectory, "notes.json"), "{}");

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        MainWindow? window = null;

        try
        {
            var browser = shell.ProjectBrowser;
            True(AssetRootService.PathEquals(paths.AssetDirectory, browser.RootPath),
                "Project 浏览器应使用默认 Assets 根目录");
            True(AssetRootService.PathEquals(paths.GeneratedAssetDirectory, browser.CurrentPath),
                "首次启动应把 Generated 作为默认生成输出目录");
            True(browser.SelectDirectory(paths.AssetDirectory), "应能导航到默认 Assets 根目录");

            Equal(5, browser.Entries.Count, "Assets 根目录项目数");
            Equal(3, browser.Entries.Count(entry => entry.IsDirectory), "Assets 根目录文件夹数");
            True(browser.Entries.Take(3).All(entry => entry.IsDirectory),
                "Project 列表应把文件夹排在文件之前");
            True(browser.Entries.Skip(3).All(entry => !entry.IsDirectory),
                "Project 列表中文件不应穿插在文件夹之前");
            True(browser.Entries.Any(entry => entry.IsDirectory && entry.Name == "Generated"),
                "Project 列表应显示 Generated 目录");
            True(browser.Entries.Any(entry => entry.IsDirectory && entry.Name == "Characters"),
                "Project 列表应显示用户创建的目录");
            True(browser.Entries.Any(entry => entry.IsDirectory && entry.Name == "StyleLibrary"),
                "Project 列表应显示应用管理的美术风格素材库目录");
            Equal("图像", browser.Entries.Single(entry => entry.Name == "preview.PNG").KindLabel,
                "图片扩展名分类应忽略大小写");
            Equal("3 个文件夹 · 2 个文件", browser.EntryCountText, "Project 根目录计数文案");
            Equal(1, browser.FolderRoots.Count, "Project 文件夹树根节点数");
            True(browser.FolderRoots[0].Children.Any(node => !node.IsPlaceholder && node.Name == "Characters"),
                "Project 文件夹树应枚举 Assets 子目录");

            True(browser.SelectDirectory(charactersDirectory), "应能进入 Characters 目录");
            True(browser.SelectDirectory(heroesDirectory), "应能进入 Heroes 目录");
            SequenceEqual(
                new[] { "Assets", "Characters", "Heroes" },
                browser.Breadcrumbs.Select(breadcrumb => breadcrumb.Label),
                "Project 面包屑路径");
            True(browser.NavigateUpCommand.CanExecute(null), "Heroes 目录应允许返回上一级");
            browser.NavigateUpCommand.Execute(null);
            True(AssetRootService.PathEquals(charactersDirectory, browser.CurrentPath),
                "返回上一级后应回到 Characters");

            var outsideDirectory = Path.Combine(paths.RootDirectory, "OutsideAssets");
            Directory.CreateDirectory(outsideDirectory);
            var currentPathBeforeRejectedNavigation = browser.CurrentPath;
            False(browser.SelectDirectory(outsideDirectory), "Project 浏览器应拒绝跳出当前资产根目录");
            True(AssetRootService.PathEquals(currentPathBeforeRejectedNavigation, browser.CurrentPath),
                "越界导航被拒绝后当前目录不应改变");

            True(browser.CreateFolderCommand.CanExecute(null), "Project 新建文件夹命令应可执行");
            browser.CreateFolderCommand.Execute(null);
            var createdFolder = Path.Combine(charactersDirectory, "New Folder");
            True(Directory.Exists(createdFolder), "Project 新建文件夹命令应创建真实目录");
            True(browser.Entries.Any(entry => entry.IsDirectory && entry.Name == "New Folder"),
                "新建目录后 Project 列表应立即刷新");

            var externalRoot = Path.Combine(paths.RootDirectory, "ExternalAssets");
            var externalChild = Path.Combine(externalRoot, "Environment");
            Directory.CreateDirectory(externalChild);
            False(AssetRootService.IsWithinRoot(paths.AssetDirectory, externalRoot),
                "外部资产目录必须位于默认 Assets 根目录之外");
            True(browser.TrySetRootPath(externalRoot), "应能切换到有效的外部资产根目录");
            True(AssetRootService.PathEquals(externalRoot, browser.RootPath), "外部资产根目录");
            True(browser.ConfiguredRootPath is { } configuredRoot &&
                 AssetRootService.PathEquals(externalRoot, configuredRoot),
                "外部资产根目录应记录为用户配置");
            True(browser.SelectDirectory(externalChild), "应能导航到外部资产根目录的子目录");

            var persistedSettings = new UserSettingsService(paths).Load();
            True(persistedSettings.AssetRootPath is { } persistedRoot &&
                 AssetRootService.PathEquals(externalRoot, persistedRoot),
                "外部资产根目录应即时保存到用户设置");
            True(persistedSettings.AssetCurrentPath is { } persistedCurrent &&
                 AssetRootService.PathEquals(externalChild, persistedCurrent),
                "当前资产目录应即时保存到用户设置");

            editor.SaveImmediately();
            True(File.Exists(paths.AutosaveProjectPath), "保存 UI 项目后应生成 pfui JSON");
            var projectJson = File.ReadAllText(paths.AutosaveProjectPath)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            False(projectJson.Contains(externalRoot, StringComparison.OrdinalIgnoreCase),
                "资产绝对路径只能进入用户设置，不能写入 pfui 项目 JSON");

            window = new MainWindow(shell, new AvalonDockLayoutService(paths));
            window.Show();
            window.UpdateLayout();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("Project 浏览测试未找到 DockManager。");
            var projectPanel = FindDockContent(manager, MainWindow.ProjectContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("Project 浏览测试未找到 ui-project 面板。");
            var projectHost = projectPanel.Content as ContentControl
                ?? throw new SmokeTestException("ui-project 面板必须由 ContentControl 承载。");
            var projectView = projectHost.Content as ProjectBrowserView
                ?? throw new SmokeTestException("UI 工作流未加载 ProjectBrowserView。");
            True(ReferenceEquals(browser, projectView.DataContext),
                "ProjectBrowserView 应绑定 Shell 的 ProjectBrowser 实例");

            var chooseRootButton = RequireNamedElement<Button>(
                projectView,
                "ChooseAssetRootButton",
                "ProjectBrowserView 缺少选择资产根目录按钮。");
            var useDefaultRootButton = RequireNamedElement<Button>(
                projectView,
                "UseDefaultAssetRootButton",
                "ProjectBrowserView 缺少恢复默认资产根目录按钮。");
            var folderTree = RequireNamedElement<TreeView>(
                projectView,
                "AssetFolderTree",
                "ProjectBrowserView 缺少资产文件夹树。");
            var entryList = RequireNamedElement<ListView>(
                projectView,
                "AssetEntryList",
                "ProjectBrowserView 缺少资产项目列表。");

            True(chooseRootButton.GetBindingExpression(Button.CommandProperty) is not null,
                "选择资产根目录按钮应保留 Command 绑定");
            True(useDefaultRootButton.GetBindingExpression(Button.CommandProperty) is not null,
                "恢复默认资产根目录按钮应保留 Command 绑定");
            True(folderTree.GetBindingExpression(ItemsControl.ItemsSourceProperty) is not null,
                "资产文件夹树应保留 ItemsSource 绑定");
            True(entryList.GetBindingExpression(ItemsControl.ItemsSourceProperty) is not null,
                "资产项目列表应保留 ItemsSource 绑定");
            True(ReferenceEquals(browser.ChooseRootCommand, chooseRootButton.Command),
                "选择资产根目录按钮命令实例");
            True(ReferenceEquals(browser.UseDefaultRootCommand, useDefaultRootButton.Command),
                "恢复默认资产根目录按钮命令实例");
            True(ReferenceEquals(browser.FolderRoots, folderTree.ItemsSource),
                "资产文件夹树数据源");
            True(ReferenceEquals(browser.Entries, entryList.ItemsSource),
                "资产项目列表数据源");

            var assetContextMenu = entryList.ContextMenu
                ?? throw new SmokeTestException("资产项目列表空白区缺少右键菜单。");
            assetContextMenu.PlacementTarget = entryList;
            assetContextMenu.IsOpen = true;
            assetContextMenu.UpdateLayout();
            try
            {
                var refreshContextMenuItem = assetContextMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "RefreshAssetContextMenuItem")
                    ?? throw new SmokeTestException("资产右键菜单缺少刷新项。");
                var openFolderContextMenuItem = assetContextMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "OpenAssetFolderContextMenuItem")
                    ?? throw new SmokeTestException("资产右键菜单缺少在资源管理器中打开项。");
                var testCreateContextMenuItem = assetContextMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "TestCreateAssetContextMenuItem")
                    ?? throw new SmokeTestException("资产右键菜单缺少创建（测试）项。");

                refreshContextMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                openFolderContextMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                testCreateContextMenuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                True(refreshContextMenuItem.GetBindingExpression(MenuItem.CommandProperty) is not null,
                    "资产右键菜单刷新项应保留 Command 绑定");
                True(openFolderContextMenuItem.GetBindingExpression(MenuItem.CommandProperty) is not null,
                    "资产右键菜单打开文件夹项应保留 Command 绑定");
                True(testCreateContextMenuItem.GetBindingExpression(MenuItem.CommandProperty) is not null,
                    "资产右键菜单创建（测试）项应保留 Command 绑定");
                True(ReferenceEquals(browser.RefreshCommand, refreshContextMenuItem.Command),
                    "资产右键菜单刷新命令实例");
                True(ReferenceEquals(browser.OpenCurrentFolderCommand, openFolderContextMenuItem.Command),
                    "资产右键菜单打开文件夹命令实例");
                True(ReferenceEquals(browser.TestCreateCommand, testCreateContextMenuItem.Command),
                    "资产右键菜单创建（测试）命令实例");

                var entriesBeforeTestCreate = Directory.EnumerateFileSystemEntries(browser.CurrentPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                True(testCreateContextMenuItem.Command?.CanExecute(testCreateContextMenuItem.CommandParameter) == true,
                    "资产右键菜单创建（测试）命令应可执行");
                testCreateContextMenuItem.Command!.Execute(testCreateContextMenuItem.CommandParameter);
                var entriesAfterTestCreate = Directory.EnumerateFileSystemEntries(browser.CurrentPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                SequenceEqual(entriesBeforeTestCreate, entriesAfterTestCreate,
                    "创建（测试）占位命令不得新建目录或文件");
                Equal("创建（测试）：占位功能，未修改任何文件", browser.StatusText,
                    "创建（测试）占位状态");
            }
            finally
            {
                assetContextMenu.IsOpen = false;
            }

            True(browser.IsPrefabLibraryAvailable,
                "页面 UI 工作流应允许使用内置 Prefab 页签");

            shell.SelectWorkflow("tilemap");
            window.UpdateLayout();
            False(browser.IsPrefabLibraryAvailable,
                "Tilemap 工作流应禁用 UI 专属的内置 Prefab 页签");
            True(ReferenceEquals(projectView, projectHost.Content),
                "Tilemap 工作流应复用同一个 ProjectBrowserView 实例");
            True(ReferenceEquals(browser, ((ProjectBrowserView)projectHost.Content).DataContext),
                "Tilemap 工作流应复用同一个 ProjectBrowserViewModel 实例");
            shell.SelectWorkflow("ui");
            window.UpdateLayout();
            True(browser.IsPrefabLibraryAvailable,
                "切回页面 UI 工作流后应恢复内置 Prefab 页签");
            True(ReferenceEquals(projectView, projectHost.Content),
                "切回 UI 工作流后应继续复用 ProjectBrowserView 实例");
        }
        finally
        {
            window?.Close();
            shell.Shutdown();
        }

        var restoredEditor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var restoredShell = new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(paths),
            restoredEditor);

        try
        {
            var restoredBrowser = restoredShell.ProjectBrowser;
            var expectedExternalRoot = Path.Combine(paths.RootDirectory, "ExternalAssets");
            var expectedExternalChild = Path.Combine(expectedExternalRoot, "Environment");
            True(AssetRootService.PathEquals(expectedExternalRoot, restoredBrowser.RootPath),
                "重启后应恢复外部资产根目录");
            True(AssetRootService.PathEquals(expectedExternalChild, restoredBrowser.CurrentPath),
                "重启后应恢复最后浏览的资产目录");
        }
        finally
        {
            restoredShell.Shutdown();
        }
    }

    private static void ArtStyleLibraryDockPanelImportsPersistsAndRemovesAssets()
    {
        StyleAnalysisCommandTracksAssetAvailability();

        var dockPaths = new WorkspacePaths(Path.Combine(_temporaryRoot, "art-style-library-dock"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(dockPaths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(dockPaths), editor);
        var window = new MainWindow(shell, new AvalonDockLayoutService(dockPaths));

        try
        {
            window.Show();
            window.UpdateLayout();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("美术风格素材库测试未找到 DockManager。");
            var stylePanel = FindDockContent(manager, MainWindow.StyleLibraryContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("默认布局缺少美术风格素材库停靠面板。");
            var projectPanel = FindDockContent(manager, MainWindow.ProjectContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("美术风格素材库测试未找到 Project 面板。");
            Equal("美术风格素材库", stylePanel.Title, "美术风格素材库面板标题");
            True(ReferenceEquals(stylePanel.Parent, projectPanel.Parent),
                "美术风格素材库默认应与 Project 共用底部标签停靠区");
            True(stylePanel.CanMove && stylePanel.CanFloat && stylePanel.CanHide && stylePanel.CanAutoHide,
                "美术风格素材库应支持拖动、浮动、隐藏与自动隐藏");

            shell.ShowPanelCommand.Execute(MainWindow.StyleLibraryContentId);
            window.UpdateLayout();
            True(stylePanel.IsVisible && stylePanel.IsSelected && stylePanel.IsActive,
                "从视图命令拉起后美术风格素材库应可见、选中并激活");

            var styleHost = stylePanel.Content as ContentControl
                ?? throw new SmokeTestException("美术风格素材库应由稳定 ContentControl host 承载。");
            var styleView = styleHost.Content as ArtStyleLibraryView
                ?? throw new SmokeTestException("美术风格素材库面板未承载 ArtStyleLibraryView。");
            True(ReferenceEquals(shell.ArtStyleLibrary, styleView.DataContext),
                "ArtStyleLibraryView 应绑定 Shell 持有的单实例 ViewModel");
            True(styleView.MinWidth >= 540d && styleView.MinHeight >= 380d,
                "美术风格素材库应具有可用的最小停靠尺寸");

            var groupList = RequireNamedElement<ListBox>(
                styleView,
                "StyleGroupList",
                "美术风格素材库缺少风格组列表。");
            var assetList = RequireNamedElement<ListBox>(
                styleView,
                "StyleAssetList",
                "美术风格素材库缺少参考素材列表。");
            var createButton = RequireNamedElement<Button>(
                styleView,
                "NewStyleGroupButton",
                "美术风格素材库缺少新建风格组按钮。");
            var importButton = RequireNamedElement<Button>(
                styleView,
                "ImportStyleAssetsButton",
                "美术风格素材库缺少导入素材按钮。");
            var removeButton = RequireNamedElement<Button>(
                styleView,
                "RemoveStyleAssetButton",
                "美术风格素材库缺少移除素材按钮。");
            var analyzeUnifiedPromptButton = RequireNamedElement<Button>(
                styleView,
                "AnalyzeUnifiedStylePromptButton",
                "美术风格素材库缺少分析生成统一提示词按钮。");
            var nameTextBox = RequireNamedElement<TextBox>(
                styleView,
                "StyleNameTextBox",
                "美术风格素材库缺少风格组名称输入框。");
            var descriptionTextBox = RequireNamedElement<TextBox>(
                styleView,
                "StyleDescriptionTextBox",
                "美术风格素材库缺少风格描述输入框。");
            var promptTextBox = RequireNamedElement<TextBox>(
                styleView,
                "StylePromptTextBox",
                "美术风格素材库缺少 AI 风格提示词输入框。");
            var unifiedPromptTextBox = RequireNamedElement<TextBox>(
                styleView,
                "UnifiedStylePromptTextBox",
                "美术风格素材库缺少统一提示词样本只读框。");

            True(ReferenceEquals(shell.ArtStyleLibrary.Groups, groupList.ItemsSource),
                "风格组列表应绑定风格库 Groups");
            True(ReferenceEquals(shell.ArtStyleLibrary.SelectedGroup?.Assets, assetList.ItemsSource),
                "参考素材列表应绑定当前风格组 Assets");
            True(ReferenceEquals(shell.ArtStyleLibrary.CreateGroupCommand, createButton.Command),
                "新建风格组按钮命令实例");
            True(ReferenceEquals(shell.ArtStyleLibrary.ImportAssetsCommand, importButton.Command),
                "导入风格素材按钮命令实例");
            True(ReferenceEquals(shell.ArtStyleLibrary.RemoveAssetCommand, removeButton.Command),
                "移除风格素材按钮命令实例");
            True(ReferenceEquals(
                    shell.ArtStyleLibrary.AnalyzeUnifiedPromptCommand,
                    analyzeUnifiedPromptButton.Command),
                "分析生成统一提示词按钮命令实例");
            Equal(
                nameof(ArtStyleLibraryViewModel.AnalyzeUnifiedPromptCommand),
                BindingOperations.GetBinding(analyzeUnifiedPromptButton, Button.CommandProperty)?.Path?.Path,
                "分析生成统一提示词按钮命令绑定路径");
            True(nameTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "风格组名称应保留双向绑定");
            True(descriptionTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "风格描述应保留双向绑定");
            True(promptTextBox.GetBindingExpression(TextBox.TextProperty) is not null,
                "AI 风格提示词应保留双向绑定");
            True(unifiedPromptTextBox.IsReadOnly,
                "统一提示词样本必须保持只读，避免绕过分析结果元数据直接修改");
            True(ReferenceEquals(shell.ArtStyleLibrary.SelectedGroup, unifiedPromptTextBox.DataContext),
                "统一提示词样本应绑定当前选中的风格组");
            Equal(
                nameof(ArtStyleGroupViewModel.UnifiedPromptText),
                BindingOperations.GetBinding(unifiedPromptTextBox, TextBox.TextProperty)?.Path?.Path,
                "统一提示词样本文本绑定路径");

            var menuItem = window.FindName("ShowStyleLibraryMenuItem") as MenuItem
                ?? throw new SmokeTestException("视图菜单缺少美术风格素材库入口。");
            menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(menuItem.IsCheckable, "美术风格素材库菜单应为可勾选开关");
            True(ReferenceEquals(shell.TogglePanelCommand, menuItem.Command),
                "美术风格素材库菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.StyleLibraryContentId, menuItem.CommandParameter as string,
                "美术风格素材库菜单 ContentId");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }

        var dataPaths = new WorkspacePaths(Path.Combine(_temporaryRoot, "art-style-library-data"));
        var sourceDirectory = Path.Combine(dataPaths.RootDirectory, "ImportSources");
        Directory.CreateDirectory(sourceDirectory);
        var pngBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var firstSource = Path.Combine(sourceDirectory, "dungeon-reference.png");
        var secondSource = Path.Combine(sourceDirectory, "dungeon-lighting.png");
        File.WriteAllBytes(firstSource, pngBytes);
        var secondBytes = new byte[pngBytes.Length + 1];
        Buffer.BlockCopy(pngBytes, 0, secondBytes, 0, pngBytes.Length);
        secondBytes[^1] = 1;
        File.WriteAllBytes(secondSource, secondBytes);

        var picker = new FixedArtStyleAssetPicker([firstSource, secondSource]);
        var library = new ArtStyleLibraryViewModel(dataPaths, picker);
        try
        {
            Equal(1, library.Groups.Count, "首次启动应创建默认风格组");
            var group = library.SelectedGroup
                ?? throw new SmokeTestException("默认风格组应成为当前风格。");
            group.Name = "像素地牢";
            group.Description = "低饱和石墙、清晰块面与暖色火把照明。";
            group.StylePrompt = "pixel art, low saturation stone, warm torch light, crisp silhouettes";

            True(library.ImportAssetsCommand.CanExecute(null), "选中风格组后应允许导入素材");
            library.ImportAssetsCommand.Execute(null);
            Equal(2, group.Assets.Count, "一次应能导入多张风格参考素材");
            True(group.Assets.All(asset => File.Exists(asset.FullPath)),
                "导入后所有风格素材应复制到应用管理目录");
            True(group.Assets.All(asset => AssetRootService.IsWithinRoot(library.ManagedAssetRoot, asset.FullPath)),
                "风格素材不得落到应用管理目录之外");
            True(File.Exists(firstSource) && File.Exists(secondSource),
                "导入不得移动或删除用户原始图片");
            True(File.Exists(library.LibraryFilePath), "风格库清单应即时写入本地 JSON");

            var assetToRemove = group.Assets[0];
            var cachedThumbnail = new FilePathToImageSourceConverter().Convert(
                assetToRemove.FullPath,
                typeof(ImageSource),
                "64",
                System.Globalization.CultureInfo.InvariantCulture);
            True(cachedThumbnail is BitmapSource, "受管参考图应能解码为不会锁文件的内存缩略图");
            library.SelectedAsset = assetToRemove;
            True(library.RemoveAssetCommand.CanExecute(null), "选中素材后应允许移除");
            library.RemoveAssetCommand.Execute(null);
            Equal(1, group.Assets.Count, "移除后当前风格组应保留其余素材");
            False(File.Exists(assetToRemove.FullPath),
                "持有内存缩略图时也应能删除对应受管图片文件");

            library.CreateGroupCommand.Execute(null);
            Equal(2, library.Groups.Count, "应能创建第二个风格组");
            var temporaryGroup = library.SelectedGroup
                ?? throw new SmokeTestException("新建风格组应自动成为当前选择。");
            temporaryGroup.Name = "临时水彩风格";
            library.DeleteGroupCommand.Execute(null);
            Equal(1, library.Groups.Count, "应能删除当前风格组并回到剩余风格");
            Equal("像素地牢", library.SelectedGroup?.Name, "删除后当前风格选择");
        }
        finally
        {
            library.Shutdown();
        }

        var restored = new ArtStyleLibraryViewModel(dataPaths, new FixedArtStyleAssetPicker([]));
        try
        {
            Equal(1, restored.Groups.Count, "重启后风格组数量");
            var restoredGroup = restored.SelectedGroup
                ?? throw new SmokeTestException("重启后应恢复当前风格组选择。");
            Equal("像素地牢", restoredGroup.Name, "重启后风格组名称");
            Equal("低饱和石墙、清晰块面与暖色火把照明。", restoredGroup.Description,
                "重启后风格描述");
            Equal("pixel art, low saturation stone, warm torch light, crisp silhouettes",
                restoredGroup.StylePrompt,
                "重启后 AI 风格提示词");
            Equal(1, restoredGroup.Assets.Count, "重启后风格参考素材数量");
            True(restored.SelectedAsset is not null && File.Exists(restored.SelectedAsset.FullPath),
                "重启后应恢复当前参考素材及其受管文件");
        }
        finally
        {
            restored.Shutdown();
        }
    }

    private static void StyleAnalysisCommandTracksAssetAvailability()
    {
        var paths = new WorkspacePaths(Path.Combine(
            _temporaryRoot,
            "art-style-analysis-command-state"));
        var sourceDirectory = Path.Combine(paths.RootDirectory, "ImportSources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "single-reference.png");
        File.WriteAllBytes(
            sourcePath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        var library = new ArtStyleLibraryViewModel(
            paths,
            new FixedArtStyleAssetPicker([sourcePath]));
        try
        {
            library.ConfigurePromptAnalysisService(new NeverRunArtStylePromptAnalysisService());
            False(library.AnalyzeUnifiedPromptCommand.CanExecute(null),
                "空风格组不应允许分析统一提示词");

            library.ImportAssetsCommand.Execute(null);
            True(library.AnalyzeUnifiedPromptCommand.CanExecute(null),
                "首次导入参考图后应立即启用统一提示词分析命令");

            library.SelectedAsset = library.SelectedGroup?.Assets.Single();
            library.RemoveAssetCommand.Execute(null);
            False(library.AnalyzeUnifiedPromptCommand.CanExecute(null),
                "移除最后一张参考图后应立即禁用统一提示词分析命令");
        }
        finally
        {
            library.Shutdown();
        }
    }

    private static void AssetGenerationDockPanelBindsWorkflowAndTracksMenuState()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "asset-generation-dock"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        shell.SelectWorkflow("tilemap");
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            var manager = RequireNamedElement<DockingManager>(
                window,
                "DockManager",
                "素材生成停靠页测试未找到 DockManager。");
            var generationPanel = FindDockContent(manager, MainWindow.AssetGenerationContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("默认布局缺少素材生成停靠面板。");
            var projectPanel = FindDockContent(manager, MainWindow.ProjectContentId) as LayoutAnchorable
                ?? throw new SmokeTestException("素材生成停靠页测试未找到 Project 面板。");
            Equal("素材生成", generationPanel.Title, "素材生成面板标题");
            True(ReferenceEquals(generationPanel.Parent, projectPanel.Parent),
                "素材生成默认应与 Project 共用底部标签停靠区");
            True(generationPanel.CanMove && generationPanel.CanFloat && generationPanel.CanHide &&
                 generationPanel.CanAutoHide && !generationPanel.CanClose,
                "素材生成应支持拖动、浮动、隐藏与自动隐藏，但不可永久关闭");

            var menuItem = RequireNamedElement<MenuItem>(
                window,
                "ShowAssetGenerationMenuItem",
                "视图菜单缺少素材生成入口。");
            menuItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
            True(menuItem.IsCheckable, "素材生成菜单应为可勾选开关");
            True(ReferenceEquals(shell.TogglePanelCommand, menuItem.Command),
                "素材生成菜单应绑定 TogglePanelCommand");
            Equal(MainWindow.AssetGenerationContentId, menuItem.CommandParameter as string,
                "素材生成菜单 ContentId");
            True(menuItem.IsChecked, "素材生成面板初始存在时视图菜单应处于勾选状态");

            var openButton = RequireNamedElement<Button>(
                window,
                "OpenAssetGenerationButton",
                "顶部工具栏缺少素材生成入口。");
            openButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            True(ReferenceEquals(shell.ShowPanelCommand, openButton.Command),
                "顶部素材生成按钮应绑定 ShowPanelCommand");
            Equal(MainWindow.AssetGenerationContentId, openButton.CommandParameter as string,
                "顶部素材生成按钮 ContentId");

            menuItem.Command!.Execute(menuItem.CommandParameter);
            True(generationPanel.IsHidden, "点击视图开关后素材生成面板应隐藏");
            False(menuItem.IsChecked, "素材生成面板隐藏后视图菜单应取消勾选");
            openButton.Command!.Execute(openButton.CommandParameter);
            window.UpdateLayout();
            PumpDispatcher();
            False(generationPanel.IsHidden, "顶部素材生成按钮应恢复隐藏面板");
            True(generationPanel.IsVisible && generationPanel.IsSelected && generationPanel.IsActive,
                "顶部素材生成按钮拉起后面板应可见、选中并激活");
            True(menuItem.IsChecked, "顶部按钮恢复素材生成面板后视图菜单应重新勾选");

            var generationHost = generationPanel.Content as ContentControl
                ?? throw new SmokeTestException("素材生成面板应由稳定 ContentControl host 承载。");
            var generationView = generationHost.Content as AssetGenerationView
                ?? throw new SmokeTestException("素材生成面板未承载 AssetGenerationView。");
            generationView.ApplyTemplate();
            generationView.UpdateLayout();
            True(ReferenceEquals(shell.AssetGeneration, generationView.DataContext),
                "AssetGenerationView 应绑定 Shell 持有的单实例 ViewModel");
            Equal(480d, generationView.MinWidth, "素材生成停靠页应允许较窄窗口使用纵向布局");
            True(generationView.MinHeight >= 420d, "素材生成停靠页应具有可用的最小停靠高度");

            var generationPageScroller = RequireNamedElement<ScrollViewer>(
                generationView,
                "AssetGenerationPageScrollViewer",
                "素材生成页缺少纵向页面滚动容器。");
            var generationVerticalFlow = RequireNamedElement<StackPanel>(
                generationView,
                "AssetGenerationVerticalFlowPanel",
                "素材生成页缺少纵向内容流。");
            var generationSetupSection = RequireNamedElement<StackPanel>(
                generationView,
                "AssetGenerationSetupSection",
                "素材生成页缺少生成设置区。");
            var generationBatchSection = RequireNamedElement<StackPanel>(
                generationView,
                "AssetGenerationBatchAndArtifactsSection",
                "素材生成页缺少批次与产物区。");
            Equal(ScrollBarVisibility.Disabled, generationPageScroller.HorizontalScrollBarVisibility,
                "素材生成整页不得依赖底部横向滚动条");
            Equal(ScrollBarVisibility.Auto, generationPageScroller.VerticalScrollBarVisibility,
                "素材生成整页应以上下滚动为主");
            True(generationVerticalFlow.Children.IndexOf(generationSetupSection) >= 0 &&
                 generationVerticalFlow.Children.IndexOf(generationSetupSection) <
                 generationVerticalFlow.Children.IndexOf(generationBatchSection),
                "生成设置、当前批次与生成产物必须按顺序从上到下排列");

            var compactGenerationView = new AssetGenerationView { DataContext = shell.AssetGeneration };
            compactGenerationView.Measure(new Size(480, 720));
            compactGenerationView.Arrange(new Rect(0, 0, 480, 720));
            compactGenerationView.UpdateLayout();
            PumpDispatcher();
            var compactGenerationScroller = RequireNamedElement<ScrollViewer>(
                compactGenerationView,
                "AssetGenerationPageScrollViewer",
                "紧凑素材生成页缺少页面滚动容器。");
            True(compactGenerationScroller.ScrollableWidth <= 0.5d,
                "480px 紧凑宽度下整页不应产生横向滚动范围");
            True(compactGenerationScroller.ScrollableHeight > 0d,
                "紧凑高度下素材生成页应通过纵向滚动访问后续区块");
            var compactImportButton = RequireNamedElement<Button>(
                compactGenerationView,
                "ImportGenerationSpreadsheetButton",
                "紧凑素材生成页缺少 Excel 导入按钮。");
            var compactAddRowButton = RequireNamedElement<Button>(
                compactGenerationView,
                "AddGenerationRowButton",
                "紧凑素材生成页缺少新建任务行按钮。");
            var compactImportBounds = compactImportButton.TransformToAncestor(compactGenerationView)
                .TransformBounds(new Rect(compactImportButton.RenderSize));
            var compactAddRowBounds = compactAddRowButton.TransformToAncestor(compactGenerationView)
                .TransformBounds(new Rect(compactAddRowButton.RenderSize));
            True(compactImportButton.ActualWidth > 0d &&
                 compactImportBounds.Left >= -0.5d &&
                 compactImportBounds.Right <= compactGenerationView.ActualWidth + 0.5d,
                "480px 紧凑宽度下 Excel 导入按钮不得被页面右边界裁切");
            True(compactAddRowButton.ActualWidth > 0d &&
                 compactAddRowBounds.Left >= -0.5d &&
                 compactAddRowBounds.Right <= compactGenerationView.ActualWidth + 0.5d,
                "480px 紧凑宽度下新建任务行按钮不得被页面右边界裁切");

            var styleComboBox = RequireNamedElement<ComboBox>(
                generationView,
                "AssetGenerationStyleComboBox",
                "素材生成页缺少美术风格组下拉框。");
            var stepComboBox = RequireNamedElement<ComboBox>(
                generationView,
                "AssetGenerationStepComboBox",
                "素材生成页缺少生成步骤下拉框。");
            True(generationView.FindName("AssetGenerationParallelismComboBox") is null,
                "素材生成页应移除用户可控并行图片数量入口");
            var contentPromptTextBox = RequireNamedElement<TextBox>(
                generationView,
                "AssetGenerationContentPromptTextBox",
                "素材生成页缺少生成内容输入框。");
            var importSpreadsheetButton = RequireNamedElement<Button>(
                generationView,
                "ImportGenerationSpreadsheetButton",
                "素材生成页缺少 Excel 描述导入按钮。");
            var spreadsheetPreviewPanel = RequireNamedElement<Border>(
                generationView,
                "AssetGenerationSpreadsheetPreviewPanel",
                "素材生成页缺少 Excel 风格预览面板。");
            var workbookSheetComboBox = RequireNamedElement<ComboBox>(
                generationView,
                "AssetGenerationWorkbookSheetComboBox",
                "Excel 预览缺少工作表选择器。");
            var descriptionColumnComboBox = RequireNamedElement<ComboBox>(
                generationView,
                "AssetGenerationDescriptionColumnComboBox",
                "Excel 预览缺少物品描述列选择器。");
            var spreadsheetPreviewGrid = RequireNamedElement<DataGrid>(
                generationView,
                "AssetGenerationSpreadsheetPreviewGrid",
                "Excel 预览缺少表格网格。");
            var appendWorkbookRowsButton = RequireNamedElement<Button>(
                generationView,
                "AppendSelectedWorkbookRowsButton",
                "Excel 预览缺少追加任务条按钮。");
            var replaceWorkbookRowsButton = RequireNamedElement<Button>(
                generationView,
                "ReplaceWithSelectedWorkbookRowsButton",
                "Excel 预览缺少替换任务表按钮。");
            var negativePromptTextBox = RequireNamedElement<TextBox>(
                generationView,
                "AssetGenerationNegativePromptTextBox",
                "素材生成页缺少负向提示词输入框。");
            var chooseOutputButton = RequireNamedElement<Button>(
                generationView,
                "ChooseGenerationOutputButton",
                "素材生成页缺少选择输出目录按钮。");
            var useProjectOutputButton = RequireNamedElement<Button>(
                generationView,
                "UseProjectOutputButton",
                "素材生成页缺少使用项目目录按钮。");
            var constraintPreviewTextBox = RequireNamedElement<TextBox>(
                generationView,
                "GenerationConstraintPreviewTextBox",
                "素材生成页缺少最终约束预览框。");
            var createTaskButton = RequireNamedElement<Button>(
                generationView,
                "CreateGenerationTaskButton",
                "素材生成页缺少创建任务按钮。");
            var startGenerationButton = RequireNamedElement<Button>(
                generationView,
                "StartGenerationButton",
                "素材生成页缺少开始生成按钮。");
            var refreshTaskButton = RequireNamedElement<Button>(
                generationView,
                "RefreshGenerationTaskButton",
                "素材生成页缺少刷新任务按钮。");
            var generatedAssetList = RequireNamedElement<ListBox>(
                generationView,
                "GeneratedAssetList",
                "素材生成页缺少生成产物列表。");
            var dispatchProgressBar = RequireNamedElement<ProgressBar>(
                generationView,
                "GenerationDispatchProgressBar",
                "当前批次缺少调度步骤进度条。");
            var dispatchStageText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationDispatchStageText",
                "当前批次缺少调度步骤名称。");
            var dispatchProgressText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationDispatchProgressText",
                "当前批次缺少调度百分比文本。");
            var currentInstructionText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationCurrentInstructionText",
                "当前批次缺少当前执行指令描述。");
            var dispatchStageDetailText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationDispatchStageDetailText",
                "当前批次缺少调度步骤详情。");
            var dispatchFailureStageText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationDispatchFailureStageText",
                "当前批次缺少失败步骤名称。");
            var dispatchFailureText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationDispatchFailureText",
                "当前批次缺少失败步骤诊断文本。");
            var imageProgressBar = RequireNamedElement<ProgressBar>(
                generationView,
                "GenerationImageProgressBar",
                "当前批次缺少图片数量进度条。");
            var imageProgressText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationImageProgressText",
                "当前批次缺少图片数量进度文本。");
            var currentImageText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationCurrentImageText",
                "当前批次缺少当前图片目标文本。");
            var agentFeedbackStatusText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationAgentFeedbackStatusText",
                "当前批次缺少 Agent 最近活动状态。");
            var agentRiskText = RequireNamedElement<TextBlock>(
                generationView,
                "GenerationAgentGenerationRiskText",
                "当前批次缺少 Agent 风险诊断文本。");
            var agentFeedbackList = RequireNamedElement<ListBox>(
                generationView,
                "GenerationAgentFeedbackList",
                "当前批次缺少 Agent 实时反馈列表。");

            True(ReferenceEquals(shell.AssetGeneration.StyleGroups, styleComboBox.ItemsSource),
                "风格组下拉框应绑定 StyleGroups");
            True(ReferenceEquals(shell.AssetGeneration.SelectedStyleGroup, styleComboBox.SelectedItem),
                "风格组下拉框应同步 SelectedStyleGroup");
            Equal(nameof(ArtStyleGroupViewModel.Name), styleComboBox.DisplayMemberPath,
                "风格组下拉框显示字段");
            True(ReferenceEquals(shell.AssetGeneration.GenerationSteps, stepComboBox.ItemsSource),
                "生成步骤下拉框应绑定 GenerationSteps");
            True(ReferenceEquals(shell.AssetGeneration.SelectedGenerationStep, stepComboBox.SelectedItem),
                "生成步骤下拉框应同步 SelectedGenerationStep");
            Equal(nameof(AssetGenerationStepOption.DisplayName), stepComboBox.DisplayMemberPath,
                "生成步骤下拉框显示字段");
            var contentPromptBinding = BindingOperations.GetBinding(contentPromptTextBox, TextBox.TextProperty);
            Equal(nameof(AssetGenerationViewModel.ContentPrompt), contentPromptBinding?.Path?.Path,
                "生成内容输入框绑定路径");
            Equal(BindingMode.TwoWay, contentPromptBinding?.Mode ?? BindingMode.Default,
                "生成内容输入框绑定模式");
            Equal(nameof(AssetGenerationViewModel.ImportDescriptionSpreadsheetCommand),
                BindingOperations.GetBinding(importSpreadsheetButton, Button.CommandProperty)?.Path?.Path,
                "Excel 导入按钮命令绑定路径");
            Equal(nameof(AssetGenerationViewModel.HasSpreadsheetPreview),
                BindingOperations.GetBinding(spreadsheetPreviewPanel, UIElement.VisibilityProperty)?.Path?.Path,
                "Excel 预览面板可见性绑定路径");
            Equal(nameof(AssetGenerationViewModel.ImportedWorksheets),
                BindingOperations.GetBinding(workbookSheetComboBox, ItemsControl.ItemsSourceProperty)?.Path?.Path,
                "Excel 工作表选择器数据源绑定路径");
            Equal(nameof(AssetGenerationViewModel.SelectedImportedWorksheet),
                BindingOperations.GetBinding(workbookSheetComboBox, Selector.SelectedItemProperty)?.Path?.Path,
                "Excel 工作表选择绑定路径");
            Equal(nameof(AssetGenerationViewModel.ImportedColumns),
                BindingOperations.GetBinding(descriptionColumnComboBox, ItemsControl.ItemsSourceProperty)?.Path?.Path,
                "Excel 描述列选择器数据源绑定路径");
            Equal(nameof(AssetGenerationViewModel.SelectedDescriptionColumn),
                BindingOperations.GetBinding(descriptionColumnComboBox, Selector.SelectedItemProperty)?.Path?.Path,
                "Excel 描述列选择绑定路径");
            Equal(nameof(AssetGenerationViewModel.ImportedRows),
                BindingOperations.GetBinding(spreadsheetPreviewGrid, ItemsControl.ItemsSourceProperty)?.Path?.Path,
                "Excel 预览表格行绑定路径");
            Equal(ScrollBarVisibility.Auto, spreadsheetPreviewGrid.HorizontalScrollBarVisibility,
                "Excel 预览横向滚动应限制在表格内部");
            True(spreadsheetPreviewGrid.CanUserResizeColumns &&
                 spreadsheetPreviewGrid.GridLinesVisibility == DataGridGridLinesVisibility.All,
                "Excel 预览应支持拖动列宽并显示完整单元格网格");
            Equal(nameof(AssetGenerationViewModel.AppendSelectedImportedRowsCommand),
                BindingOperations.GetBinding(appendWorkbookRowsButton, Button.CommandProperty)?.Path?.Path,
                "追加 Excel 行命令绑定路径");
            Equal(nameof(AssetGenerationViewModel.ReplaceDraftRowsWithImportedRowsCommand),
                BindingOperations.GetBinding(replaceWorkbookRowsButton, Button.CommandProperty)?.Path?.Path,
                "替换任务表命令绑定路径");
            var negativePromptBinding = BindingOperations.GetBinding(negativePromptTextBox, TextBox.TextProperty);
            Equal(nameof(AssetGenerationViewModel.NegativePrompt), negativePromptBinding?.Path?.Path,
                "负向提示词输入框绑定路径");
            Equal(BindingMode.TwoWay, negativePromptBinding?.Mode ?? BindingMode.Default,
                "负向提示词输入框绑定模式");
            Equal(nameof(AssetGenerationViewModel.ConstraintPreview),
                BindingOperations.GetBinding(constraintPreviewTextBox, TextBox.TextProperty)?.Path?.Path,
                "最终约束预览绑定路径");
            True(constraintPreviewTextBox.IsReadOnly, "最终约束预览应为只读内容");

            chooseOutputButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            useProjectOutputButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            createTaskButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            startGenerationButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            refreshTaskButton.GetBindingExpression(Button.CommandProperty)?.UpdateTarget();
            True(ReferenceEquals(shell.AssetGeneration.ChooseOutputDirectoryCommand, chooseOutputButton.Command),
                "选择输出目录按钮命令实例");
            True(ReferenceEquals(shell.AssetGeneration.UseProjectCurrentDirectoryCommand, useProjectOutputButton.Command),
                "使用项目目录按钮命令实例");
            True(ReferenceEquals(shell.AssetGeneration.CreateTaskCommand, createTaskButton.Command),
                "仅发布队列按钮命令实例");
            True(ReferenceEquals(shell.AssetGeneration.StartGenerationCommand, startGenerationButton.Command),
                "开始生成按钮命令实例");
            False(startGenerationButton.Command?.CanExecute(startGenerationButton.CommandParameter) == true,
                "Agent 链路未就绪时开始生成按钮不得执行");
            True(ReferenceEquals(shell.AssetGeneration.RefreshTaskCommand, refreshTaskButton.Command),
                "刷新生成任务按钮命令实例");
            True(ReferenceEquals(shell.AssetGeneration.GeneratedAssets, generatedAssetList.ItemsSource),
                "生成产物列表应绑定 GeneratedAssets");
            Equal(nameof(AssetGenerationViewModel.DispatchProgress),
                BindingOperations.GetBinding(dispatchProgressBar, RangeBase.ValueProperty)?.Path?.Path,
                "调度进度条绑定路径");
            Equal(nameof(AssetGenerationViewModel.CurrentDispatchStageText),
                BindingOperations.GetBinding(dispatchStageText, TextBlock.TextProperty)?.Path?.Path,
                "调度步骤名称绑定路径");
            Equal(nameof(AssetGenerationViewModel.DispatchProgressText),
                BindingOperations.GetBinding(dispatchProgressText, TextBlock.TextProperty)?.Path?.Path,
                "调度百分比绑定路径");
            Equal(nameof(AssetGenerationViewModel.CurrentInstruction),
                BindingOperations.GetBinding(currentInstructionText, TextBlock.TextProperty)?.Path?.Path,
                "当前执行指令绑定路径");
            Equal(nameof(AssetGenerationViewModel.DispatchStageDetail),
                BindingOperations.GetBinding(dispatchStageDetailText, TextBlock.TextProperty)?.Path?.Path,
                "调度步骤详情绑定路径");
            Equal(nameof(AssetGenerationViewModel.FailureStageText),
                BindingOperations.GetBinding(dispatchFailureStageText, TextBlock.TextProperty)?.Path?.Path,
                "失败步骤名称绑定路径");
            Equal(nameof(AssetGenerationViewModel.DispatchFailureText),
                BindingOperations.GetBinding(dispatchFailureText, TextBlock.TextProperty)?.Path?.Path,
                "失败步骤诊断绑定路径");
            Equal(nameof(AssetGenerationViewModel.ImageProgress),
                BindingOperations.GetBinding(imageProgressBar, RangeBase.ValueProperty)?.Path?.Path,
                "图片进度条绑定路径");
            Equal(nameof(AssetGenerationViewModel.ImageProgressText),
                BindingOperations.GetBinding(imageProgressText, TextBlock.TextProperty)?.Path?.Path,
                "图片进度文本绑定路径");
            Equal(nameof(AssetGenerationViewModel.CurrentImageText),
                BindingOperations.GetBinding(currentImageText, TextBlock.TextProperty)?.Path?.Path,
                "当前图片目标绑定路径");
            Equal(nameof(AssetGenerationViewModel.AgentFeedbackStatusText),
                BindingOperations.GetBinding(agentFeedbackStatusText, TextBlock.TextProperty)?.Path?.Path,
                "Agent 最近活动绑定路径");
            Equal(nameof(AssetGenerationViewModel.AgentGenerationRiskText),
                BindingOperations.GetBinding(agentRiskText, TextBlock.TextProperty)?.Path?.Path,
                "Agent 风险诊断绑定路径");
            True(ReferenceEquals(shell.AssetGeneration.AgentFeedbackEntries, agentFeedbackList.ItemsSource),
                "Agent 实时反馈列表应绑定 AgentFeedbackEntries");

            _ = RequireBoundElement<TextBlock>(
                generationView,
                TextBlock.TextProperty,
                nameof(AssetGenerationViewModel.ConnectionStatusText),
                "素材生成页缺少 MCP 连接状态绑定。");
            _ = RequireBoundElement<TextBlock>(
                generationView,
                TextBlock.TextProperty,
                nameof(AssetGenerationViewModel.CanvasSizeText),
                "素材生成页缺少 Tilemap 画幅绑定。");
            _ = RequireBoundElement<TextBlock>(
                generationView,
                TextBlock.TextProperty,
                nameof(AssetGenerationViewModel.GenerationPerspectiveText),
                "素材生成页缺少当前生成步骤的相机与透视绑定。");
            _ = RequireBoundElement<TextBox>(
                generationView,
                TextBox.TextProperty,
                nameof(AssetGenerationViewModel.OutputDirectory),
                "素材生成页缺少输出目录绑定。");
            _ = RequireBoundElement<TextBlock>(
                generationView,
                TextBlock.TextProperty,
                nameof(AssetGenerationViewModel.OutputDirectoryHint),
                "素材生成页缺少输出目录提示绑定。");
            _ = RequireBoundElement<TextBlock>(
                generationView,
                TextBlock.TextProperty,
                nameof(AssetGenerationViewModel.StatusText),
                "素材生成页缺少状态文本绑定。");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void GenerationReferenceSnapshotsFailFastAndStreamHashes()
    {
        Equal(128, MaterialGenerationConstraintComposer.MaximumReferenceCount,
            "生成约束公开的风格参考图数量上限");
        Equal(
            ArtStyleLibraryValidator.MaximumAssetImportBatchBytes,
            GenerationReferenceSnapshotService.MaximumTaskReferenceBytes,
            "生成快照任务总字节上限应与风格库批次契约对齐");

        var overLimitPaths = new WorkspacePaths(Path.Combine(
            _temporaryRoot,
            "generation-reference-snapshot-over-limit"));
        var overLimitService = new GenerationReferenceSnapshotService(overLimitPaths);
        var overLimitSources = Enumerable.Range(
                0,
                MaterialGenerationConstraintComposer.MaximumReferenceCount + 1)
            .Select(index => new GenerationReferenceSource(
                Guid.NewGuid().ToString("N"),
                $"reference-{index}",
                Path.Combine(overLimitPaths.RootDirectory, "should-not-be-opened.png"),
                new string('0', 64),
                "image/png"))
            .ToArray();
        Throws<ArgumentException>(
            () => RunWithoutDispatcherContext(() => overLimitService.CaptureAsync(
                Guid.NewGuid().ToString("N"),
                overLimitSources)),
            "超过 128 张风格参考图时应在复制前拒绝");
        False(
            Directory.EnumerateFileSystemEntries(overLimitPaths.GenerationReferenceSnapshotDirectory).Any(),
            "超过参考图数量上限时不得创建快照文件或临时目录");

        var paths = new WorkspacePaths(Path.Combine(
            _temporaryRoot,
            "generation-reference-snapshot-stream"));
        var service = new GenerationReferenceSnapshotService(paths);
        var sourceDirectory = Path.Combine(paths.RootDirectory, "Sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "stream-reference.png");
        var sourceBytes = new byte[(384 * 1024) + 37];
        for (var index = 0; index < sourceBytes.Length; index++)
        {
            sourceBytes[index] = (byte)((index * 31 + 17) % 251);
        }

        File.WriteAllBytes(sourcePath, sourceBytes);
        var expectedHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var source = new GenerationReferenceSource(
            Guid.NewGuid().ToString("N"),
            "stream-reference",
            sourcePath,
            expectedHash,
            "image/png");
        var snapshot = RunWithoutDispatcherContext(() => service.CaptureAsync(
            Guid.NewGuid().ToString("N"),
            [source]));
        Equal(1, snapshot.References.Count, "流式参考快照数量");
        var copiedPath = Path.GetFullPath(Path.Combine(
            paths.RootDirectory,
            snapshot.References.Single().DataRootRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        True(File.Exists(copiedPath), "流式参考快照文件应存在");
        using (var copiedStream = File.OpenRead(copiedPath))
        {
            Equal(
                expectedHash,
                Convert.ToHexString(SHA256.HashData(copiedStream)).ToLowerInvariant(),
                "流式复制后的快照 SHA-256");
        }

        Equal(expectedHash, snapshot.References.Single().Sha256,
            "流式快照元数据 SHA-256");
        service.Delete(snapshot);
        False(Directory.Exists(snapshot.SnapshotDirectory),
            "显式删除流式快照后目录应移除");

        var mismatchBatchId = Guid.NewGuid().ToString("N");
        var mismatchedSource = source with { Sha256 = new string('f', 64) };
        Throws<InvalidDataException>(
            () => RunWithoutDispatcherContext(() => service.CaptureAsync(
                mismatchBatchId,
                [mismatchedSource])),
            "参考图哈希变化时应拒绝创建快照");
        False(
            Directory.EnumerateFileSystemEntries(paths.GenerationReferenceSnapshotDirectory).Any(),
            "流式复制失败后不得遗留最终或临时快照目录");
    }

    private static void AssetGenerationRunsImmutableMcpDeliveryAndRejectsInvalidRasterOutputs()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "asset-generation-mcp-delivery"));
        var port = GetAvailableLoopbackPort();
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            ActiveWorkflowKey = "tilemap",
            HasCompletedWorkflowSelection = true,
            McpAgentKey = "codex",
            McpPort = port,
            TilemapGridUnitSize = 72d,
            TilemapArtImageWidth = 512,
            TilemapArtImageHeight = 256,
            TilemapDiamondAngle = 48d,
            TilemapRotationAngle = 27.5d
        });

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);
        using var httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        var assetGenerationServerService = typeof(AgentConnectionViewModel)
            .GetField("_serverService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(shell.AgentConnection) as McpServerProcessService
            ?? throw new SmokeTestException("素材生成闭环无法读取 MCP 进程服务的临时认证信息。");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", assetGenerationServerService.TokenEnvironmentVariableValue);

        try
        {
            var sourceDirectory = Path.Combine(paths.RootDirectory, "GenerationTestSources");
            Directory.CreateDirectory(sourceDirectory);
            var sourceReferencePath = Path.Combine(sourceDirectory, "hand-painted-stone.png");
            WritePngFixture(sourceReferencePath, 32, 16, includeAlpha: true);

            var styleLibrary = shell.ArtStyleLibrary;
            var styleGroup = styleLibrary.SelectedGroup
                ?? throw new SmokeTestException("素材生成闭环缺少默认风格组。");
            styleGroup.Name = "暖色手绘遗迹";
            styleGroup.Description = "低饱和砂岩、暖色边缘光、清晰的大块面与手绘笔触。";
            styleGroup.StylePrompt = "hand-painted game art, warm rim light, readable sandstone shapes";
            styleLibrary.ImportAssets([sourceReferencePath]);
            Equal(1, styleGroup.Assets.Count, "素材生成风格组参考图数量");
            var managedReferencePath = styleGroup.Assets.Single().FullPath;
            var importedReferenceSha256 = styleGroup.Assets.Single().Sha256;
            True(File.Exists(managedReferencePath), "风格参考图导入后应存在于受管素材库");

            var outputDirectory = Path.Combine(paths.GeneratedAssetDirectory, "TilemapWorkflow");
            Directory.CreateDirectory(outputDirectory);
            True(shell.ProjectBrowser.SelectDirectory(outputDirectory),
                "素材生成闭环应能选择 Assets 内的指定输出目录");

            var generation = shell.AssetGeneration;
            generation.SelectedStyleGroup = styleGroup;
            generation.ContentPrompt = "单个长满苔藓的古代砂岩地表瓦片";
            generation.NegativePrompt = "文字、水印、棋盘格背景、画布外溢";
            generation.UseProjectCurrentDirectoryCommand.Execute(null);

            Equal("512 × 256 px · PNG · 透明背景", generation.CanvasSizeText,
                "素材生成 Tilemap 画布摘要");
            True(generation.GenerationPerspectiveText.Contains("A/B 边夹角 48°", StringComparison.Ordinal) &&
                 generation.GenerationPerspectiveText.Contains("A 边方向 27.5°", StringComparison.Ordinal),
                "素材生成应公开 Tilemap 边夹角与绝对方向参数");
            True(generation.ConstraintPreview.Contains(styleGroup.Description, StringComparison.Ordinal) &&
                 generation.ConstraintPreview.Contains("512 × 256", StringComparison.Ordinal) &&
                 generation.ConstraintPreview.Contains(outputDirectory, StringComparison.OrdinalIgnoreCase),
                "确认描述应合并风格、画幅与指定输出位置");

            var secondStore = new JsonGenerationTaskStore(paths.GenerationDirectory);
            False(generation.IsAgentConnected, "握手前素材生成不应显示 Agent 已连接");
            True(generation.CanCreateTask, "握手前有效配置应允许离线发布到本地队列");
            True(generation.CreateTaskCommand.CanExecute(null), "握手前批量发布命令仍应可用");
            Equal(0, secondStore.ListAsync().GetAwaiter().GetResult().Count,
                "尚未执行发布命令时队列应保持为空");

            Task.Run(() => assetGenerationServerService.StartAsync(port, "codex"))
                .GetAwaiter()
                .GetResult();
            WaitUntilWithDispatcher(
                () => assetGenerationServerService.State is
                    McpServerProcessState.WaitingForAgent or McpServerProcessState.Connected,
                TimeSpan.FromSeconds(15),
                "素材生成闭环的 MCP Server 未进入等待握手状态。");

            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp", UriKind.Absolute);
            var healthEndpoint = new Uri($"http://127.0.0.1:{port}/health", UriKind.Absolute);
            using (RunWithoutDispatcherContext(() =>
                       WaitForHealthStatusAsync(httpClient, healthEndpoint, "waiting", TimeSpan.FromSeconds(12))))
            {
                // The health payload only needs to remain alive until the server is known to be ready.
            }

            using var initialize = RunWithoutDispatcherContext(() => CallHttpMcpAsync(httpClient, endpoint, new
            {
                jsonrpc = "2.0",
                id = 501,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "Codex Asset Generation SmokeTests", version = "1.0.0" }
                }
            }));
            Equal(HttpStatusCode.OK, initialize.StatusCode, "素材生成 MCP initialize 状态码");
            var initializePayload = initialize.Payload
                ?? throw new SmokeTestException("素材生成 MCP initialize 未返回 JSON-RPC 响应体。");
            False(initializePayload.RootElement.TryGetProperty("error", out _),
                "素材生成 MCP initialize 不应返回 Agent 配置错误");
            var sessionId = initialize.SessionId
                ?? throw new SmokeTestException("素材生成 MCP initialize 未返回会话 ID。");

            using var initialized = RunWithoutDispatcherContext(() => CallHttpMcpAsync(
                httpClient,
                endpoint,
                new
                {
                    jsonrpc = "2.0",
                    method = "notifications/initialized",
                    @params = new { }
                },
                sessionId));
            Equal(HttpStatusCode.Accepted, initialized.StatusCode,
                "素材生成 MCP initialized 通知状态码");

            using (RunWithoutDispatcherContext(() =>
                       WaitForHealthStatusAsync(httpClient, healthEndpoint, "connected", TimeSpan.FromSeconds(8))))
            {
                // The desktop connection state is asserted after its dispatcher receives the health update.
            }

            WaitUntilWithDispatcher(
                () => assetGenerationServerService.State == McpServerProcessState.Connected,
                TimeSpan.FromSeconds(8),
                "真实 HTTP MCP 握手后桌面服务未进入 Connected 状态。");
            False(shell.AgentConnection.IsConnected || generation.IsAgentConnected,
                "仅 MCP 握手不得伪装成可执行的 Agent Runtime 会话");
            True(generation.CanCreateTask, "完成握手且配置有效后应允许提交素材生成任务");

            generation.CreateTaskCommand.Execute(null);
            // The task store becomes readable before the run manifest and page state are applied.
            WaitUntilWithDispatcher(
                () => !generation.IsBusy &&
                      secondStore.ListAsync().GetAwaiter().GetResult().Count > 0,
                TimeSpan.FromSeconds(8),
                "素材生成页未完成共享 Pending 任务持久化与页面状态同步。");

            var task = secondStore.ListAsync().GetAwaiter().GetResult().Single();
            var batchId = task.BatchId
                ?? throw new SmokeTestException("素材生成页创建的任务缺少批次 ID。");
            Equal(batchId, generation.CurrentBatchId, "素材生成页当前批次 ID");
            var taskId = task.Id;
            Equal(GenerationTaskStatus.Pending, task.Status, "素材生成页新任务状态");
            Equal(TilemapConstraintPackageBuilder.WorkflowId, task.Constraints.WorkflowId,
                "素材生成任务工作流");
            Equal(512, task.Constraints.Output.Width, "素材生成任务输出宽度");
            Equal(256, task.Constraints.Output.Height, "素材生成任务输出高度");
            True(task.Constraints.Output.TransparentBackground,
                "素材生成 Tilemap 任务必须要求透明背景");

            var tilemapNode = task.Constraints.Parameters["tilemap"] as JsonObject
                ?? throw new SmokeTestException("素材生成任务缺少 parameters.tilemap。");
            Near(72d, tilemapNode["gridUnitSize"]!.GetValue<double>(), 0.000001,
                "素材生成任务网格单位");
            Near(48d, tilemapNode["diamondAngle"]!.GetValue<double>(), 0.000001,
                "素材生成任务控制角");
            Near(27.5d, tilemapNode["rotationAngle"]!.GetValue<double>(), 0.000001,
                "素材生成任务旋转角");

            var styleNode = task.Constraints.Parameters["style"] as JsonObject
                ?? throw new SmokeTestException("素材生成任务缺少 parameters.style。");
            Equal(styleGroup.Id, styleNode["groupId"]!.GetValue<string>(), "任务风格组 ID");
            Equal(styleGroup.Name, styleNode["name"]!.GetValue<string>(), "任务风格组名称");
            Equal(styleGroup.Description, styleNode["description"]!.GetValue<string>(),
                "任务风格描述");
            Equal(styleGroup.StylePrompt, styleNode["prompt"]!.GetValue<string>(),
                "任务风格提示词");
            var styleReferences = styleNode["references"] as JsonArray
                ?? throw new SmokeTestException("素材生成任务缺少风格参考快照列表。");
            Equal(1, styleReferences.Count, "任务风格参考快照数量");
            var styleReference = styleReferences[0] as JsonObject
                ?? throw new SmokeTestException("任务风格参考快照格式无效。");
            var snapshotRelativePath = styleReference["dataRootRelativePath"]!.GetValue<string>();
            var snapshotFullPath = Path.GetFullPath(Path.Combine(
                paths.RootDirectory,
                snapshotRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            True(AssetRootService.IsWithinRoot(paths.GenerationReferenceSnapshotDirectory, snapshotFullPath),
                "风格参考快照必须位于 Generation/ReferenceSnapshots");
            True(File.Exists(snapshotFullPath), "风格参考不可变快照文件应存在");
            Equal(importedReferenceSha256, styleReference["sha256"]!.GetValue<string>(),
                "任务风格参考快照记录 SHA-256");
            Equal(
                importedReferenceSha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(snapshotFullPath))).ToLowerInvariant(),
                "任务风格参考快照实际 SHA-256");

            True(MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
                 delivery is not null,
                "素材生成任务必须包含可读取的固定 delivery");
            var deliveryPlan = delivery!;
            True(AssetRootService.PathEquals(paths.AssetDirectory, deliveryPlan.AssetRoot),
                "delivery 必须保存任务创建时的 Assets 根目录");
            True(
                deliveryPlan.OutputDirectory.StartsWith(
                    "Generated/TilemapWorkflow/Tilemap/",
                    StringComparison.Ordinal) &&
                deliveryPlan.OutputDirectory.EndsWith("/Final", StringComparison.Ordinal),
                "delivery 输出目录应冻结到 Assets 内的批次 Final 分类目录");
            Equal(1, deliveryPlan.TargetPaths.Length, "delivery 预分配目标数量");
            var targetRelativePath = deliveryPlan.TargetPaths.Single();
            True(targetRelativePath.StartsWith($"{deliveryPlan.OutputDirectory}/tilemap_", StringComparison.Ordinal) &&
                  targetRelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase),
                "delivery 应预分配唯一 Tilemap PNG 目标路径");
            var targetFullPath = Path.GetFullPath(Path.Combine(
                deliveryPlan.AssetRoot,
                targetRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            False(File.Exists(targetFullPath), "Agent 完成前预分配目标不应伪装成已有产物");

            var nextRpcId = 502;
            var claimedPrimary = CallHttpMcpToolSuccess(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                "claim_generation_task",
                new { taskId, agentId = "asset-generation-smoke-agent" });
            Equal(taskId, claimedPrimary.GetProperty("task").GetProperty("id").GetString(),
                "MCP 应认领素材生成页创建的精确任务");
            Equal(paths.RootDirectory, claimedPrimary.GetProperty("dataRoot").GetString(),
                "MCP claim 应返回风格快照可解析的数据根目录");
            True(AssetRootService.PathEquals(
                    deliveryPlan.AssetRoot,
                    claimedPrimary.GetProperty("assetRoot").GetString()!),
                "MCP claim 应返回任务快照的 AssetRoot");
            var expectedRecommendedSourcePath = Path.GetFullPath(Path.Combine(
                deliveryPlan.AssetRoot,
                deliveryPlan.Workspace!.ModelOutputPaths.Single()
                    .Replace('/', Path.DirectorySeparatorChar)));
            Equal(
                expectedRecommendedSourcePath,
                claimedPrimary.GetProperty("recommendedSourcePath").GetString(),
                "MCP claim 应返回唯一项目内模型原图绝对推荐路径");
            var imageGeneration = claimedPrimary.GetProperty("imageGeneration");
            var imageGenerationOutput = imageGeneration.GetProperty("output");
            Equal(512, imageGenerationOutput.GetProperty("targetWidth").GetInt32(),
                "MCP claim CLI 合同必须传递冻结目标宽度");
            Equal(256, imageGenerationOutput.GetProperty("targetHeight").GetInt32(),
                "MCP claim CLI 合同必须传递冻结目标高度");
            Equal("2:1", imageGenerationOutput.GetProperty("targetAspectRatio").GetString(),
                "MCP claim CLI 合同必须传递约分后画幅");
            Equal("1152x576", imageGenerationOutput.GetProperty("cliSize").GetString(),
                "MCP claim 应生成符合 GPT Image 约束且保持 2:1 的非方形 CLI 尺寸");
            True(imageGenerationOutput.GetProperty("preservesExactAspectRatio").GetBoolean(),
                "2:1 画幅应能在 CLI 尺寸中精确保留");
            var imageGenerationStyle = imageGeneration.GetProperty("style");
            Equal(styleGroup.StylePrompt, imageGenerationStyle.GetProperty("prompt").GetString(),
                "MCP claim CLI 合同必须传递冻结风格提示词");
            var referenceSourcePaths = imageGenerationStyle.GetProperty("referenceSourcePaths");
            Equal(1, referenceSourcePaths.GetArrayLength(),
                "MCP claim CLI 合同必须传递全部冻结风格参考图");
            Equal(snapshotFullPath, referenceSourcePaths[0].GetString(),
                "MCP claim 应把风格快照解析为可直接传给 --image 的绝对路径");

            var invalidTaskIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var caseName in new[]
                     {
                         "wrong-size",
                         "no-alpha",
                         "fake-png",
                         "empty-idat",
                         "opaque-outside",
                         "missing-tilemap",
                         "bad-angle",
                         "all-transparent",
                         "oversized-decode",
                         "wrong-target"
                     })
            {
                var validationTarget = $"{deliveryPlan.OutputDirectory}/{caseName}-{Guid.NewGuid():N}.png";
                var validationConstraints = CloneConstraintsWithDeliveryTarget(task.Constraints, validationTarget);
                if (caseName == "missing-tilemap")
                {
                    validationConstraints.Parameters.Remove("tilemap");
                }
                else if (caseName == "bad-angle")
                {
                    ((JsonObject)validationConstraints.Parameters["tilemap"]!)["diamondAngle"] = 5d;
                }
                else if (caseName == "oversized-decode")
                {
                    ((JsonObject)validationConstraints.Parameters["delivery"]!).Remove("stagingPaths");
                    validationConstraints = validationConstraints with
                    {
                        WorkflowId = "generic-raster-smoke",
                        Output = validationConstraints.Output with
                        {
                            Width = 16_384,
                            Height = 16_384,
                            // Keep this as a generic PNG decoder-budget regression. Background-removal
                            // tasks now reject this canvas during task creation using the shared RGBA cap.
                            BackgroundRemoval = null
                        }
                    };
                }

                invalidTaskIds[caseName] = CreateAndClaimHttpGenerationTask(
                    httpClient,
                    endpoint,
                    sessionId,
                    ref nextRpcId,
                    $"Validate {caseName}",
                    validationConstraints,
                    "asset-generation-smoke-agent");
            }

            var unsupportedDelivery = CloneConstraintsWithDeliveryTarget(
                task.Constraints,
                $"{deliveryPlan.OutputDirectory}/bad-version-{Guid.NewGuid():N}.png");
            ((JsonObject)unsupportedDelivery.Parameters["delivery"]!)["schemaVersion"] = 99;
            var unsupportedDeliveryError = CallHttpMcpToolError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                "create_generation_task",
                new
                {
                    displayName = "Unsupported delivery schema",
                    requestedBy = "smoke-tests",
                    constraints = unsupportedDelivery
                });
            True(unsupportedDeliveryError.Contains("损坏或不受支持", StringComparison.Ordinal),
                "MCP 创建任务必须拒绝不受支持的 delivery schemaVersion");

            var missingDeliverySchema = CloneConstraintsWithDeliveryTarget(
                task.Constraints,
                $"{deliveryPlan.OutputDirectory}/missing-version-{Guid.NewGuid():N}.png");
            ((JsonObject)missingDeliverySchema.Parameters["delivery"]!).Remove("schemaVersion");
            var malformedStoredTask = secondStore.CreateAsync(new CreateGenerationTaskRequest
            {
                DisplayName = "Persisted malformed delivery",
                RequestedBy = "smoke-tests",
                Constraints = missingDeliverySchema
            }).GetAwaiter().GetResult();
            var malformedClaimError = CallHttpMcpToolError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                "claim_generation_task",
                new { taskId = malformedStoredTask.Id, agentId = "asset-generation-smoke-agent" });
            True(malformedClaimError.Contains("损坏或不受支持", StringComparison.Ordinal),
                "MCP 认领历史任务时必须拒绝缺失 schemaVersion 的 delivery");
            secondStore.ClaimAsync(malformedStoredTask.Id, "asset-generation-smoke-agent")
                .GetAwaiter().GetResult();
            var malformedCompleteError = CallHttpMcpToolError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                "complete_generation_task",
                new
                {
                    taskId = malformedStoredTask.Id,
                    agentId = "asset-generation-smoke-agent",
                    assetPaths = new[] { targetRelativePath }
                });
            True(malformedCompleteError.Contains("损坏或不受支持", StringComparison.Ordinal),
                "MCP 完成历史任务时必须拒绝缺失 schemaVersion 的 delivery");

            styleLibrary.DeleteGroupCommand.Execute(null);
            Equal(0, styleLibrary.Groups.Count, "删除原风格组后素材库应为空");
            False(File.Exists(managedReferencePath), "删除原风格组应移除受管参考原件");
            True(File.Exists(sourceReferencePath), "删除风格组不得删除用户导入源文件");
            True(File.Exists(snapshotFullPath), "删除原风格组后任务不可变参考快照必须保留");
            Equal(importedReferenceSha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(snapshotFullPath))).ToLowerInvariant(),
                "删除原风格组后参考快照哈希必须保持不变");

            var replacementAssetRoot = Path.Combine(paths.RootDirectory, "ReplacementAssets");
            Directory.CreateDirectory(replacementAssetRoot);
            True(shell.ProjectBrowser.TrySetRootPath(replacementAssetRoot),
                "应能在任务创建后切换到另一个 Assets 根目录");
            True(AssetRootService.PathEquals(replacementAssetRoot, shell.ProjectBrowser.RootPath),
                "项目区应显示新的 Assets 根目录");
            var persistedReplacementRoot = settingsService.Load().AssetRootPath;
            True(persistedReplacementRoot is not null &&
                 AssetRootService.PathEquals(replacementAssetRoot, persistedReplacementRoot),
                "新的 Assets 根目录应在完成任务前写入设置");

            WritePngFixture(
                targetFullPath,
                512,
                256,
                includeAlpha: true,
                diamondAngle: 48d,
                rotationAngle: 27.5d);
            var completedPrimary = CallHttpMcpToolSuccess(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                "complete_generation_task",
                new
                {
                    taskId,
                    agentId = "asset-generation-smoke-agent",
                    assetPaths = new[] { targetRelativePath }
                });
            Equal("completed", completedPrimary.GetProperty("task").GetProperty("status").GetString(),
                "MCP 完成素材生成任务状态");
            True(AssetRootService.PathEquals(
                    deliveryPlan.AssetRoot,
                    completedPrimary.GetProperty("effectiveAssetRoot").GetString()!),
                "切换设置后 completion 仍必须使用创建任务时 delivery.AssetRoot");
            Equal(targetRelativePath,
                completedPrimary.GetProperty("relativeAssetPaths")[0].GetString(),
                "MCP 完成任务登记的精确相对路径");

            generation.RefreshTaskCommand.Execute(null);
            try
            {
                WaitUntilWithDispatcher(
                    () => generation.CurrentTaskStatusText == "批次全部完成" &&
                          generation.GeneratedAssets.Count == 1,
                    TimeSpan.FromSeconds(8),
                    "MCP 完成后素材生成页未回填产物状态。");
            }
            catch (SmokeTestException)
            {
                throw new SmokeTestException(
                    $"MCP 完成后素材生成页未回填产物状态：status={generation.CurrentTaskStatusText}, " +
                    $"detail={generation.CurrentTaskDetail}, published={generation.PublishedTasks.Count}, " +
                    $"generated={generation.GeneratedAssets.Count}。");
            }
            var generatedAsset = generation.GeneratedAssets.Single();
            True(AssetRootService.PathEquals(targetFullPath, generatedAsset.FullPath),
                "素材生成页应按任务 delivery.AssetRoot 展示产物，而非后来切换的新根目录");
            Equal("已生成", generatedAsset.AvailabilityText, "素材生成页产物可用状态");

            var wrongSizeTarget = GetDeliveryTarget(secondStore, invalidTaskIds["wrong-size"]);
            var wrongSizeFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, wrongSizeTarget);
            WritePngFixture(wrongSizeFullPath, 511, 256, includeAlpha: true);
            var wrongSizeError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["wrong-size"],
                wrongSizeTarget);
            True(wrongSizeError.Contains("尺寸不匹配", StringComparison.Ordinal),
                "MCP 必须拒绝非 512×256 的 Tilemap PNG");

            var noAlphaTarget = GetDeliveryTarget(secondStore, invalidTaskIds["no-alpha"]);
            var noAlphaFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, noAlphaTarget);
            WritePngFixture(noAlphaFullPath, 512, 256, includeAlpha: false);
            var noAlphaError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["no-alpha"],
                noAlphaTarget);
            True(noAlphaError.Contains("真实 Alpha", StringComparison.Ordinal),
                "MCP 必须拒绝没有 Alpha 通道的 PNG");

            var fakePngTarget = GetDeliveryTarget(secondStore, invalidTaskIds["fake-png"]);
            var fakePngFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, fakePngTarget);
            File.WriteAllBytes(fakePngFullPath, Encoding.UTF8.GetBytes("not-a-real-png-file"));
            var fakePngError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["fake-png"],
                fakePngTarget);
            True(fakePngError.Contains("不是真实 PNG", StringComparison.Ordinal),
                "MCP 必须拒绝仅使用 .png 扩展名的伪图片");

            var emptyIdatTarget = GetDeliveryTarget(secondStore, invalidTaskIds["empty-idat"]);
            var emptyIdatFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, emptyIdatTarget);
            WritePngFixtureWithEmptyDecodedPayload(emptyIdatFullPath, 512, 256);
            var emptyIdatError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["empty-idat"],
                emptyIdatTarget);
            True(
                emptyIdatError.Contains("解压数据不足", StringComparison.Ordinal) ||
                emptyIdatError.Contains("无法完整解码", StringComparison.Ordinal),
                $"MCP 必须真实解压 IDAT，并拒绝 CRC 正确但没有像素数据的 PNG；实际错误：{emptyIdatError}");

            var opaqueOutsideTarget = GetDeliveryTarget(secondStore, invalidTaskIds["opaque-outside"]);
            var opaqueOutsideFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, opaqueOutsideTarget);
            WritePngFixture(opaqueOutsideFullPath, 512, 256, includeAlpha: true);
            var opaqueOutsideError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["opaque-outside"],
                opaqueOutsideTarget);
            True(opaqueOutsideError.Contains("瓦片有效轮廓外", StringComparison.Ordinal),
                "MCP 必须拒绝带 Alpha 通道但瓦片有效轮廓外仍有不透明像素的 Tilemap PNG");

            foreach (var malformedGeometryCase in new[] { "missing-tilemap", "bad-angle" })
            {
                var malformedGeometryTarget = GetDeliveryTarget(secondStore, invalidTaskIds[malformedGeometryCase]);
                var malformedGeometryFullPath = ResolveDeliveryTargetFullPath(
                    paths.AssetDirectory,
                    malformedGeometryTarget);
                WritePngFixture(
                    malformedGeometryFullPath,
                    512,
                    256,
                    includeAlpha: true,
                    diamondAngle: 48d,
                    rotationAngle: 27.5d);
                var malformedGeometryError = CompleteHttpGenerationTaskExpectingError(
                    httpClient,
                    endpoint,
                    sessionId,
                    ref nextRpcId,
                    invalidTaskIds[malformedGeometryCase],
                    malformedGeometryTarget);
                True(malformedGeometryError.Contains("Tilemap", StringComparison.OrdinalIgnoreCase),
                    $"MCP 必须对 {malformedGeometryCase} 的 Tilemap 几何参数 fail-closed；实际错误：{malformedGeometryError}");
            }

            var allTransparentTarget = GetDeliveryTarget(secondStore, invalidTaskIds["all-transparent"]);
            var allTransparentFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, allTransparentTarget);
            WritePngFixture(
                allTransparentFullPath,
                512,
                256,
                includeAlpha: true,
                diamondAngle: 48d,
                rotationAngle: 27.5d,
                forceFullyTransparent: true);
            var allTransparentError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["all-transparent"],
                allTransparentTarget);
            True(allTransparentError.Contains("全透明", StringComparison.Ordinal) ||
                 allTransparentError.Contains("有效内容", StringComparison.Ordinal),
                $"MCP 必须拒绝没有任何有效内容的全透明 Tilemap PNG；实际错误：{allTransparentError}");

            var oversizedDecodeTarget = GetDeliveryTarget(secondStore, invalidTaskIds["oversized-decode"]);
            var oversizedDecodeFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, oversizedDecodeTarget);
            WritePngFixtureWithEmptyDecodedPayload(
                oversizedDecodeFullPath,
                16_384,
                16_384,
                bitDepth: 16);
            var oversizedDecodeError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["oversized-decode"],
                oversizedDecodeTarget);
            True(oversizedDecodeError.Contains("上限", StringComparison.Ordinal) ||
                 oversizedDecodeError.Contains("过大", StringComparison.Ordinal),
                $"MCP 必须在解压前拒绝超出像素预算的 PNG；实际错误：{oversizedDecodeError}");

            var expectedWrongTarget = GetDeliveryTarget(secondStore, invalidTaskIds["wrong-target"]);
            var unallocatedTarget = $"Generated/TilemapWorkflow/unallocated-{Guid.NewGuid():N}.png";
            var unallocatedFullPath = ResolveDeliveryTargetFullPath(paths.AssetDirectory, unallocatedTarget);
            WritePngFixture(
                unallocatedFullPath,
                512,
                256,
                includeAlpha: true,
                diamondAngle: 48d,
                rotationAngle: 27.5d);
            var wrongTargetError = CompleteHttpGenerationTaskExpectingError(
                httpClient,
                endpoint,
                sessionId,
                ref nextRpcId,
                invalidTaskIds["wrong-target"],
                unallocatedTarget);
            True(!string.Equals(expectedWrongTarget, unallocatedTarget, StringComparison.OrdinalIgnoreCase),
                "非预分配路径 fixture 必须与 delivery.targetPaths 不同");
            True(wrongTargetError.Contains("精确一致", StringComparison.Ordinal),
                "MCP 必须拒绝用其它现有 PNG 替代预分配目标路径");

            foreach (var invalidTaskId in invalidTaskIds.Values)
            {
                Equal(GenerationTaskStatus.Claimed,
                    secondStore.GetAsync(invalidTaskId).GetAwaiter().GetResult()?.Status,
                    "产物校验失败后任务不得被错误标记为 Completed");
            }
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void EditorCreatesLayersTextAndPrefabsWithUndoRedo()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "editor-operations"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());

        try
        {
            var initialLayerCount = editor.ActiveDocument.LayersById.Count;

            editor.NewLayerCommand.Execute(null);
            Equal(initialLayerCount + 1, editor.ActiveDocument.LayersById.Count, "新建普通图层");
            Equal("frame", editor.SelectedLayer?.ComponentType, "普通图层组件类型");

            editor.NewFolderCommand.Execute(null);
            Equal(initialLayerCount + 2, editor.ActiveDocument.LayersById.Count, "新建图层文件夹");
            True(editor.SelectedLayer?.IsGroup == true, "新建文件夹后应选中文件夹");

            editor.CreateTextAt(123, 234);
            Equal(initialLayerCount + 3, editor.ActiveDocument.LayersById.Count, "创建文字图层");
            True(editor.SelectedLayer?.IsText == true, "创建文字后应选中文字图层");
            Equal(123d, editor.SelectedLayer?.X ?? double.NaN, "文字图层 X");
            Equal(234d, editor.SelectedLayer?.Y ?? double.NaN, "文字图层 Y");

            var prefabIds = editor.Prefabs.Select(prefab => prefab.Id).ToArray();
            Equal(5, prefabIds.Length, "基础 Prefab 数量");
            SequenceEqual(
                new[]
                {
                    "prefab-basic-button",
                    "prefab-text-input",
                    "prefab-background-panel",
                    "prefab-slider",
                    "prefab-toggle"
                },
                prefabIds,
                "基础 Prefab 集合");

            foreach (var prefabId in prefabIds)
            {
                var beforePrefabCount = editor.ActiveDocument.LayersById.Count;
                var payloadLayerCount = editor.Project.Library.AssetsById[prefabId].Payload.LayersById.Count;
                editor.InstantiatePrefabAt(prefabId, 320, 420);
                Equal(beforePrefabCount + payloadLayerCount, editor.ActiveDocument.LayersById.Count,
                    $"实例化 {prefabId}");
                Equal(prefabId, editor.SelectedLayer?.Model.PrefabSourceId,
                    $"{prefabId} 实例应保留来源 ID");
            }

            var countAfterPrefabs = editor.ActiveDocument.LayersById.Count;
            var lastPrefabId = prefabIds[^1];
            var lastPrefabLayerCount = editor.Project.Library.AssetsById[lastPrefabId].Payload.LayersById.Count;
            True(editor.UndoCommand.CanExecute(null), "创建内容后应可撤销");
            editor.UndoCommand.Execute(null);
            Equal(countAfterPrefabs - lastPrefabLayerCount, editor.ActiveDocument.LayersById.Count,
                "撤销最后一次 Prefab 实例化");
            True(editor.RedoCommand.CanExecute(null), "撤销后应可重做");
            editor.RedoCommand.Execute(null);
            Equal(countAfterPrefabs, editor.ActiveDocument.LayersById.Count,
                "重做最后一次 Prefab 实例化");
            True(editor.ActiveDocument.LayersById.Values.Any(layer =>
                    string.Equals(layer.PrefabSourceId, lastPrefabId, StringComparison.Ordinal)),
                "重做后应恢复 Prefab 来源信息");
        }
        finally
        {
            editor.Shutdown();
        }
    }

    private static void LayerReorderingAndGroupingPreserveTreeRules()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "layer-reordering"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());

        try
        {
            True(editor.CanMoveLayer("layer-primary", "group-content", intoTarget: true),
                "可移动图层应允许归入未锁定文件夹");
            editor.MoveLayer("layer-primary", "group-content", intoTarget: true);
            Equal("group-content", editor.ActiveDocument.LayersById["layer-primary"].ParentId,
                "归组后的 ParentId");
            Equal("layer-primary", editor.ActiveDocument.LayersById["group-content"].ChildIds[0],
                "归组后的图层应位于文件夹前排");
            False(editor.ActiveDocument.LayersById["group-actions"].ChildIds.Contains("layer-primary"),
                "归组后旧父组不应保留图层 ID");

            editor.MoveLayer("layer-status", "layer-secondary", intoTarget: false);
            var actionChildren = editor.ActiveDocument.LayersById["group-actions"].ChildIds;
            SequenceEqual(new[] { "layer-status", "layer-secondary" }, actionChildren,
                "同级移动应把源图层放到目标前方");
            Equal("group-actions", editor.ActiveDocument.LayersById["layer-status"].ParentId,
                "同级移动应保留目标父组");

            True(editor.CanMoveLayer("group-actions", "group-content", intoTarget: true),
                "独立文件夹应可归入另一个文件夹");
            editor.MoveLayer("group-actions", "group-content", intoTarget: true);
            False(editor.CanMoveLayer("group-content", "group-actions", intoTarget: true),
                "父文件夹不得移动进自己的后代");
            editor.MoveLayer("group-content", "group-actions", intoTarget: true);
            Equal<string?>(null, editor.ActiveDocument.LayersById["group-content"].ParentId,
                "拒绝循环后父文件夹位置不应改变");

            False(editor.CanMoveLayer("layer-page", "group-content", intoTarget: true),
                "页面根图层不得移动");
            editor.MoveLayer("layer-page", "group-content", intoTarget: true);
            Equal<string?>(null, editor.ActiveDocument.LayersById["layer-page"].ParentId,
                "拒绝移动后页面仍应位于根层级");
            True(editor.ActiveDocument.RootIds.Contains("layer-page"),
                "拒绝移动后 RootIds 仍应包含页面");
        }
        finally
        {
            editor.Shutdown();
        }
    }

    private static void ParentLockAndOpacityFlowToDescendants()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "layer-inheritance"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());

        try
        {
            var group = FindLayer(editor.LayerRoots, "group-actions");
            var child = FindLayer(editor.LayerRoots, "layer-primary");

            group.OpacityPercent = 50;
            child.OpacityPercent = 40;
            Near(0.2, child.EffectiveOpacity, 0.0001,
                "子图层有效透明度应为父子透明度乘积");

            group.IsLocked = true;
            True(child.EffectiveLocked, "父组锁定后子图层应处于有效锁定状态");
            False(editor.BeginMove(child.Id), "父组锁定时不得开始移动子图层");
            False(editor.CanMoveLayer("layer-secondary", "layer-card", intoTarget: false),
                "不得从外部把图层插入锁定文件夹的子层前方");
            editor.SelectLayer(child.Id);
            var lockedLayerCount = editor.ActiveDocument.LayersById.Count;
            False(editor.DeleteSelectedCommand.CanExecute(null), "父组锁定时不得删除子图层");
            editor.DeleteSelectedCommand.Execute(null);
            Equal(lockedLayerCount, editor.ActiveDocument.LayersById.Count,
                "锁定子图层的删除命令不得改变项目");

            group.IsLocked = false;
            False(child.EffectiveLocked, "解锁父组后未单独锁定的子图层应恢复可编辑");
            True(editor.BeginMove(child.Id), "解锁父组后应允许开始移动子图层");
            editor.EndMove();
        }
        finally
        {
            editor.Shutdown();
        }
    }

    private static void ProjectSaveReloadRoundTripsThroughJsonRepository()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "project-roundtrip"));
        var repository = new JsonProjectRepository(paths);
        var editor = new EditorWorkspaceViewModel(
            repository,
            new DesktopFileDialogService(),
            new ProjectHistoryService());

        try
        {
            editor.NewLayerCommand.Execute(null);
            var savedLayerId = editor.SelectedLayer?.Id
                ?? throw new SmokeTestException("保存测试未选中新建图层。");
            var savedLayerName = editor.SelectedLayer.Name;
            var savedLayerCount = editor.ActiveDocument.LayersById.Count;

            editor.SaveImmediately();
            True(File.Exists(paths.AutosaveProjectPath), "保存后应创建 default.pfui.json");
            False(editor.IsDirty, "立即保存后项目不应处于 dirty 状态");

            var reloadedRepository = new JsonProjectRepository(paths);
            var reloaded = reloadedRepository.LoadOrCreate();
            var reloadedDocument = reloaded.DocumentsById[reloaded.ActiveDocumentId];
            Equal(savedLayerCount, reloadedDocument.LayersById.Count, "重载后的图层数量");
            True(reloadedDocument.LayersById.TryGetValue(savedLayerId, out var savedLayer),
                "重载后应存在新建图层");
            Equal(savedLayerName, savedLayer?.Name, "重载后的图层名称");
            Equal(savedLayerId, reloaded.EditorSession.SelectedLayerId, "重载后的选中图层");
            Equal<string?>(null, reloadedRepository.LastRecoverySource, "有效主项目不应触发恢复路径");
        }
        finally
        {
            editor.Shutdown();
        }
    }

    private static void SessionOnlyChangesPersistOnShutdown()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "session-roundtrip"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());

        editor.Zoom = 1.2;
        editor.SelectToolCommand.Execute("text");
        editor.SelectLayer("layer-primary");
        False(editor.IsDirty, "仅会话状态变化不应标记项目业务内容 dirty");
        editor.Shutdown();

        True(File.Exists(paths.AutosaveProjectPath), "退出时应保存待处理的轻量会话状态");
        var reloaded = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        try
        {
            Equal(1.2, reloaded.Zoom, "重载后的 Zoom");
            Equal(EditorTool.Text, reloaded.ActiveTool, "重载后的活动工具");
            Equal("layer-primary", reloaded.SelectedLayer?.Id, "重载后的选中图层");
        }
        finally
        {
            reloaded.Shutdown();
        }
    }

    private static void InvalidThemeSettingFallsBackToDark()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "invalid-theme"));
        File.WriteAllText(
            paths.UserSettingsPath,
            """
            {
              "schemaVersion": 1,
              "theme": 42,
              "activeWorkflowKey": "ui",
              "hasCompletedWorkflowSelection": true
            }
            """);

        var settingsService = new UserSettingsService(paths);
        Equal(AppTheme.Dark, settingsService.Load().Theme, "非法主题枚举的设置加载结果");

        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settingsService, editor);
        try
        {
            Equal(AppTheme.Dark, shell.CurrentTheme, "Shell 应用非法设置后的主题");
            True(shell.DockTheme is Vs2013DarkTheme, "非法主题设置应使用暗色 Dock 主题");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void AvalonDockLayoutSaveRestoreRoundTripsByContentId()
    {
        var paths = new WorkspacePaths(Path.Combine(_temporaryRoot, "layout-roundtrip"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), new UserSettingsService(paths), editor);
        var layoutService = new AvalonDockLayoutService(paths);
        var window = new MainWindow(shell, layoutService);

        try
        {
            var sourceManager = window.FindName("DockManager") as DockingManager
                ?? throw new SmokeTestException("布局往返测试未找到源 DockManager。");
            AssertDefaultTopology(sourceManager);
            layoutService.Save(sourceManager);
            True(File.Exists(paths.LayoutPath), "保存布局后应创建 ui-layout-v1.json");

            var restoredContent = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [MainWindow.ToolsContentId] = NewPanelHost(),
                [MainWindow.CanvasContentId] = NewPanelHost(),
                [MainWindow.TilemapWorkspaceContentId] = NewPanelHost(),
                [MainWindow.CardDesignWorkspaceContentId] = NewPanelHost(),
                [MainWindow.ParallelPerspectiveWorkspaceContentId] = NewPanelHost(),
                [MainWindow.PropertiesContentId] = NewPanelHost(),
                [MainWindow.LayersContentId] = NewPanelHost(),
                [MainWindow.ProjectContentId] = NewPanelHost(),
                [MainWindow.StyleLibraryContentId] = NewPanelHost(),
                [MainWindow.AssetGenerationContentId] = NewPanelHost(),
                [MainWindow.BackgroundRemovalContentId] = NewPanelHost(),
                [MainWindow.ImageOutlineContentId] = NewPanelHost(),
                [MainWindow.IconDesignContentId] = NewPanelHost(),
                [MainWindow.PixelAlignmentContentId] = NewPanelHost(),
                [MainWindow.BlueprintContentId] = NewPanelHost(),
                [MainWindow.AgentConnectionContentId] = NewPanelHost(),
                [MainWindow.CommandConsoleContentId] = NewPanelHost()
            };
            var restoredManager = new DockingManager();
            True(layoutService.TryRestore(restoredManager, restoredContent),
                "有效布局应能从主文件恢复");
            Equal<string?>(null, layoutService.LastRecoverySource, "有效主布局不应触发恢复路径");
            AssertDefaultTopology(restoredManager);

            foreach (var content in restoredManager.Layout.Descendents().OfType<LayoutContent>())
            {
                var contentId = content.ContentId
                    ?? throw new SmokeTestException("恢复后的布局内容缺少 ContentId。");
                True(ReferenceEquals(restoredContent[contentId], content.Content),
                    $"恢复后的 {contentId} 应绑定调用方提供的面板实例");
            }

            var cardDocument = FindDockContent(restoredManager, MainWindow.CardDesignWorkspaceContentId)
                as LayoutDocument
                ?? throw new SmokeTestException("可选业务文档布局测试缺少卡牌文档。");
            cardDocument.Close();
            var optionalPaths = new WorkspacePaths(Path.Combine(_temporaryRoot, "layout-optional-documents"));
            var optionalLayoutService = new AvalonDockLayoutService(optionalPaths);
            optionalLayoutService.Save(restoredManager);
            var optionalContent = restoredContent.Keys.ToDictionary(
                contentId => contentId,
                _ => (object)NewPanelHost(),
                StringComparer.Ordinal);
            var optionalManager = new DockingManager();
            True(optionalLayoutService.TryRestore(optionalManager, optionalContent),
                "关闭单个业务文档后保存的布局仍应有效");
            True(FindDockContent(optionalManager, MainWindow.CardDesignWorkspaceContentId) is null,
                "关闭的卡牌业务文档不应在布局恢复时被强制重新打开");
            True(FindDockContent(optionalManager, MainWindow.CanvasContentId) is LayoutDocument &&
                 FindDockContent(optionalManager, MainWindow.TilemapWorkspaceContentId) is LayoutDocument &&
                 FindDockContent(optionalManager, MainWindow.ParallelPerspectiveWorkspaceContentId) is LayoutDocument,
                "关闭一个业务文档不得影响其它业务文档的布局恢复");

            var revisionTwelveLayout = File.ReadAllText(paths.LayoutPath);
            var revisionElevenLayout = revisionTwelveLayout.Replace(
                "\"presetRevision\": 12",
                "\"presetRevision\": 11",
                StringComparison.Ordinal);
            False(string.Equals(revisionTwelveLayout, revisionElevenLayout, StringComparison.Ordinal),
                "布局迁移测试必须真实降级 presetRevision");
            File.WriteAllText(paths.LayoutPath, revisionElevenLayout);
            var revisionElevenManager = new DockingManager();
            False(layoutService.TryRestore(revisionElevenManager, restoredContent),
                "缺少 Icon 图标制作的 rev11 布局应回退到 rev12 默认布局");
            Equal("default", layoutService.LastRecoverySource,
                "旧布局版本应明确标记为默认布局回退");
            File.WriteAllText(paths.LayoutPath, revisionTwelveLayout);
            layoutService = new AvalonDockLayoutService(paths);

            layoutService.Save(sourceManager);
            True(File.Exists(paths.LayoutBackupPath), "第二次保存有效布局后应产生备份");
            File.WriteAllText(paths.LayoutPath, "{ invalid layout envelope");

            var backupContent = restoredContent.Keys.ToDictionary(
                contentId => contentId,
                _ => (object)NewPanelHost(),
                StringComparer.Ordinal);
            var backupManager = new DockingManager();
            True(layoutService.TryRestore(backupManager, backupContent),
                "主布局损坏时应从有效备份恢复");
            Equal("backup", layoutService.LastRecoverySource, "布局恢复来源");
            AssertDefaultTopology(backupManager);

            var incompleteContent = backupContent
                .Where(pair => !string.Equals(pair.Key, MainWindow.ProjectContentId, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var incompleteManager = new DockingManager();
            False(layoutService.TryRestore(incompleteManager, incompleteContent),
                "缺少任一已知 ContentId 的布局不得被当作有效布局恢复");
            Equal("default", layoutService.LastRecoverySource, "不完整布局应回退默认布局");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void ProjectValidatorRejectsDuplicateLayerMounts()
    {
        var duplicateRoot = DefaultUiProjectFactory.Create();
        var duplicateRootDocument = duplicateRoot.DocumentsById[duplicateRoot.ActiveDocumentId];
        duplicateRootDocument.RootIds.Insert(0, duplicateRootDocument.RootIds[0]);
        Throws<InvalidDataException>(() => ProjectValidator.Validate(duplicateRoot),
            "重复 RootIds 应被拒绝");

        var duplicateChild = DefaultUiProjectFactory.Create();
        var duplicateChildDocument = duplicateChild.DocumentsById[duplicateChild.ActiveDocumentId];
        var group = duplicateChildDocument.LayersById["group-content"];
        group.ChildIds.Add(group.ChildIds[0]);
        Throws<InvalidDataException>(() => ProjectValidator.Validate(duplicateChild),
            "重复 ChildIds 应被拒绝");
    }

    private static void RefreshTilemapGridBindings(TilemapGridSurface grid)
    {
        var cellSizeBinding = grid.GetBindingExpression(TilemapGridSurface.CellSizeProperty)
            ?? throw new SmokeTestException("TilemapGridSurface.CellSize 未绑定 GridUnitSize。");
        var diamondAngleBinding = grid.GetBindingExpression(TilemapGridSurface.DiamondAngleProperty)
            ?? throw new SmokeTestException("TilemapGridSurface.DiamondAngle 未绑定参数模型。");
        var gridRotationBinding = grid.GetBindingExpression(TilemapGridSurface.GridRotationProperty)
            ?? throw new SmokeTestException("TilemapGridSurface.GridRotation 未绑定参数模型。");
        cellSizeBinding.UpdateTarget();
        diamondAngleBinding.UpdateTarget();
        gridRotationBinding.UpdateTarget();
    }

    private static void AssertDiamondCellCenter(
        TilemapGridSurface grid,
        int cellA,
        int cellB,
        string message)
    {
        var expectedCenter = grid.GetScreenPoint(cellA + 0.5d, cellB + 0.5d);
        True(double.IsFinite(expectedCenter.X) && double.IsFinite(expectedCenter.Y),
            $"{message}正变换应返回有限坐标");
        Equal((cellA, cellB), grid.GetCellAt(expectedCenter), $"{message}命中坐标");

        var coordinates = grid.GetGridCoordinates(expectedCenter);
        Near(cellA + 0.5d, coordinates.X, 0.000000001, $"{message}连续 A 坐标");
        Near(cellB + 0.5d, coordinates.Y, 0.000000001, $"{message}连续 B 坐标");

        var cellCenter = grid.GetCellCenter(cellA, cellB);
        Near(expectedCenter.X, cellCenter.X, 0.000000001, $"{message}格中心 X");
        Near(expectedCenter.Y, cellCenter.Y, 0.000000001, $"{message}格中心 Y");
    }

    private static T RequireBoundElement<T>(
        DependencyObject root,
        DependencyProperty property,
        string bindingPath,
        string message)
        where T : DependencyObject
    {
        return VisualDescendants<T>(root).FirstOrDefault(candidate =>
                   BindingOperations.GetBindingBase(candidate, property) is Binding binding &&
                   string.Equals(binding.Path?.Path, bindingPath, StringComparison.Ordinal))
               ?? throw new SmokeTestException(message);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T rootMatch)
        {
            yield return rootMatch;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            foreach (var match in VisualDescendants<T>(child))
            {
                yield return match;
            }
        }
    }

    private static int GetAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void WaitUntilWithDispatcher(
        Func<bool> condition,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            PumpDispatcher();
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        PumpDispatcher();
        if (!condition())
        {
            throw new SmokeTestException(timeoutMessage);
        }
    }

    private static T RunWithoutDispatcherContext<T>(Func<Task<T>> operation) =>
        Task.Run(operation).GetAwaiter().GetResult();

    private static JsonElement CallHttpMcpToolSuccess(
        HttpClient client,
        Uri endpoint,
        string sessionId,
        ref int nextRpcId,
        string toolName,
        object arguments)
    {
        var rpcId = nextRpcId++;
        using var response = RunWithoutDispatcherContext(() => CallHttpMcpAsync(
            client,
            endpoint,
            new
            {
                jsonrpc = "2.0",
                id = rpcId,
                method = "tools/call",
                @params = new { name = toolName, arguments }
            },
            sessionId));
        Equal(HttpStatusCode.OK, response.StatusCode, $"HTTP MCP {toolName} 状态码");
        var payload = response.Payload
            ?? throw new SmokeTestException($"HTTP MCP {toolName} 未返回 JSON-RPC 响应体。");
        if (payload.RootElement.TryGetProperty("error", out var rpcError))
        {
            throw new SmokeTestException($"HTTP MCP {toolName} 返回 JSON-RPC 错误：{rpcError}");
        }

        var result = payload.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var errorElement) && errorElement.GetBoolean();
        if (isError)
        {
            var message = result.GetProperty("content")[0].GetProperty("text").GetString();
            throw new SmokeTestException($"HTTP MCP {toolName} 工具执行失败：{message}");
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static string CallHttpMcpToolError(
        HttpClient client,
        Uri endpoint,
        string sessionId,
        ref int nextRpcId,
        string toolName,
        object arguments)
    {
        var rpcId = nextRpcId++;
        using var response = RunWithoutDispatcherContext(() => CallHttpMcpAsync(
            client,
            endpoint,
            new
            {
                jsonrpc = "2.0",
                id = rpcId,
                method = "tools/call",
                @params = new { name = toolName, arguments }
            },
            sessionId));
        Equal(HttpStatusCode.OK, response.StatusCode, $"HTTP MCP {toolName} 拒绝请求状态码");
        var payload = response.Payload
            ?? throw new SmokeTestException($"HTTP MCP {toolName} 拒绝请求未返回响应体。");
        var result = payload.RootElement.GetProperty("result");
        True(result.TryGetProperty("isError", out var errorElement) && errorElement.GetBoolean(),
            $"HTTP MCP {toolName} 应以工具错误拒绝无效请求");
        return result.GetProperty("content")[0].GetProperty("text").GetString()
               ?? throw new SmokeTestException($"HTTP MCP {toolName} 工具错误缺少文字说明。");
    }

    private static string CreateAndClaimHttpGenerationTask(
        HttpClient client,
        Uri endpoint,
        string sessionId,
        ref int nextRpcId,
        string displayName,
        GenerationConstraintPackage constraints,
        string agentId)
    {
        var created = CallHttpMcpToolSuccess(
            client,
            endpoint,
            sessionId,
            ref nextRpcId,
            "create_generation_task",
            new
            {
                displayName,
                requestedBy = "GAssetForge.SmokeTests",
                constraints
            });
        var taskId = created.GetProperty("task").GetProperty("id").GetString()
            ?? throw new SmokeTestException("MCP 创建校验任务未返回 taskId。");
        var claimed = CallHttpMcpToolSuccess(
            client,
            endpoint,
            sessionId,
            ref nextRpcId,
            "claim_generation_task",
            new { taskId, agentId });
        Equal(taskId, claimed.GetProperty("task").GetProperty("id").GetString(),
            $"MCP 应认领校验任务 {displayName}");
        return taskId;
    }

    private static GenerationConstraintPackage CloneConstraintsWithDeliveryTarget(
        GenerationConstraintPackage template,
        string targetRelativePath)
    {
        var parameters = template.Parameters.DeepClone().AsObject();
        var delivery = parameters["delivery"] as JsonObject
            ?? throw new SmokeTestException("克隆素材生成约束时缺少 parameters.delivery。");
        delivery["targetPaths"] = new JsonArray(JsonValue.Create(targetRelativePath));
        delivery["suggestedBaseName"] = Path.GetFileNameWithoutExtension(targetRelativePath);
        return template with { Parameters = parameters };
    }

    private static string GetDeliveryTarget(JsonGenerationTaskStore store, string taskId)
    {
        var task = store.GetAsync(taskId).GetAwaiter().GetResult()
            ?? throw new SmokeTestException($"校验任务不存在：{taskId}");
        True(MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
             delivery is not null,
            $"校验任务 {taskId} 缺少有效 delivery");
        return delivery!.TargetPaths.Single();
    }

    private static string ResolveDeliveryTargetFullPath(string assetRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            assetRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        True(AssetRootService.IsWithinRoot(assetRoot, fullPath),
            $"测试 delivery 路径越出了 Assets 根目录：{relativePath}");
        return fullPath;
    }

    private static string CompleteHttpGenerationTaskExpectingError(
        HttpClient client,
        Uri endpoint,
        string sessionId,
        ref int nextRpcId,
        string taskId,
        string assetPath) =>
        CallHttpMcpToolError(
            client,
            endpoint,
            sessionId,
            ref nextRpcId,
            "complete_generation_task",
            new
            {
                taskId,
                agentId = "asset-generation-smoke-agent",
                assetPaths = new[] { assetPath }
            });

    internal static void WritePngFixture(
        string fullPath,
        int width,
        int height,
        bool includeAlpha,
        double? diamondAngle = null,
        double rotationAngle = 0d,
        bool forceFullyTransparent = false,
        TilemapRotationConvention tilemapRotationConvention = TilemapRotationConvention.AxisAAligned,
        Func<int, int, bool>? foregroundPredicate = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG fixture 尺寸必须为正数。");
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"PNG fixture 路径缺少父目录：{fullPath}");
        Directory.CreateDirectory(directory);

        var bytesPerPixel = includeAlpha ? 4 : 3;
        var rowStride = checked(width * bytesPerPixel + 1);
        var rawPixels = new byte[checked(rowStride * height)];
        for (var y = 0; y < height; y++)
        {
            var offset = y * rowStride;
            rawPixels[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                rawPixels[offset++] = (byte)(36 + x % 160);
                rawPixels[offset++] = (byte)(52 + y % 140);
                rawPixels[offset++] = (byte)(28 + (x + y) % 120);
                if (includeAlpha)
                {
                    var outsideRequiredRegion = forceFullyTransparent ||
                        (foregroundPredicate is not null
                            ? !foregroundPredicate(x, y)
                            : diamondAngle is not null
                                ? IsOutsideTilemapDiamond(
                                    x,
                                    y,
                                    width,
                                    height,
                                    diamondAngle.Value,
                                    rotationAngle,
                                    tilemapRotationConvention)
                                : x == 0 || y == 0 || x == width - 1 || y == height - 1);
                    rawPixels[offset++] = outsideRequiredRegion
                        ? (byte)0
                        : (byte)220;
                }
            }
        }

        byte[] compressedPixels;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(rawPixels);
            }

            compressedPixels = compressed.ToArray();
        }

        using var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
        header[8] = 8;
        header[9] = includeAlpha ? (byte)6 : (byte)2;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(output, "IHDR", header);
        WritePngChunk(output, "IDAT", compressedPixels);
        WritePngChunk(output, "IEND", []);
    }

    private static void WritePngFixtureWithEmptyDecodedPayload(
        string fullPath,
        int width,
        int height,
        byte bitDepth = 8)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"PNG fixture 路径缺少父目录：{fullPath}");
        Directory.CreateDirectory(directory);

        // Valid zlib stream containing one final empty DEFLATE block plus Adler-32(empty).
        // The PNG chunks and CRCs remain valid, so only actual IDAT decoding rejects it.
        byte[] compressedPixels = [0x78, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];

        using var output = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
        header[8] = bitDepth;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(output, "IHDR", header);
        WritePngChunk(output, "IDAT", compressedPixels);
        WritePngChunk(output, "IEND", []);
    }

    private static bool IsOutsideTilemapDiamond(
        int x,
        int y,
        int width,
        int height,
        double diamondAngle,
        double rotationAngle,
        TilemapRotationConvention rotationConvention)
    {
        if (!double.IsFinite(diamondAngle) || diamondAngle is <= 0d or >= 180d)
        {
            throw new ArgumentOutOfRangeException(nameof(diamondAngle));
        }

        if (!double.IsFinite(rotationAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationAngle));
        }

        var halfAngle = diamondAngle * Math.PI / 360d;
        var halfWidth = Math.Cos(halfAngle);
        var halfHeight = Math.Sin(halfAngle);
        var rotation = TilemapGeometryCalculator.CalculateEffectiveRotationRadians(
            diamondAngle,
            rotationAngle,
            rotationConvention);
        var cosine = Math.Cos(rotation);
        var sine = Math.Sin(rotation);
        var vertices = new[]
        {
            RotateFixturePoint(-halfWidth, 0d, cosine, sine),
            RotateFixturePoint(0d, -halfHeight, cosine, sine),
            RotateFixturePoint(halfWidth, 0d, cosine, sine),
            RotateFixturePoint(0d, halfHeight, cosine, sine)
        };
        var minimumX = vertices.Min(vertex => vertex.X);
        var maximumX = vertices.Max(vertex => vertex.X);
        var minimumY = vertices.Min(vertex => vertex.Y);
        var maximumY = vertices.Max(vertex => vertex.Y);
        var rotatedX = minimumX + (x + 0.5d) * (maximumX - minimumX) / width;
        var rotatedY = minimumY + (y + 0.5d) * (maximumY - minimumY) / height;
        var localX = rotatedX * cosine + rotatedY * sine;
        var localY = -rotatedX * sine + rotatedY * cosine;
        return Math.Abs(localX) / halfWidth + Math.Abs(localY) / halfHeight > 1d;
    }

    private static (double X, double Y) RotateFixturePoint(
        double x,
        double y,
        double cosine,
        double sine) =>
        (x * cosine - y * sine, x * sine + y * cosine);

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        if (typeBytes.Length != 4)
        {
            throw new ArgumentException("PNG chunk type 必须为四个 ASCII 字符。", nameof(type));
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, checked((uint)data.Length));
        output.Write(lengthBytes);
        output.Write(typeBytes);
        output.Write(data);

        var crc = UpdatePngCrc(0xFFFFFFFFu, typeBytes);
        crc = UpdatePngCrc(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint UpdatePngCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }
        }

        return crc;
    }

    private static async Task<HttpMcpResponse> CallHttpMcpAsync(
        HttpClient client,
        Uri endpoint,
        object message,
        string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var returnedSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)
            ? sessionValues.SingleOrDefault()
            : null;
        JsonDocument? payload = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                payload = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new SmokeTestException(
                    $"HTTP MCP 返回了无效 JSON：{body}{Environment.NewLine}{exception.Message}");
            }
        }

        return new HttpMcpResponse(response.StatusCode, returnedSessionId, payload);
    }

    private static async Task<JsonDocument> WaitForHealthStatusAsync(
        HttpClient client,
        Uri healthEndpoint,
        string expectedStatus,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastObservation = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var response = await client.GetAsync(healthEndpoint);
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var document = JsonDocument.Parse(body);
                    var actualStatus = document.RootElement.TryGetProperty("status", out var statusElement)
                        ? statusElement.GetString()
                        : null;
                    if (string.Equals(actualStatus, expectedStatus, StringComparison.Ordinal))
                    {
                        return document;
                    }

                    lastObservation = $"health status={actualStatus ?? "<null>"}";
                    document.Dispose();
                }
                else
                {
                    lastObservation = $"HTTP {(int)response.StatusCode}: {body}";
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastObservation = exception.Message;
            }

            await Task.Delay(75);
        }

        throw new SmokeTestException(
            $"等待 health={expectedStatus} 超时。最后结果：{lastObservation ?? "未收到响应"}");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        if (!condition())
        {
            throw new SmokeTestException(timeoutMessage);
        }
    }

    private static async Task<bool> CanReachHealthAsync(HttpClient client, Uri healthEndpoint)
    {
        try
        {
            using var response = await client.GetAsync(healthEndpoint);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static JsonDocument CallMcpTool(Process process, int id, string name, object arguments) =>
        CallMcp(process, new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments }
        });

    private static JsonDocument CallMcp(Process process, object request)
    {
        SendMcp(process, request);

        string? responseLine;
        try
        {
            responseLine = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException exception)
        {
            var error = process.HasExited ? process.StandardError.ReadToEnd() : "服务器仍在运行但未返回响应";
            throw new SmokeTestException($"等待 MCP 响应超时：{error}\n{exception.Message}");
        }

        if (responseLine is null)
        {
            process.WaitForExit(1_000);
            var error = process.StandardError.ReadToEnd();
            throw new SmokeTestException($"MCP Server 在返回响应前退出：{error}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseLine);
        }
        catch (JsonException exception)
        {
            throw new SmokeTestException($"MCP stdout 包含非协议内容：{responseLine}\n{exception.Message}");
        }

        if (document.RootElement.TryGetProperty("error", out var rpcError))
        {
            var message = rpcError.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : rpcError.GetRawText();
            document.Dispose();
            throw new SmokeTestException($"MCP JSON-RPC 返回错误：{message}");
        }

        return document;
    }

    private static void SendMcp(Process process, object message)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        process.StandardInput.Flush();
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GAssetForge.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new SmokeTestException("无法定位 GAssetForge.sln 所在的仓库根目录。");
    }

    private static T RequireNamedElement<T>(FrameworkElement owner, string name, string message)
        where T : FrameworkElement =>
        owner.FindName(name) as T ?? throw new SmokeTestException(message);

    private static ContentControl NewPanelHost() => new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    private static LayerViewModel FindLayer(IEnumerable<LayerViewModel> roots, string layerId)
    {
        foreach (var layer in roots)
        {
            if (string.Equals(layer.Id, layerId, StringComparison.Ordinal))
            {
                return layer;
            }

            try
            {
                return FindLayer(layer.Children, layerId);
            }
            catch (SmokeTestException)
            {
                // Continue searching sibling branches.
            }
        }

        throw new SmokeTestException($"图层树中未找到 {layerId}。");
    }

    private static void AssertBusinessDocumentViews(DockingManager manager)
    {
        var uiHost = FindDockContent(manager, MainWindow.CanvasContentId)?.Content as ContentControl
            ?? throw new SmokeTestException("页面 UI 业务文档缺少稳定 host。");
        var tilemapHost = FindDockContent(manager, MainWindow.TilemapWorkspaceContentId)?.Content as ContentControl
            ?? throw new SmokeTestException("Tilemap 业务文档缺少稳定 host。");
        var cardHost = FindDockContent(manager, MainWindow.CardDesignWorkspaceContentId)?.Content as ContentControl
            ?? throw new SmokeTestException("卡牌设计业务文档缺少稳定 host。");
        var parallelHost = FindDockContent(manager, MainWindow.ParallelPerspectiveWorkspaceContentId)?.Content as ContentControl
            ?? throw new SmokeTestException("平行透视业务文档缺少稳定 host。");
        True(uiHost.Content is CanvasDocumentView,
            "页面 UI 业务文档应承载独立 CanvasDocumentView");
        True(tilemapHost.Content is TilemapWorkspaceView,
            "Tilemap 业务文档应承载独立 TilemapWorkspaceView");
        True(cardHost.Content is WorkflowPlaceholderView
             {
                 DataContext: WorkflowPlaceholderViewModel
                 {
                     Workflow.Kind: WorkflowKind.CardDesign
                 }
             },
            "卡牌设计应拥有稳定的正常业务文档页");
        True(parallelHost.Content is WorkflowPlaceholderView
             {
                 DataContext: WorkflowPlaceholderViewModel
                 {
                     Workflow.Kind: WorkflowKind.ParallelPerspective
                 }
             },
            "平行透视角物体制作应拥有稳定的正常业务文档页");
    }

    private static void AssertDefaultTopology(DockingManager manager)
    {
        var contents = manager.Layout.Descendents()
            .OfType<LayoutContent>()
            .ToDictionary(content => content.ContentId ?? string.Empty, StringComparer.Ordinal);
        var expectedIds = new[]
        {
            MainWindow.ToolsContentId,
            MainWindow.CanvasContentId,
            MainWindow.TilemapWorkspaceContentId,
            MainWindow.CardDesignWorkspaceContentId,
            MainWindow.ParallelPerspectiveWorkspaceContentId,
            MainWindow.PropertiesContentId,
            MainWindow.LayersContentId,
            MainWindow.ProjectContentId,
            MainWindow.StyleLibraryContentId,
            MainWindow.AssetGenerationContentId,
            MainWindow.BackgroundRemovalContentId,
            MainWindow.ImageOutlineContentId,
            MainWindow.IconDesignContentId,
            MainWindow.PixelAlignmentContentId,
            MainWindow.BlueprintContentId,
            MainWindow.AgentConnectionContentId,
            MainWindow.CommandConsoleContentId
        };

        Equal(expectedIds.Length, contents.Count, "默认布局面板数量");
        foreach (var contentId in expectedIds)
        {
            True(contents.ContainsKey(contentId), $"默认布局缺少 {contentId}");
        }

        var documentIds = new[]
        {
            MainWindow.CanvasContentId,
            MainWindow.TilemapWorkspaceContentId,
            MainWindow.CardDesignWorkspaceContentId,
            MainWindow.ParallelPerspectiveWorkspaceContentId
        };
        foreach (var contentId in documentIds)
        {
            var document = contents[contentId] as LayoutDocument
                ?? throw new SmokeTestException($"{contentId} 必须是 LayoutDocument。");
            True(document.CanClose, $"{contentId} 可从顶部文档标签关闭");
            True(document.CanFloat, $"{contentId} 可浮动");
            True(document.CanMove, $"{contentId} 可拖动");
        }

        foreach (var contentId in expectedIds.Except(documentIds, StringComparer.Ordinal))
        {
            var anchorable = contents[contentId] as LayoutAnchorable
                ?? throw new SmokeTestException($"{contentId} 必须是 LayoutAnchorable。");
            True(anchorable.CanMove, $"{contentId} 可拖动");
            True(anchorable.CanFloat, $"{contentId} 可浮动");
            True(anchorable.CanHide, $"{contentId} 可隐藏");
            False(anchorable.CanClose, $"{contentId} 不可永久关闭");
            True(anchorable.CanAutoHide, $"{contentId} 可自动隐藏");
            True(anchorable.CanDockAsTabbedDocument,
                $"{contentId} 应允许停靠进中央文档标签区");
            True(anchorable.FloatingWidth > 0 && anchorable.FloatingHeight > 0,
                $"{contentId} 应提供合理的默认浮窗尺寸");
        }

        var agentConnection = contents[MainWindow.AgentConnectionContentId] as LayoutAnchorable
            ?? throw new SmokeTestException("mcp-agent-connection 必须是 LayoutAnchorable。");
        True(agentConnection.IsHidden, "MCP Agent 接入面板初始应隐藏");
        False(agentConnection.IsVisible, "MCP Agent 接入面板初始不应占用工作区");
        True(manager.Layout.Hidden.Contains(agentConnection),
            "MCP Agent 接入面板初始应登记在 AvalonDock Hidden 集合");

        var root = manager.Layout.RootPanel
            ?? throw new SmokeTestException("默认布局缺少 RootPanel。");
        Equal(System.Windows.Controls.Orientation.Horizontal, root.Orientation, "根布局方向");
        Equal(3, root.Children.Count, "根布局分区数");

        AssertPaneContains(root.Children[0], MainWindow.ToolsContentId, "左侧工具区");

        var center = root.Children[1] as LayoutPanel
            ?? throw new SmokeTestException("中间区域必须是 LayoutPanel。");
        Equal(System.Windows.Controls.Orientation.Vertical, center.Orientation, "中间布局方向");
        Equal(3, center.Children.Count, "中间布局分区数");
        True(center.Children[0] is LayoutDocumentPane,
            "中央主工作区必须由 LayoutDocumentPane 承载顶部文档标签");
        foreach (var documentId in documentIds)
        {
            AssertPaneContains(center.Children[0], documentId, "中央业务窗口区");
        }
        True(center.Children[1] is LayoutAnchorablePane,
            "中央下方应保留 MCP Agent 接入停靠区域");
        AssertPaneContains(center.Children[2], MainWindow.ProjectContentId, "下方 Project 区");
        AssertPaneContains(center.Children[2], MainWindow.StyleLibraryContentId, "下方美术风格素材库区");
        AssertPaneContains(center.Children[2], MainWindow.AssetGenerationContentId, "下方素材生成区");
        AssertPaneContains(center.Children[2], MainWindow.BackgroundRemovalContentId, "下方背景剔除工具区");
        AssertPaneContains(center.Children[2], MainWindow.ImageOutlineContentId, "下方图片描边工具区");
        AssertPaneContains(center.Children[2], MainWindow.IconDesignContentId, "下方 Icon 图标制作区");
        AssertPaneContains(center.Children[2], MainWindow.PixelAlignmentContentId, "下方像素点对齐工具区");
        AssertPaneContains(center.Children[2], MainWindow.BlueprintContentId, "下方蓝图窗口区");
        AssertPaneContains(center.Children[2], MainWindow.CommandConsoleContentId, "下方指令日志区");

        var right = root.Children[2] as LayoutPanel
            ?? throw new SmokeTestException("右侧区域必须是 LayoutPanel。");
        Equal(System.Windows.Controls.Orientation.Vertical, right.Orientation, "右侧布局方向");
        Equal(2, right.Children.Count, "右侧布局分区数");
        AssertPaneContains(right.Children[0], MainWindow.PropertiesContentId, "右上属性区");
        AssertPaneContains(right.Children[1], MainWindow.LayersContentId, "右下图层区");
    }

    private static void AssertAnchorableTabsUseTopHeaders(MainWindow window, DockingManager manager)
    {
        var shell = window.DataContext as ShellViewModel
            ?? throw new SmokeTestException("MainWindow 未绑定 ShellViewModel。");
        if (shell.IsNewProjectDialogOpen && shell.CloseNewProjectCommand.CanExecute(null))
        {
            shell.CloseNewProjectCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
        }

        var paneStyle = window.FindResource("App.DockAnchorablePaneControlStyle") as Style
            ?? throw new SmokeTestException("MainWindow 缺少 Anchorable Pane 顶部标签样式。");
        var placementSetter = paneStyle.Setters
            .OfType<Setter>()
            .SingleOrDefault(setter => setter.Property == TabControl.TabStripPlacementProperty)
            ?? throw new SmokeTestException("Anchorable Pane 样式未显式设置 TabStripPlacement。");
        Equal(Dock.Top, placementSetter.Value is Dock dock ? dock : default,
            "Anchorable Pane 标签栏应固定在顶部");

        var paneControls = VisualDescendants<LayoutAnchorablePaneControl>(manager).ToArray();
        True(paneControls.Length > 0, "默认布局应生成 Anchorable Pane 可视控件");
        foreach (var paneControl in paneControls)
        {
            Equal(Dock.Top, paneControl.TabStripPlacement,
                "每个可见 Anchorable Pane 控件都应在顶部显示标签");
        }

        var canvas = FindDockContent(manager, MainWindow.CanvasContentId) as LayoutDocument
            ?? throw new SmokeTestException("顶部标签测试未找到主视图 LayoutDocument。");
        var canvasTab = VisualDescendants<LayoutDocumentTabItem>(manager)
            .FirstOrDefault(tab => ReferenceEquals(tab.Model, canvas))
            ?? throw new SmokeTestException("主视图未生成 LayoutDocumentTabItem。");
        var canvasTitle = VisualDescendants<TextBlock>(canvasTab)
            .FirstOrDefault(text => string.Equals(text.Text, canvas.Title, StringComparison.Ordinal))
            ?? throw new SmokeTestException("主视图文档标签未呈现标题文字。");
        var titleBounds = canvasTitle.TransformToAncestor(canvasTab)
            .TransformBounds(new Rect(canvasTitle.RenderSize));
        True(canvasTitle.ActualWidth > 0 && canvasTitle.ActualHeight > 0,
            "主视图文档标签标题必须具有可见尺寸");
        True(titleBounds.Top >= -0.5 && titleBounds.Bottom <= canvasTab.RenderSize.Height + 0.5,
            "主视图文档标签文字必须完整位于标签可视高度内，不能被上下裁切");
        True(titleBounds.Left >= -0.5 && titleBounds.Right <= canvasTab.RenderSize.Width + 0.5,
            "主视图文档标签文字必须完整位于标签可视宽度内");
        canvas.IsSelected = true;
        canvas.IsActive = true;
        manager.ActiveContent = canvas.Content;
        PumpDispatcher();
        window.UpdateLayout();
        var canvasLayoutItem = canvasTab.LayoutItem as LayoutDocumentItem
            ?? throw new SmokeTestException("主视图标签未关联 LayoutDocumentItem。");
        var canvasCloseButton = VisualDescendants<Button>(canvasTab)
            .FirstOrDefault(button => ReferenceEquals(button.Command, canvasLayoutItem.CloseCommand));
        True(canvasCloseButton is
            {
                Visibility: Visibility.Visible,
                ActualWidth: > 0,
                ActualHeight: > 0,
                IsEnabled: true,
                IsHitTestVisible: true
            }, "主视图文档标签应保留 AvalonDock 原生可点击关闭按钮");
        True(canvasCloseButton!.Command.CanExecute(canvasCloseButton.CommandParameter),
            "主视图文档标签关闭按钮当前应可执行");

        var internalTitles = VisualDescendants<AnchorablePaneTitle>(manager).ToArray();
        True(internalTitles.All(title => title.Visibility == Visibility.Collapsed),
            "工具停靠区内部标题条应被省略或折叠，避免与顶部标签重复");

        var singleContentPanes = paneControls
            .Where(control => control.Model is LayoutAnchorablePane { ChildrenCount: 1 })
            .ToArray();
        True(singleContentPanes.Length > 0,
            "默认布局应包含工具、属性或图层等单标签停靠区");
        foreach (var paneControl in singleContentPanes)
        {
            var pane = (LayoutAnchorablePane)paneControl.Model;
            var visibleTab = VisualDescendants<LayoutAnchorableTabItem>(paneControl)
                .FirstOrDefault(tab =>
                    tab.Visibility == Visibility.Visible &&
                    ReferenceEquals(tab.Model, pane.Children[0]));
            True(visibleTab is not null,
                $"单标签停靠区 {pane.Children[0].ContentId} 也必须显示顶部标签");
        }

        var visibleAnchorableTabs = VisualDescendants<LayoutAnchorableTabItem>(manager)
            .Where(tab => tab.Visibility == Visibility.Visible && tab.Model is LayoutAnchorable)
            .ToArray();
        True(visibleAnchorableTabs.Length > 0,
            "默认布局应呈现可关闭的工具窗口标签");
        foreach (var tab in visibleAnchorableTabs)
        {
            var model = (LayoutAnchorable)tab.Model;
            var tabCloseButton = VisualDescendants<Button>(tab)
                .FirstOrDefault(button => ReferenceEquals(button.Tag, model));
            True(tabCloseButton is { Visibility: Visibility.Visible },
                $"工具窗口 {model.ContentId} 的顶部标签应显示关闭按钮");
            var layoutItem = tab.LayoutItem as LayoutAnchorableItem
                ?? throw new SmokeTestException($"工具标签 {model.ContentId} 未关联 LayoutAnchorableItem。");
            True(ReferenceEquals(tabCloseButton!.Command, layoutItem.HideCommand),
                $"工具窗口 {model.ContentId} 的关闭按钮必须绑定 HideCommand");
            True(tabCloseButton.ActualWidth > 0 && tabCloseButton.ActualHeight > 0,
                $"工具窗口 {model.ContentId} 的关闭按钮必须占有可见尺寸");
            True(tabCloseButton.IsEnabled && tabCloseButton.IsHitTestVisible,
                $"工具窗口 {model.ContentId} 的关闭按钮必须可点击");
            True(tabCloseButton.Command.CanExecute(tabCloseButton.CommandParameter),
                $"工具窗口 {model.ContentId} 的 HideCommand 当前应可执行");
        }

        var tools = FindDockContent(manager, MainWindow.ToolsContentId) as LayoutAnchorable
            ?? throw new SmokeTestException("顶部标签测试未找到工具面板。");
        var toolsTab = visibleAnchorableTabs
            .FirstOrDefault(tab => ReferenceEquals(tab.Model, tools))
            ?? throw new SmokeTestException("工具面板未生成 LayoutAnchorableTabItem。");
        var closeButton = VisualDescendants<Button>(toolsTab)
            .First(button => ReferenceEquals(button.Tag, tools));
        var toolsLayoutItem = toolsTab.LayoutItem as LayoutAnchorableItem
            ?? throw new SmokeTestException("工具标签未关联 LayoutAnchorableItem。");
        True(ReferenceEquals(closeButton.Command, toolsLayoutItem.HideCommand),
            "工具窗口标签关闭按钮必须绑定 AvalonDock HideCommand，而不是永久 Close");
        True(closeButton.Command.CanExecute(closeButton.CommandParameter),
            "工具窗口标签关闭按钮当前应可执行");
        closeButton.Command.Execute(closeButton.CommandParameter);
        PumpDispatcher();
        True(tools.IsHidden,
            "工具窗口标签关闭按钮应执行 Hide，保留通过视图菜单恢复的能力");
        True(manager.Layout.Hidden.Contains(tools),
            "工具窗口隐藏后应进入 AvalonDock Hidden 集合");
        shell.ShowPanelCommand.Execute(MainWindow.ToolsContentId);
        PumpDispatcher();
        True(ReferenceEquals(FindDockContent(manager, MainWindow.ToolsContentId), tools),
            "视图菜单恢复工具窗口时应复用同一 LayoutAnchorable 实例");
        True(tools.IsVisible && !tools.IsHidden && tools.IsActive,
            "工具窗口经视图菜单恢复后应可见并激活");
    }

    private static void AssertPaneContains(ILayoutElement element, string contentId, string label)
    {
        if (element is not ILayoutContainer container ||
            !container.Descendents().OfType<LayoutContent>()
                .Any(content => string.Equals(content.ContentId, contentId, StringComparison.Ordinal)))
        {
            throw new SmokeTestException($"{label}未包含 {contentId}。");
        }
    }

    private static LayoutContent? FindDockContent(DockingManager manager, string contentId)
    {
        return manager.Layout.Descendents()
                   .OfType<LayoutContent>()
                   .FirstOrDefault(content => string.Equals(content.ContentId, contentId, StringComparison.Ordinal))
               ?? manager.Layout.Hidden.FirstOrDefault(content =>
                   string.Equals(content.ContentId, contentId, StringComparison.Ordinal));
    }

    private static void AssertWorkspaceStateIsConsistent(ShellViewModel shell, string? propertyName)
    {
        switch (shell.CurrentWorkflow.Kind)
        {
            case WorkflowKind.UiDesign:
                True(ReferenceEquals(shell.EditorWorkspace, shell.CurrentWorkspace) &&
                     shell.PlaceholderWorkspace is null,
                    $"{propertyName} 发布时 UI 工作流状态必须一致");
                break;
            case WorkflowKind.Tilemap:
                True(ReferenceEquals(shell.TilemapWorkspace, shell.CurrentWorkspace) &&
                     shell.PlaceholderWorkspace is null,
                    $"{propertyName} 发布时 Tilemap 工作流状态必须一致");
                break;
            default:
                True(shell.PlaceholderWorkspace is not null &&
                     ReferenceEquals(shell.PlaceholderWorkspace, shell.CurrentWorkspace) &&
                     Equals(shell.CurrentWorkflow, shell.PlaceholderWorkspace.Workflow),
                    $"{propertyName} 发布时占位工作流状态必须一致");
                break;
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new SmokeTestException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new SmokeTestException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new SmokeTestException($"{message}：预期 {expected} ± {tolerance}，实际 {actual}。");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        var expectedItems = expected.ToArray();
        var actualItems = actual.ToArray();
        if (!expectedItems.SequenceEqual(actualItems))
        {
            throw new SmokeTestException(
                $"{message}：预期 [{string.Join(", ", expectedItems)}]，实际 [{string.Join(", ", actualItems)}]。");
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new SmokeTestException(message);
    }

    private static void DeleteTemporaryRoot()
    {
        if (string.IsNullOrWhiteSpace(_temporaryRoot) || !Directory.Exists(_temporaryRoot))
        {
            return;
        }

        var fullRoot = Path.GetFullPath(_temporaryRoot);
        var safeParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GAssetForge.SmokeTests"));
        if (!fullRoot.StartsWith(safeParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"拒绝清理非 smoke test 临时目录：{fullRoot}");
        }

        Directory.Delete(fullRoot, recursive: true);
        if (Directory.Exists(safeParent) && !Directory.EnumerateFileSystemEntries(safeParent).Any())
        {
            Directory.Delete(safeParent);
        }
    }

    private sealed class HttpMcpResponse(
        HttpStatusCode statusCode,
        string? sessionId,
        JsonDocument? payload) : IDisposable
    {
        public HttpStatusCode StatusCode { get; } = statusCode;

        public string? SessionId { get; } = sessionId;

        public JsonDocument? Payload { get; } = payload;

        public void Dispose() => Payload?.Dispose();
    }

    private sealed class FixedArtStyleAssetPicker(IReadOnlyList<string> paths) : IArtStyleAssetPicker
    {
        public IReadOnlyList<string> SelectImageFiles() => paths;
    }

    private sealed class NeverRunArtStylePromptAnalysisService : IArtStylePromptAnalysisService
    {
        public Task<ArtStylePromptAnalysisResult> AnalyzeAsync(
            ArtStylePromptAnalysisInput input,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("命令状态测试不应实际启动风格分析。");
    }

    private sealed class SmokeTestException(string message) : Exception(message);
}
