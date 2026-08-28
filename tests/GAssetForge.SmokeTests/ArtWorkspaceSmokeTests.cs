using AvalonDock;
using AvalonDock.Layout;
using GAssetForge.App;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.ArtWorkspaces;
using GAssetForge.Core.Generation;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "art-workspace");
        var projectParent = Path.Combine(root, "projects");
        var fixtureDirectory = Path.Combine(root, "fixtures");
        Directory.CreateDirectory(projectParent);
        Directory.CreateDirectory(fixtureDirectory);

        var workspacePaths = new WorkspacePaths(Path.Combine(root, "desktop-data"));
        var projectService = new ProjectWorkspaceService();
        var projectA = projectService.Create(projectParent, "ArtWorkspaceA", "ui");
        var projectB = projectService.Create(projectParent, "ArtWorkspaceB", "tilemap");
        var cleanupProject = projectService.Create(projectParent, "ArtWorkspaceCleanup", "ui");
        var geometryProject = projectService.Create(projectParent, "ArtWorkspaceGeometry", "ui");
        var fixtureA = Path.Combine(fixtureDirectory, "reference-a.png");
        var fixtureB = Path.Combine(fixtureDirectory, "reference-b.png");
        var extremeTallFixture = Path.Combine(fixtureDirectory, "reference-extreme-tall.png");
        var extremeWideFixture = Path.Combine(fixtureDirectory, "reference-extreme-wide.png");
        SavePngFixture(fixtureA, 8, 4, 0x29, 0x78, 0xD1);
        SavePngFixture(fixtureB, 5, 9, 0xD8, 0x73, 0x2A);
        SavePngFixture(extremeTallFixture, 8, 8192, 0x42, 0x92, 0xD0);
        SavePngFixture(extremeWideFixture, 8192, 8, 0xD0, 0x78, 0x42);

        var repository = new JsonArtWorkspaceRepository(workspacePaths);
        ImportPersistenceAndProjectIsolationStayStable(
            workspacePaths,
            repository,
            projectA,
            projectB,
            fixtureA,
            fixtureB);
        InvalidSelectedRegionsDoNotSurviveProjectReload(
            workspacePaths,
            repository,
            projectParent,
            fixtureA);
        ValidatorAndConversationLimitsFailClosed(workspacePaths);
        AsyncImportAndUnusedAssetMetadataCleanupStayStable(
            workspacePaths,
            repository,
            cleanupProject,
            fixtureA);
        ExtremeAspectRatioLayerSelectionAndRotatedBoundsStayStable(
            workspacePaths,
            repository,
            geometryProject,
            extremeTallFixture,
            extremeWideFixture);
        BackupRecoveryAndPathBoundariesFailClosed(
            repository,
            projectA,
            projectB,
            root);
        AgentGatewayGenerationFlowStaysProjectSafe(
            workspacePaths,
            repository,
            fixtureA);
        FailedSaveKeepsActiveGenerationAlive(workspacePaths, repository);
        DockDocumentUsesArtWorkspaceContexts(Path.Combine(root, "dock-ui"));
    }

    private static void InvalidSelectedRegionsDoNotSurviveProjectReload(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository,
        string projectParent,
        string fixturePath)
    {
        var projectService = new ProjectWorkspaceService();
        var hiddenProject = projectService.Create(
            projectParent,
            $"ArtWorkspaceHiddenRegion-{Guid.NewGuid():N}",
            "ui");
        var missingProject = projectService.Create(
            projectParent,
            $"ArtWorkspaceMissingRegion-{Guid.NewGuid():N}",
            "ui");

        using (var setupConsole = new WorkbenchConsoleService(workspacePaths))
        using (var setup = new ArtWorkspaceViewModel(repository, new StubImagePicker(), setupConsole))
        {
            True(setup.Activate(hiddenProject.Paths.RootDirectory),
                "隐藏框选回归应激活正式项目");
            Equal(1, setup.AddImagesAt([fixturePath], 80d, 120d),
                "隐藏框选回归应导入受管图片");
            True(setup.SetSelectedImageRegionNormalized(0.2d, 0.3d, 0.4d, 0.5d, commit: true),
                "隐藏框选回归应建立局部区域");
            True(setup.SaveNow(), "隐藏框选回归应保存初始工作区");
        }

        var hiddenDocument = repository.LoadOrCreate(hiddenProject.Paths.RootDirectory).Document;
        hiddenDocument.Images.Single().IsVisible = false;
        True(hiddenDocument.SelectedImageRegion is not null,
            "隐藏框选回归夹具应保留旧版悬空区域");
        repository.Save(hiddenProject.Paths.RootDirectory, hiddenDocument);

        using (var hiddenConsole = new WorkbenchConsoleService(workspacePaths))
        using (var hiddenReload = new ArtWorkspaceViewModel(repository, new StubImagePicker(), hiddenConsole))
        {
            True(hiddenReload.Activate(hiddenProject.Paths.RootDirectory),
                "隐藏框选工作区应可重载");
            False(hiddenReload.HasSelectedImageRegion,
                "隐藏图片上的框选区域不得在项目重载后存活");
            False(hiddenReload.SelectedImage!.HasSelectedRegion,
                "隐藏图片模板不得恢复悬空框选视觉状态");
        }

        var sanitizedHiddenDocument = repository.LoadOrCreate(hiddenProject.Paths.RootDirectory).Document;
        True(sanitizedHiddenDocument.SelectedImageRegion is null,
            "隐藏图片的无效框选应在首次重载关闭时持久化清除");
        sanitizedHiddenDocument.Images.Single().IsVisible = true;
        repository.Save(hiddenProject.Paths.RootDirectory, sanitizedHiddenDocument);
        using (var visibleConsole = new WorkbenchConsoleService(workspacePaths))
        using (var visibleReload = new ArtWorkspaceViewModel(repository, new StubImagePicker(), visibleConsole))
        {
            True(visibleReload.Activate(hiddenProject.Paths.RootDirectory),
                "重新显示图片后的工作区应可再次重载");
            False(visibleReload.HasSelectedImageRegion,
                "重新显示图片并再次重载不得复活已废弃的旧框选区域");
        }

        string missingManagedPath;
        using (var setupConsole = new WorkbenchConsoleService(workspacePaths))
        using (var setup = new ArtWorkspaceViewModel(repository, new StubImagePicker(), setupConsole))
        {
            True(setup.Activate(missingProject.Paths.RootDirectory),
                "缺失框选回归应激活正式项目");
            Equal(1, setup.AddImagesAt([fixturePath], 160d, 200d),
                "缺失框选回归应导入受管图片");
            True(setup.SetSelectedImageRegionNormalized(0.1d, 0.15d, 0.6d, 0.7d, commit: true),
                "缺失框选回归应建立局部区域");
            missingManagedPath = setup.SelectedImage!.AbsolutePath;
            True(setup.SaveNow(), "缺失框选回归应保存初始工作区");
        }

        File.Delete(missingManagedPath);
        using (var missingConsole = new WorkbenchConsoleService(workspacePaths))
        using (var missingReload = new ArtWorkspaceViewModel(repository, new StubImagePicker(), missingConsole))
        {
            True(missingReload.Activate(missingProject.Paths.RootDirectory),
                "缺失框选工作区应可重载");
            True(missingReload.SelectedImage!.IsMissing,
                "缺失框选回归应识别受管文件不存在");
            False(missingReload.HasSelectedImageRegion,
                "缺失图片上的框选区域不得在项目重载后存活");
            False(missingReload.SelectedImage.HasSelectedRegion,
                "缺失图片模板不得恢复悬空框选视觉状态");
        }


        var sanitizedMissingDocument = repository.LoadOrCreate(missingProject.Paths.RootDirectory).Document;
        True(sanitizedMissingDocument.SelectedImageRegion is null,
            "缺失图片的无效框选应在首次重载关闭时持久化清除");
        File.Copy(fixturePath, missingManagedPath);
        using (var restoredConsole = new WorkbenchConsoleService(workspacePaths))
        using (var restoredReload = new ArtWorkspaceViewModel(repository, new StubImagePicker(), restoredConsole))
        {
            True(restoredReload.Activate(missingProject.Paths.RootDirectory),
                "受管文件恢复后的工作区应可再次重载");
            False(restoredReload.SelectedImage!.IsMissing,
                "恢复后的受管文件应重新可用");
            False(restoredReload.HasSelectedImageRegion,
                "受管文件恢复并再次重载不得复活已废弃的旧框选区域");
        }
    }

    private static void ValidatorAndConversationLimitsFailClosed(WorkspacePaths workspacePaths)
    {
        var projectService = new ProjectWorkspaceService();
        var capacityProject = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceCapacity-{Guid.NewGuid():N}",
            "ui");
        var sizeProject = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceSize-{Guid.NewGuid():N}",
            "ui");
        var maximumPixelDocument = ArtWorkspaceDocumentFactory.Create();
        maximumPixelDocument.Assets.Add(CreateManagedAsset(
            "Assets/ArtWorkspace/References/maximum.png",
            pixelWidth: 8192,
            pixelHeight: 8192));
        ArtWorkspaceValidator.Validate(maximumPixelDocument);

        var oversizedPixelDocument = ArtWorkspaceDocumentFactory.Create();
        oversizedPixelDocument.Assets.Add(CreateManagedAsset(
            "Assets/ArtWorkspace/References/oversized.png",
            pixelWidth: 8193,
            pixelHeight: 8192));
        Throws<InvalidDataException>(
            () => ArtWorkspaceValidator.Validate(oversizedPixelDocument),
            "素材总像素超过 67,108,864 时必须被拒绝");

        var escapedOutputDocument = ArtWorkspaceDocumentFactory.Create();
        escapedOutputDocument.OutputFrame.OutputRelativeDirectory = "Generated/ArtWorkspace";
        Throws<InvalidDataException>(
            () => ArtWorkspaceValidator.Validate(escapedOutputDocument),
            "输出目录不在 Assets 根目录时必须被拒绝");

        var maximumOutputDocument = ArtWorkspaceDocumentFactory.Create();
        maximumOutputDocument.OutputFrame.Width = 16384d;
        maximumOutputDocument.OutputFrame.Height = 16384d;
        ArtWorkspaceValidator.Validate(maximumOutputDocument);
        var oversizedOutputDocument = ArtWorkspaceDocumentFactory.Create();
        oversizedOutputDocument.OutputFrame.Width = 16385d;
        Throws<InvalidDataException>(
            () => ArtWorkspaceValidator.Validate(oversizedOutputDocument),
            "美术工作区输出框任一边超过 16384 时必须被拒绝");

        var regionDocument = ArtWorkspaceDocumentFactory.Create();
        var regionAsset = CreateManagedAsset(
            "Assets/ArtWorkspace/References/region.png",
            pixelWidth: 64,
            pixelHeight: 32);
        var regionImage = new ArtWorkspaceImageItem
        {
            AssetId = regionAsset.Id,
            Name = "区域参考",
            Width = 320d,
            Height = 160d
        };
        regionDocument.Assets.Add(regionAsset);
        regionDocument.Images.Add(regionImage);
        regionDocument.SelectedImageId = regionImage.Id;
        regionDocument.SelectedImageRegion = new ArtWorkspaceImageRegionSelection
        {
            ImageId = regionImage.Id,
            X = 0.25d,
            Y = 0.125d,
            Width = 0.5d,
            Height = 0.75d
        };
        var regionRoundTrip = ArtWorkspaceJson.Deserialize(ArtWorkspaceJson.Serialize(regionDocument));
        Near(0.25d, regionRoundTrip.SelectedImageRegion!.X,
            "图片本地归一化框选区域应完成 JSON 往返");
        var escapedRegionDocument = ArtWorkspaceJson.Deserialize(ArtWorkspaceJson.Serialize(regionDocument));
        escapedRegionDocument.SelectedImageRegion!.X = 0.8d;
        escapedRegionDocument.SelectedImageRegion.Width = 0.4d;
        Throws<InvalidDataException>(
            () => ArtWorkspaceValidator.Validate(escapedRegionDocument),
            "图片框选区域不得越出图片本地边界");
        var mismatchedRegionDocument = ArtWorkspaceJson.Deserialize(ArtWorkspaceJson.Serialize(regionDocument));
        mismatchedRegionDocument.SelectedImageRegion!.ImageId = Guid.NewGuid().ToString("N");
        Throws<InvalidDataException>(
            () => ArtWorkspaceValidator.Validate(mismatchedRegionDocument),
            "框选区域必须绑定当前所选图片");

        var maximumConstraintInput = new ArtWorkspaceGenerationConstraintInput
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Instruction = "验证美术工作区最大输出画幅。",
            OutputFrameName = "最大输出框",
            OutputFrameWidth = 16384d,
            OutputFrameHeight = 16384d,
            OutputPixelWidth = 16384,
            OutputPixelHeight = 16384,
            OutputBackgroundHex = "#20242C",
            OutputRelativeDirectory = "Generated/ArtWorkspace"
        };
        _ = ArtWorkspaceGenerationConstraintPackageBuilder.Build(maximumConstraintInput);
        Throws<ArgumentOutOfRangeException>(
            () => ArtWorkspaceGenerationConstraintPackageBuilder.Build(
                maximumConstraintInput with { OutputFrameWidth = 16385d }),
            "美术工作区生成约束不得接受超过 16384 的显示画幅");

        using (var console = new WorkbenchConsoleService(workspacePaths))
        using (var capacityViewModel = new ArtWorkspaceViewModel(
                   new JsonArtWorkspaceRepository(workspacePaths),
                   new StubImagePicker(),
                   console))
        {
            True(capacityViewModel.Activate(capacityProject.Paths.RootDirectory),
                "对话容量测试应先激活正式项目");
            capacityViewModel.ConfigureGenerationGateway(new StubGenerationGateway());
            for (var index = 0; index < ArtWorkspaceValidator.MaximumMessages - 3; index++)
            {
                capacityViewModel.Conversation.Add(CreateConversationMessage(index, "容量测试"));
            }

            capacityViewModel.InstructionDraft = "还能占用最后两个消息槽的指令";
            True(capacityViewModel.SubmitInstructionCommand.CanExecute(null),
                "恰好剩余两个对话槽时创作指令仍应可提交");
            capacityViewModel.Conversation.Add(CreateConversationMessage(
                ArtWorkspaceValidator.MaximumMessages - 2,
                "占用倒数第二个消息槽"));
            Equal(ArtWorkspaceValidator.MaximumMessages - 1, capacityViewModel.Conversation.Count,
                "对话容量边界测试应只剩一个空槽");
            False(capacityViewModel.SubmitInstructionCommand.CanExecute(null),
                "剩余不足两个消息槽时不得提交会一次追加两条记录的创作指令");
            var countBeforeRejectedSubmit = capacityViewModel.Conversation.Count;
            capacityViewModel.SubmitInstructionCommand.Execute(null);
            Equal(countBeforeRejectedSubmit, capacityViewModel.Conversation.Count,
                "容量不足时执行命令不得部分追加对话记录");
        }

        using var sizeConsole = new WorkbenchConsoleService(workspacePaths);
        using var sizeViewModel = new ArtWorkspaceViewModel(
            new JsonArtWorkspaceRepository(workspacePaths),
            new StubImagePicker(),
            sizeConsole);
        True(sizeViewModel.Activate(sizeProject.Paths.RootDirectory),
            "序列化大小测试应先激活正式项目");
        sizeViewModel.ConfigureGenerationGateway(new StubGenerationGateway());
        var largeMessage = new string('界', 16000);
        for (var index = 0; index < 350; index++)
        {
            sizeViewModel.Conversation.Add(CreateConversationMessage(index, largeMessage));
        }

        sizeViewModel.InstructionDraft = "不得令工作区 JSON 超过 16 MiB";
        True(sizeViewModel.SubmitInstructionCommand.CanExecute(null),
            "JSON 大小预检前，对话数量与指令长度仍在允许范围内");
        var oversizedCountBeforeSubmit = sizeViewModel.Conversation.Count;
        sizeViewModel.SubmitInstructionCommand.Execute(null);
        Equal(oversizedCountBeforeSubmit, sizeViewModel.Conversation.Count,
            "候选 JSON 超过 16 MiB 时不得追加任何对话记录");
        True(sizeViewModel.StatusText.Contains("16 MiB", StringComparison.Ordinal),
            "JSON 大小阻断应向界面提供明确的 16 MiB 诊断");
    }

    private static void AsyncImportAndUnusedAssetMetadataCleanupStayStable(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace project,
        string fixturePath)
    {
        using var console = new WorkbenchConsoleService(workspacePaths);
        using var viewModel = new ArtWorkspaceViewModel(repository, new StubImagePicker(), console);
        True(viewModel.Activate(project.Paths.RootDirectory), "异步导入测试项目应可激活");
        var importTask = viewModel.AddImagesAtAsync([fixturePath], 96d, 128d);
        var added = AwaitWithDispatcher(importTask, TimeSpan.FromSeconds(10));
        Equal(1, added, "异步导入应加入一张小型 PNG");
        False(viewModel.IsImporting, "AddImagesAtAsync 完成后必须复位 IsImporting");
        True(viewModel.ImportImagesCommand.CanExecute(null),
            "异步导入结束后导入命令应恢复可用");
        True(viewModel.SaveNow(), "异步导入完成后工作区应可立即保存");

        var importedImage = viewModel.Images.Single();
        var managedAssetPath = importedImage.AbsolutePath;
        True(File.Exists(managedAssetPath), "异步导入应真实落盘受管参考文件");
        var afterImport = ArtWorkspaceJson.Deserialize(File.ReadAllText(project.Paths.ArtWorkspacePath));
        Equal(1, afterImport.Images.Count, "异步导入保存后 JSON 应包含一个画布对象");
        Equal(1, afterImport.Assets.Count, "异步导入保存后 JSON 应包含一个受管素材元数据");

        viewModel.SelectImage(importedImage);
        True(viewModel.RemoveSelectedImageCommand.CanExecute(null), "选中图片后删除命令应可执行");
        viewModel.RemoveSelectedImageCommand.Execute(null);
        Equal(0, viewModel.Images.Count, "删除最后一个图片引用后画布应为空");
        True(viewModel.SaveNow(), "删除最后一个图片引用后应可保存工作区");
        var afterRemoval = ArtWorkspaceJson.Deserialize(File.ReadAllText(project.Paths.ArtWorkspacePath));
        Equal(0, afterRemoval.Images.Count, "保存后 JSON 不得保留已删除图片对象");
        Equal(0, afterRemoval.Assets.Count,
            "最后一个图片引用删除后 JSON 不得保留孤立 asset 元数据");
        True(File.Exists(managedAssetPath),
            "清理孤立 asset 元数据时应保留实体参考文件供审计与再次复用");

        const string cancelledImportSaveMarker = "取消导入后仍应自动保存";
        viewModel.OutputFrameName = cancelledImportSaveMarker;
        True(viewModel.PersistenceText.StartsWith("待保存", StringComparison.Ordinal),
            "启动异步导入前应存在可观察的待保存修改");
        var cancellationTask = viewModel.AddImagesAtAsync(
            Enumerable.Repeat(fixturePath, 64),
            160d,
            192d);
        True(viewModel.IsImporting, "取消回归必须在异步导入仍处于活动状态时触发");
        True(viewModel.CancelImportCommand.CanExecute(null), "异步导入期间取消命令应可执行");
        viewModel.CancelImportCommand.Execute(null);
        _ = AwaitWithDispatcher(cancellationTask, TimeSpan.FromSeconds(10));
        False(viewModel.IsImporting, "取消导入完成后必须退出导入状态");
        WaitUntilWithDispatcher(
            () => StoredOutputFrameNameEquals(project.Paths.ArtWorkspacePath, cancelledImportSaveMarker),
            TimeSpan.FromSeconds(5),
            "异步导入取消后，导入前已有的待保存修改应由防抖保存最终落盘");

        const string failedImportSaveMarker = "全失败导入后仍应自动保存";
        viewModel.OutputFrameName = failedImportSaveMarker;
        True(viewModel.PersistenceText.StartsWith("待保存", StringComparison.Ordinal),
            "全失败异步导入开始前应存在待保存修改");
        var missingPaths = new[]
        {
            Path.Combine(Path.GetDirectoryName(fixturePath)!, "missing-a.png"),
            Path.Combine(Path.GetDirectoryName(fixturePath)!, "missing-b.png")
        };
        var failedImportCount = AwaitWithDispatcher(
            viewModel.AddImagesAtAsync(missingPaths, 220d, 260d),
            TimeSpan.FromSeconds(10));
        Equal(0, failedImportCount, "所有源图片无效时异步导入不得创建画布对象");
        WaitUntilWithDispatcher(
            () => StoredOutputFrameNameEquals(project.Paths.ArtWorkspacePath, failedImportSaveMarker),
            TimeSpan.FromSeconds(5),
            "异步导入全部失败后，导入前已有的待保存修改仍应最终落盘");
    }

    private static void ExtremeAspectRatioLayerSelectionAndRotatedBoundsStayStable(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace project,
        string extremeTallFixture,
        string extremeWideFixture)
    {
        using var console = new WorkbenchConsoleService(workspacePaths);
        using var viewModel = new ArtWorkspaceViewModel(repository, new StubImagePicker(), console);
        True(viewModel.Activate(project.Paths.RootDirectory), "极端长宽比测试项目应可激活");
        Equal(2, viewModel.AddImagesAt([extremeTallFixture, extremeWideFixture], 200d, 260d),
            "合同边界内的两张极端长宽比 PNG 均应可导入");

        var tall = viewModel.Images.Single(image => image.PixelWidth == 8 && image.PixelHeight == 8192);
        var wide = viewModel.Images.Single(image => image.PixelWidth == 8192 && image.PixelHeight == 8);
        AssertImageDisplayDimensionsWithinContract(tall, "极端竖图");
        AssertImageDisplayDimensionsWithinContract(wide, "极端横图");
        True(viewModel.SaveNow(),
            "极端长宽比图片导入后生成的画布宽高必须通过工作区序列化合同");

        viewModel.SelectImage(tall);
        tall.IsVisible = false;
        viewModel.SelectImage(null);
        False(tall.IsVisible, "图层列表回归开始前图片应处于隐藏状态");
        False(viewModel.HasSelectedImage, "隐藏图片回归开始前不应保留画布选中对象");

        var propertiesView = new ArtWorkspacePropertiesView
        {
            DataContext = viewModel
        };
        var propertiesWindow = new Window
        {
            Width = 420d,
            Height = 900d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = propertiesView
        };
        try
        {
            propertiesWindow.Show();
            propertiesWindow.UpdateLayout();
            PumpDispatcher();
            var layerList = propertiesView.FindName("ArtWorkspaceImageLayerList") as ListBox
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区属性页缺少图片图层列表。");
            Equal(2, layerList.Items.Count,
                "属性图层列表必须保留隐藏图片，不能像画布可见项一样将其移除");
            True(layerList.Items.Contains(tall), "属性图层列表应包含已隐藏图片");

            layerList.SelectedItem = tall;
            PumpDispatcher();
            True(ReferenceEquals(tall, viewModel.SelectedImage),
                "在属性图层列表选择隐藏图片后应重新建立工作区选择状态");

            layerList.ScrollIntoView(tall);
            layerList.UpdateLayout();
            PumpDispatcher();
            var container = layerList.ItemContainerGenerator.ContainerFromItem(tall) as ListBoxItem
                ?? throw new ArtWorkspaceSmokeTestException("属性图层列表未生成隐藏图片的可交互项。");
            var visibilityToggle = FindVisualDescendant<CheckBox>(container)
                ?? throw new ArtWorkspaceSmokeTestException("属性图层项缺少显示/隐藏复选框。");
            visibilityToggle.IsChecked = true;
            PumpDispatcher();
            True(tall.IsVisible,
                "隐藏图片应可通过属性图层列表的显示开关恢复到画布");
        }
        finally
        {
            propertiesWindow.Close();
        }

        wide.IsVisible = false;
        tall.X = 100d;
        tall.Y = 100d;
        tall.Width = 100d;
        tall.Height = 40d;
        tall.RotationDegrees = 45d;
        tall.IsVisible = true;
        viewModel.SetOutputFrame(140d, 110d, 16d, 16d, commit: false);

        var corners = GetRotatedCorners(tall).ToArray();
        var bounds = viewModel.GetContentBounds();
        foreach (var corner in corners)
        {
            True(corner.X >= bounds.X - 0.0001d &&
                 corner.X <= bounds.X + bounds.Width + 0.0001d &&
                 corner.Y >= bounds.Y - 0.0001d &&
                 corner.Y <= bounds.Y + bounds.Height + 0.0001d,
                $"聚焦全部边界必须包含旋转图片四角 ({corner.X:0.###}, {corner.Y:0.###})");
        }

        var expectedMinimumX = corners.Min(point => point.X);
        var expectedMinimumY = corners.Min(point => point.Y);
        var expectedMaximumX = corners.Max(point => point.X);
        var expectedMaximumY = corners.Max(point => point.Y);
        Near(expectedMinimumX, bounds.X, "旋转图片内容边界 X");
        Near(expectedMinimumY, bounds.Y, "旋转图片内容边界 Y");
        Near(expectedMaximumX - expectedMinimumX, bounds.Width, "旋转图片内容边界宽度");
        Near(expectedMaximumY - expectedMinimumY, bounds.Height, "旋转图片内容边界高度");
    }

    private static void ImportPersistenceAndProjectIsolationStayStable(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace projectA,
        ProjectWorkspace projectB,
        string fixtureA,
        string fixtureB)
    {
        const string projectAInstruction = "创建一个与两张参考图构图一致的轴对齐游戏资产。";
        const string projectBInstruction = "项目 B 的独立创作意图。";
        string firstImageId;
        string firstAssetId;

        using (var console = new WorkbenchConsoleService(workspacePaths))
        using (var viewModel = new ArtWorkspaceViewModel(repository, new StubImagePicker(), console))
        {
            True(viewModel.Activate(projectA.Paths.RootDirectory), "项目 A 美术工作区应可激活");
            viewModel.ConfigureGenerationGateway(new StubGenerationGateway());
            Equal(2, viewModel.AddImagesAt([fixtureA, fixtureB], 120d, 240d),
                "两张有效参考图应同时加入画布");
            Equal(2, viewModel.Images.Count, "项目 A 画布图片数量");

            var first = viewModel.Images[0];
            var second = viewModel.Images[1];
            firstImageId = first.Id;
            firstAssetId = first.AssetId;
            Equal(8, first.PixelWidth, "第一张参考图像素宽度");
            Equal(4, first.PixelHeight, "第一张参考图像素高度");
            Near(120d, first.X, "第一张参考图 X 坐标");
            Near(240d, first.Y, "第一张参考图 Y 坐标");
            Equal(5, second.PixelWidth, "第二张参考图像素宽度");
            Equal(9, second.PixelHeight, "第二张参考图像素高度");
            Near(148d, second.X, "批量导入的第二张图片应错开放置");
            Near(268d, second.Y, "批量导入的第二张图片应错开放置");
            True(File.Exists(first.AbsolutePath) && File.Exists(second.AbsolutePath),
                "导入后应真实创建项目受管参考文件");
            True(IsWithin(projectA.Paths.RootDirectory, first.AbsolutePath) &&
                 IsWithin(projectA.Paths.RootDirectory, second.AbsolutePath),
                "项目 A 的受管参考文件不得落到项目外");
            Equal(2, CountReferenceFiles(projectA), "首次导入应产生两个实体参考文件");

            True(viewModel.SaveNow(), "项目 A 首次工作区保存应成功");
            var documentPath = repository.ResolveDocumentPath(projectA.Paths.RootDirectory);
            var serialized = File.ReadAllText(documentPath);
            var stored = ArtWorkspaceJson.Deserialize(serialized);
            Equal(2, stored.Assets.Count, "工作区 JSON 应记录两个受管素材");
            Equal(2, stored.Images.Count, "工作区 JSON 应记录两个画布对象");
            True(stored.Assets.All(asset => !Path.IsPathRooted(asset.RelativePath)),
                "工作区 JSON 只允许保存相对素材路径");
            False(serialized.Contains(projectA.Paths.RootDirectory, StringComparison.OrdinalIgnoreCase),
                "工作区 JSON 不得固化项目绝对路径");
            False(serialized.Contains(Path.GetDirectoryName(fixtureA)!, StringComparison.OrdinalIgnoreCase),
                "工作区 JSON 不得固化导入源目录");

            Equal(1, viewModel.AddImagesAt([fixtureA], 360d, 460d),
                "重复导入同图仍应允许创建新的画布对象");
            Equal(3, viewModel.Images.Count, "重复导入后应有三个独立画布对象");
            Equal(firstAssetId, viewModel.Images[2].AssetId,
                "相同 SHA-256 的图片应复用同一个受管素材 ID");
            Equal(2, CountReferenceFiles(projectA),
                "相同哈希重复导入不得复制新的实体参考文件");

            viewModel.MoveImage(first, 444.5d, 555.25d, commit: true);
            viewModel.SelectImage(first);
            first.RotationDegrees = 35d;
            True(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
                "可见且文件存在的所选图片应允许启用图像框选工具");
            viewModel.ImageRegionSelectToolCommand.Execute(null);
            True(viewModel.IsImageRegionSelectToolActive,
                "图像框选工具命令应切换工作区工具模式");
            True(viewModel.SetSelectedImageRegionNormalized(
                    0.2d,
                    0.25d,
                    0.5d,
                    0.4d,
                    commit: true),
                "所选图片应接受图片本地归一化框选区域");
            True(viewModel.HasSelectedImageRegion && first.HasSelectedRegion,
                "父工作区和图片对象应同时暴露当前框选状态");
            Near(first.Width * 0.2d, first.SelectedRegionX,
                "图片模板框选 X 应随当前显示宽度解释");
            True(viewModel.SelectedImageReferenceText.Contains(first.Name, StringComparison.Ordinal) &&
                 viewModel.SelectedImageReferenceText.Contains("局部区域", StringComparison.Ordinal),
                "对话输入区引用摘要应显示图片名称和局部语义，不暴露坐标");
            viewModel.SetOutputFrame(700d, 820d, 512d, 768d, commit: true);
            viewModel.OutputFrameName = "角色立绘输出";
            viewModel.OutputRelativeDirectory = "Assets/Generated/ArtWorkspace/Characters";
            ArtWorkspaceCreationRequestedEventArgs? capturedRequest = null;
            viewModel.CreationRequested += (_, request) => capturedRequest = request;
            viewModel.InstructionDraft = projectAInstruction;
            True(viewModel.SubmitInstructionCommand.CanExecute(null),
                "非空创作指令应可提交到本地工作区");
            viewModel.SubmitInstructionCommand.Execute(null);
            True(capturedRequest is not null, "提交指令应发布可供后续生成适配器消费的冻结请求");
            Equal(projectAInstruction, capturedRequest!.Instruction, "冻结请求应保留原始创作指令");
            Equal(3, capturedRequest.VisibleImageIds.Count,
                "冻结请求应包含当前三个可见参考图对象");
            True(viewModel.Conversation.Any(message =>
                    message.IsUser && string.Equals(message.Text, projectAInstruction, StringComparison.Ordinal)),
                "创作指令应先作为本地对话记录持久化");
            True(viewModel.Conversation.Any(message =>
                    message.IsUser &&
                    message.HasReferenceSummary &&
                    message.ReferenceSummary.Contains(first.Name, StringComparison.Ordinal) &&
                    message.ReferenceSummary.Contains("局部区域", StringComparison.Ordinal)),
                "用户对话记录应显示坐标无关的冻结图片/区域引用摘要");
            True(viewModel.Conversation.Any(message =>
                    message.IsAgent && message.State == "failed"),
                "测试网关失败时应把原因写入可见 Agent 对话，而不是停留在等待适配器状态");
            True(viewModel.SaveNow(), "修改画布、输出框与对话后应可立即保存");

            True(viewModel.Activate(projectB.Paths.RootDirectory), "从项目 A 切换到项目 B 应成功");
            Equal(0, viewModel.Images.Count, "新项目 B 不得继承项目 A 的画布图片");
            False(viewModel.Conversation.Any(message => message.Text.Contains(projectAInstruction, StringComparison.Ordinal)),
                "项目 B 不得读取项目 A 的创作对话");
            Equal(1, viewModel.AddImagesAt([fixtureB], 32d, 64d),
                "项目 B 应可建立自己的受管参考图");
            viewModel.InstructionDraft = projectBInstruction;
            viewModel.SubmitInstructionCommand.Execute(null);
            True(viewModel.SaveNow(), "项目 B 工作区保存应成功");

            True(viewModel.Activate(projectA.Paths.RootDirectory), "切回项目 A 应成功");
            Equal(3, viewModel.Images.Count, "切回项目 A 应恢复三个画布对象");
            var restoredFirst = viewModel.Images.Single(image => image.Id == firstImageId);
            Near(444.5d, restoredFirst.X, "项目切换后应恢复图片 X 坐标");
            Near(555.25d, restoredFirst.Y, "项目切换后应恢复图片 Y 坐标");
            True(viewModel.HasSelectedImageRegion,
                "项目切换后应恢复当前图片局部框选状态");
            Near(0.2d, viewModel.SelectedImageRegion!.X,
                "项目切换后应恢复归一化框选 X");
            Near(700d, viewModel.OutputFrameX, "项目切换后应恢复输出框 X 坐标");
            Near(820d, viewModel.OutputFrameY, "项目切换后应恢复输出框 Y 坐标");
            Near(512d, viewModel.OutputFrameWidth, "项目切换后应恢复输出框宽度");
            Near(768d, viewModel.OutputFrameHeight, "项目切换后应恢复输出框高度");
            Equal("角色立绘输出", viewModel.OutputFrameName, "项目切换后应恢复输出框名称");
            Equal("Assets/Generated/ArtWorkspace/Characters", viewModel.OutputRelativeDirectory,
                "项目切换后应恢复相对输出目录");
            True(viewModel.Conversation.Any(message => message.Text == projectAInstruction),
                "项目 A 创作对话应完成持久化往返");
            False(viewModel.Conversation.Any(message => message.Text == projectBInstruction),
                "切回项目 A 后不得混入项目 B 对话");
        }

        using var reloadConsole = new WorkbenchConsoleService(workspacePaths);
        using var reloaded = new ArtWorkspaceViewModel(repository, new StubImagePicker(), reloadConsole);
        True(reloaded.Activate(projectA.Paths.RootDirectory), "新建 ViewModel 应可重载项目 A");
        Equal(3, reloaded.Images.Count, "新建 ViewModel 应恢复全部画布对象");
        var reloadedFirst = reloaded.Images.Single(image => image.Id == firstImageId);
        Equal(firstAssetId, reloadedFirst.AssetId, "新建 ViewModel 应保留素材引用 ID");
        Near(444.5d, reloadedFirst.X, "新建 ViewModel 应恢复图片位置");
        True(reloaded.HasSelectedImageRegion,
            "新建 ViewModel 应从项目 JSON 恢复图片框选区域");
        Near(0.5d, reloaded.SelectedImageRegion!.Width,
            "重载后归一化框选宽度");
        True(reloaded.Conversation.Any(message => message.Text == projectAInstruction),
            "新建 ViewModel 应恢复创作对话");
    }

    private static void AgentGatewayGenerationFlowStaysProjectSafe(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository,
        string referenceFixture)
    {
        var projectService = new ProjectWorkspaceService();
        var projectA = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceAgentA-{Guid.NewGuid():N}",
            "ui");
        var projectB = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceAgentB-{Guid.NewGuid():N}",
            "ui");
        using var console = new WorkbenchConsoleService(workspacePaths);
        using var viewModel = new ArtWorkspaceViewModel(repository, new StubImagePicker(), console);
        True(viewModel.Activate(projectA.Paths.RootDirectory), "Agent 生成测试应激活项目 A");
        viewModel.InstructionDraft = "创建一枚轴对齐的蓝色游戏徽章。";
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "尚未配置生成网关时发送并生成必须禁用");

        var gateway = new StubGenerationGateway(isConnected: false);
        viewModel.ConfigureGenerationGateway(gateway);
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "网关未连接时发送并生成必须禁用");
        gateway.SetState(isConnected: true, isBusy: false, "Codex App Server 会话已就绪");
        True(viewModel.AgentConnectionStatusText.Contains("Agent 已接入可用", StringComparison.Ordinal),
            "网关 ready 后属性页应显示 Agent 已接入可用");
        gateway.SetState(isConnected: true, isBusy: true, "Agent 正在执行其它任务");
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "全局 Agent 忙碌时发送并生成必须禁用");
        gateway.SetState(isConnected: true, isBusy: false, "Agent 可用");

        Equal(1, viewModel.AddImagesAt([referenceFixture], 120d, 180d),
            "Agent 生成测试应先加入一张可见参考图");
        var selectedReference = viewModel.Images.Single();
        True(viewModel.SetSelectedImageRegionNormalized(
                0.125d,
                0.25d,
                0.5d,
                0.5d,
                commit: true),
            "Agent 生成测试应冻结当前图片的局部框选");
        viewModel.SetOutputFrame(640d, 720d, 20000d, 30000d, commit: true);
        Near(16384d, viewModel.OutputFrameWidth,
            "属性输入超过生成合同上限时输出框宽度应收束到 16384");
        Near(16384d, viewModel.OutputFrameHeight,
            "属性输入超过生成合同上限时输出框高度应收束到 16384");
        viewModel.SetOutputFrame(640d, 720d, 512d, 384d, commit: true);
        viewModel.OutputFrameName = "徽章输出";
        var finalDirectory = Path.Combine(
            projectA.Paths.RootDirectory,
            "Assets",
            "Generated",
            "ArtWorkspace");
        Directory.CreateDirectory(finalDirectory);
        var finalPath = Path.Combine(finalDirectory, "badge-final.png");
        SavePngFixture(finalPath, 12, 9, 0x29, 0x78, 0xD1);

        ArtWorkspaceGenerationRequest? capturedRequest = null;
        gateway.GenerateHandler = (request, progress, _) =>
        {
            capturedRequest = request;
            progress?.Report(new ArtWorkspaceGenerationProgress
            {
                State = "generating",
                Message = "正在生成徽章",
                AgentDescription = "正在根据冻结参考图生成徽章。",
                Percentage = 55d
            });
            return Task.FromResult(new ArtWorkspaceGenerationResult
            {
                IsSuccess = true,
                AgentDescription = "已生成轴对齐的蓝色游戏徽章。",
                OutputPaths = [finalPath],
                BatchId = Guid.NewGuid().ToString("N"),
                TaskId = Guid.NewGuid().ToString("N")
            });
        };

        viewModel.InstructionDraft = "创建一枚轴对齐的蓝色游戏徽章。";
        True(viewModel.SubmitInstructionCommand.CanExecute(null),
            "正式项目和可用 Agent 下发送并生成应可执行");
        viewModel.SubmitInstructionCommand.Execute(null);
        WaitUntilWithDispatcher(
            () => !viewModel.IsGenerating &&
                  viewModel.Conversation.Any(message => message.IsAgent && message.State == "completed"),
            TimeSpan.FromSeconds(10),
            "Agent 生成完成后应更新对话并退出忙碌状态");

        True(capturedRequest is not null, "生成网关应收到冻结请求");
        Equal(projectA.Paths.RootDirectory, capturedRequest!.ProjectRoot,
            "冻结请求应绑定项目 A 根目录");
        Equal(projectA.Paths.AssetDirectory, capturedRequest.AssetRoot,
            "冻结请求应绑定项目 A Assets 根目录");
        Equal(512, capturedRequest.OutputFrame.PixelWidth, "冻结请求输出像素宽度");
        Equal(384, capturedRequest.OutputFrame.PixelHeight, "冻结请求输出像素高度");
        Equal(1, capturedRequest.VisibleReferences.Length, "冻结请求应只包含当前可见参考图");
        True(capturedRequest.InstructionReference is not null,
            "冻结请求应独立携带当前所选图片指代");
        Equal(selectedReference.Id, capturedRequest.InstructionReference!.ImageId,
            "冻结请求所选图片 ID");
        Near(0.125d, capturedRequest.InstructionReference.Region!.X,
            "冻结请求局部区域 X");
        Near(0.5d, capturedRequest.InstructionReference.Region.Height,
            "冻结请求局部区域高度");
        True(Path.IsPathRooted(capturedRequest.VisibleReferences[0].AbsolutePath),
            "冻结参考图必须使用绝对受管路径");
        True(File.Exists(finalPath), "导入画布后必须保留 Generated 正式原件");
        Equal(2, viewModel.Images.Count, "生成成功后画布应增加一张受管预览副本");
        var generated = viewModel.Images.Single(image => image.Name == "徽章输出");
        False(string.Equals(generated.AbsolutePath, finalPath, StringComparison.OrdinalIgnoreCase),
            "画布应引用受管预览副本而不是移动 Generated 原件");
        True(File.Exists(generated.AbsolutePath), "画布受管预览副本应真实存在");
        Near(640d, generated.X, "生成预览应精确放入输出框 X");
        Near(720d, generated.Y, "生成预览应精确放入输出框 Y");
        Near(512d, generated.Width, "生成预览应匹配输出框宽度");
        Near(384d, generated.Height, "生成预览应匹配输出框高度");
        True(viewModel.Conversation.Any(message =>
                message.IsAgent && message.Text.Contains("蓝色游戏徽章", StringComparison.Ordinal)),
            "Agent 最终描述应保存到工作区对话");

        var outsidePath = Path.Combine(projectA.Paths.AssetDirectory, "Unrelated", "outside-final.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        SavePngFixture(outsidePath, 4, 4, 0xD8, 0x73, 0x2A);
        gateway.GenerateHandler = (_, _, _) => Task.FromResult(new ArtWorkspaceGenerationResult
        {
            IsSuccess = true,
            AgentDescription = "不应被接受的冻结输出目录外产物",
            OutputPaths = [outsidePath]
        });
        var imageCountBeforeOutsideResult = viewModel.Images.Count;
        viewModel.InstructionDraft = "尝试返回冻结输出目录外的图片。";
        viewModel.SubmitInstructionCommand.Execute(null);
        WaitUntilWithDispatcher(
            () => !viewModel.IsGenerating &&
                  viewModel.Conversation.Last().State == "failed",
            TimeSpan.FromSeconds(10),
            "冻结输出目录外产物应转换为可见失败消息");
        Equal(imageCountBeforeOutsideResult, viewModel.Images.Count,
            "同项目但不属于冻结输出目录的返回路径不得加入画布");

        var delayedCompletion = new TaskCompletionSource<ArtWorkspaceGenerationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.GenerateHandler = (_, _, _) => delayedCompletion.Task;
        viewModel.InstructionDraft = "生成一个会在项目切换后返回的图片。";
        viewModel.SubmitInstructionCommand.Execute(null);
        WaitUntilWithDispatcher(
            () => viewModel.IsGenerating,
            TimeSpan.FromSeconds(2),
            "迟到结果测试应进入生成状态");
        True(viewModel.Activate(projectB.Paths.RootDirectory),
            "生成期间应允许切换项目并取消当前请求");
        delayedCompletion.SetResult(new ArtWorkspaceGenerationResult
        {
            IsSuccess = true,
            AgentDescription = "项目 A 的迟到结果",
            OutputPaths = [finalPath]
        });
        WaitUntilWithDispatcher(
            () => !viewModel.IsGenerating,
            TimeSpan.FromSeconds(10),
            "迟到结果返回后应释放单飞状态");
        Equal(0, viewModel.Images.Count,
            "项目 A 的迟到结果不得污染已切换到项目 B 的画布");
    }

    private static void FailedSaveKeepsActiveGenerationAlive(
        WorkspacePaths workspacePaths,
        JsonArtWorkspaceRepository repository)
    {
        var projectService = new ProjectWorkspaceService();
        var projectA = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceSaveFailureA-{Guid.NewGuid():N}",
            "ui");
        var projectB = projectService.Create(
            workspacePaths.ProjectDirectory,
            $"ArtWorkspaceSaveFailureB-{Guid.NewGuid():N}",
            "ui");
        using var console = new WorkbenchConsoleService(workspacePaths);
        using var viewModel = new ArtWorkspaceViewModel(repository, new StubImagePicker(), console);
        True(viewModel.Activate(projectA.Paths.RootDirectory),
            "保存失败取消顺序测试应先激活项目 A");

        var completion = new TaskCompletionSource<ArtWorkspaceGenerationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = 0;
        var gateway = new StubGenerationGateway
        {
            GenerateHandler = (_, _, cancellationToken) =>
            {
                cancellationToken.Register(() => Interlocked.Exchange(ref cancellationObserved, 1));
                handlerStarted.TrySetResult(true);
                return completion.Task.WaitAsync(cancellationToken);
            }
        };
        viewModel.ConfigureGenerationGateway(gateway);
        viewModel.InstructionDraft = "生成期间测试未保存项目切换。";
        viewModel.SubmitInstructionCommand.Execute(null);
        WaitUntilWithDispatcher(
            () => handlerStarted.Task.IsCompleted && viewModel.IsGenerating,
            TimeSpan.FromSeconds(2),
            "保存失败取消顺序测试应先进入 Agent 生成状态");

        viewModel.OutputFrameName = "尚未安全保存的输出框";
        var documentPath = repository.ResolveDocumentPath(projectA.Paths.RootDirectory);
        var heldDocumentPath = documentPath + $".held-{Guid.NewGuid():N}";
        File.Move(documentPath, heldDocumentPath);
        Directory.CreateDirectory(documentPath);
        try
        {
            False(viewModel.Activate(projectB.Paths.RootDirectory),
                "当前工作区保存失败时必须拒绝项目切换");
            Equal(0, Volatile.Read(ref cancellationObserved),
                "保存失败不得提前取消仍属于当前项目的 Agent 生成");
            True(viewModel.IsGenerating,
                "保存失败后当前项目的 Agent 生成应继续等待原结果");
            True(viewModel.ProjectContextText.Contains(projectA.Paths.RootDirectory, StringComparison.OrdinalIgnoreCase),
                "保存失败后美术工作区必须仍绑定项目 A");
        }
        finally
        {
            Directory.Delete(documentPath);
            File.Move(heldDocumentPath, documentPath);
        }

        completion.SetResult(new ArtWorkspaceGenerationResult
        {
            IsSuccess = false,
            ErrorMessage = "测试结束"
        });
        WaitUntilWithDispatcher(
            () => !viewModel.IsGenerating,
            TimeSpan.FromSeconds(5),
            "恢复保存路径并结束测试结果后应释放生成状态");
        True(viewModel.SaveNow(),
            "保存路径恢复后当前项目工作区应可继续正常保存");
    }

    private static void BackupRecoveryAndPathBoundariesFailClosed(
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace projectA,
        ProjectWorkspace projectB,
        string testRoot)
    {
        var projectADocument = repository.LoadOrCreate(projectA.Paths.RootDirectory).Document;
        projectADocument.OutputFrame.Name = "备份可恢复状态";
        repository.Save(projectA.Paths.RootDirectory, projectADocument);
        projectADocument.OutputFrame.Name = "较新的主文件状态";
        repository.Save(projectA.Paths.RootDirectory, projectADocument);
        True(File.Exists(projectA.Paths.ArtWorkspaceBackupPath),
            "连续有效保存应产生工作区备份文件");
        File.WriteAllText(projectA.Paths.ArtWorkspacePath, "{ invalid primary json");
        var recovered = repository.LoadOrCreate(projectA.Paths.RootDirectory);
        Equal("backup", recovered.RecoverySource, "主文件损坏时应从有效备份恢复");
        Equal("备份可恢复状态", recovered.Document.OutputFrame.Name,
            "备份恢复应返回上一次有效保存状态");
        var validBackupJson = File.ReadAllText(projectA.Paths.ArtWorkspaceBackupPath);
        AssertSemanticPrimaryFallsBackToBackup(
            repository,
            projectA,
            validBackupJson,
            root => root["viewport"] = null,
            "viewport:null");
        AssertSemanticPrimaryFallsBackToBackup(
            repository,
            projectA,
            validBackupJson,
            root => root["assets"]!.AsArray()[0]!.AsObject()["sha256"] = null,
            "sha256:null");
        AssertSemanticPrimaryFallsBackToBackup(
            repository,
            projectA,
            validBackupJson,
            root => root["assets"]!.AsArray()[0]!.AsObject()["relativePath"] =
                "Assets/ArtWorkspace/References/contains\0nul.png",
            "含 NUL 的素材路径");
        File.WriteAllText(projectA.Paths.ArtWorkspacePath, validBackupJson);

        var projectBDocument = repository.LoadOrCreate(projectB.Paths.RootDirectory).Document;
        projectBDocument.OutputFrame.Name = "项目 B 备份一";
        repository.Save(projectB.Paths.RootDirectory, projectBDocument);
        projectBDocument.OutputFrame.Name = "项目 B 备份二";
        repository.Save(projectB.Paths.RootDirectory, projectBDocument);
        const string invalidPrimary = "{ deliberately-corrupt-primary";
        const string invalidBackup = "deliberately-corrupt-backup";
        File.WriteAllText(projectB.Paths.ArtWorkspacePath, invalidPrimary);
        File.WriteAllText(projectB.Paths.ArtWorkspaceBackupPath, invalidBackup);
        var safeDefault = repository.LoadOrCreate(projectB.Paths.RootDirectory);
        Equal("safe-default", safeDefault.RecoverySource,
            "主文件与备份均损坏时应加载内存安全默认状态");
        Equal(0, safeDefault.Document.Images.Count, "安全默认状态不得携带损坏工作区的图片对象");
        Equal(invalidPrimary, File.ReadAllText(projectB.Paths.ArtWorkspacePath),
            "安全默认加载不得覆盖损坏主文件证据");
        Equal(invalidBackup, File.ReadAllText(projectB.Paths.ArtWorkspaceBackupPath),
            "安全默认加载不得覆盖损坏备份证据");
        using (var safeDefaultConsole = new WorkbenchConsoleService(
                   new WorkspacePaths(Path.Combine(testRoot, "safe-default-console"))))
        {
            using var safeDefaultViewModel = new ArtWorkspaceViewModel(
                repository,
                new StubImagePicker(),
                safeDefaultConsole);
            True(safeDefaultViewModel.Activate(projectB.Paths.RootDirectory),
                "ViewModel 应能以内存安全默认状态打开主备均损坏的工作区");
        }

        Equal(invalidPrimary, File.ReadAllText(projectB.Paths.ArtWorkspacePath),
            "仅打开并关闭安全默认工作区不得改写损坏主文件证据");
        Equal(invalidBackup, File.ReadAllText(projectB.Paths.ArtWorkspaceBackupPath),
            "仅打开并关闭安全默认工作区不得改写损坏备份证据");

        Throws<InvalidDataException>(
            () => repository.ResolveAssetPath(projectA.Paths.RootDirectory, "../outside.png"),
            "相对路径跳转不得离开项目受管根目录");
        var absoluteOutsidePath = Path.Combine(testRoot, "outside.png");
        File.WriteAllText(absoluteOutsidePath, "not an imported image");
        Throws<InvalidDataException>(
            () => repository.ResolveAssetPath(projectA.Paths.RootDirectory, absoluteOutsidePath),
            "绝对素材路径不得写入工作区合同");

        var invalidDocument = ArtWorkspaceDocumentFactory.Create();
        invalidDocument.Assets.Add(CreateManagedAsset("../outside.png"));
        Throws<InvalidDataException>(
            () => repository.Save(projectA.Paths.RootDirectory, invalidDocument),
            "保存工作区时应再次拒绝越界素材路径");

        AssertReparsePointIsRejected(repository, projectA, testRoot);
    }

    private static void DockDocumentUsesArtWorkspaceContexts(string root)
    {
        var paths = new WorkspacePaths(root);
        var settingsService = new UserSettingsService(paths);
        settingsService.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "ui"
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
            PumpDispatcher();
            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区停靠测试未找到 DockManager。");
            var uiZoomToolbar = window.FindName("UiZoomToolbar") as StackPanel
                ?? throw new ArtWorkspaceSmokeTestException("主窗口缺少顶部 UI 缩放状态区。");
            var bottomUiStatus = FindBottomUiStatusPanel(window);
            AssertUiDocumentVisibilityTrigger(uiZoomToolbar, "顶部 UI 缩放状态区");
            AssertUiDocumentVisibilityTrigger(bottomUiStatus, "底部 UI 编辑状态区");
            Equal(Visibility.Visible, uiZoomToolbar.Visibility,
                "默认 UI 文档活动时顶部 UI 状态区应可见");
            Equal(Visibility.Visible, bottomUiStatus.Visibility,
                "默认 UI 文档活动时底部 UI 状态区应可见");
            var document = FindDockContent(manager, MainWindow.ArtWorkspaceContentId) as LayoutDocument
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区必须作为 LayoutDocument 出现在中央文档区。");
            True(document.CanClose && document.CanFloat && document.CanMove,
                "美术工作区文档应支持关闭、浮动与拖动");
            var host = document.Content as ContentControl
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区文档必须使用稳定 ContentControl host。");
            var view = host.Content as ArtWorkspaceView
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区文档未承载 ArtWorkspaceView。");
            True(ReferenceEquals(shell.ArtWorkspace, view.DataContext),
                "美术工作区主视图应复用 Shell 持有的 ViewModel");

            document.IsSelected = true;
            document.IsActive = true;
            manager.ActiveContent = document.Content;
            PumpDispatcher();
            Equal(MainWindow.ArtWorkspaceContentId, manager.Layout.ActiveContent?.ContentId,
                "激活美术工作区后 AvalonDock 应更新活动文档上下文");
            Equal(Visibility.Collapsed, uiZoomToolbar.Visibility,
                "美术工作区活动时顶部 UI 状态区应折叠");
            Equal(Visibility.Collapsed, bottomUiStatus.Visibility,
                "美术工作区活动时底部 UI 状态区应折叠");

            var properties = FindDockContent(manager, MainWindow.PropertiesContentId) as LayoutAnchorable
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区停靠测试缺少属性面板。");
            var propertiesHost = properties.Content as ContentControl
                ?? throw new ArtWorkspaceSmokeTestException("属性面板必须使用稳定 ContentControl host。");
            var propertiesView = propertiesHost.Content as ArtWorkspacePropertiesView
                ?? throw new ArtWorkspaceSmokeTestException("激活美术工作区后属性面板未切换到专属属性视图。");
            True(ReferenceEquals(shell.ArtWorkspace, propertiesView.DataContext),
                "美术工作区属性视图应复用同一个 ViewModel");

            var tools = FindDockContent(manager, MainWindow.ToolsContentId) as LayoutAnchorable
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区停靠测试缺少工具面板。");
            var toolsHost = tools.Content as ContentControl
                ?? throw new ArtWorkspaceSmokeTestException("工具面板必须使用稳定 ContentControl host。");
            var toolPalette = toolsHost.Content as ArtWorkspaceToolPaletteView
                ?? throw new ArtWorkspaceSmokeTestException("激活美术工作区后工具面板未切换到专属工具栏。");
            True(ReferenceEquals(shell.ArtWorkspace, toolPalette.DataContext),
                "美术工作区工具栏应复用同一个 ViewModel");
            AssertUiCommandsDisabled(shell, "美术工作区活动时");
            True(shell.ActiveDocumentSaveCommand.CanExecute(null),
                "美术工作区活动时 Ctrl+S/文件保存入口应保存当前工作区");
            shell.ArtWorkspace.OutputFrameName = "标准保存入口回归";
            True(shell.ArtWorkspace.PersistenceText.StartsWith("待保存", StringComparison.Ordinal),
                "标准保存入口回归执行前应存在待保存修改");
            shell.ActiveDocumentSaveCommand.Execute(null);
            False(shell.ArtWorkspace.PersistenceText.StartsWith("待保存", StringComparison.Ordinal),
                "活动文档保存命令应立即保存美术工作区");

            foreach (var sharedPanelId in new[]
                     {
                         MainWindow.StyleLibraryContentId,
                         MainWindow.AssetGenerationContentId,
                         MainWindow.ProjectContentId,
                         MainWindow.CommandConsoleContentId
                     })
            {
                document.IsSelected = true;
                document.IsActive = true;
                manager.ActiveContent = document.Content;
                PumpDispatcher();
                var sharedPanel = FindDockContent(manager, sharedPanelId) as LayoutAnchorable
                    ?? throw new ArtWorkspaceSmokeTestException($"共享面板缺失：{sharedPanelId}。");
                sharedPanel.IsSelected = true;
                sharedPanel.IsActive = true;
                manager.ActiveContent = sharedPanel.Content;
                PumpDispatcher();
                True(toolsHost.Content is ArtWorkspaceToolPaletteView,
                    $"激活共享面板 {sharedPanelId} 时应保留美术工作区工具上下文");
                Equal(MainWindow.ArtWorkspaceContentId, shell.ActiveDocumentContentId,
                    $"激活共享面板 {sharedPanelId} 时不应丢失美术工作区活动文档身份");
                AssertUiCommandsDisabled(shell, $"美术工作区上方激活共享面板 {sharedPanelId} 时");
                True(shell.ActiveDocumentSaveCommand.CanExecute(null),
                    $"激活共享面板 {sharedPanelId} 时应保留美术工作区标准保存入口");
            }

            document.IsSelected = true;
            document.IsActive = true;
            manager.ActiveContent = document.Content;
            PumpDispatcher();

            document.Close();
            PumpDispatcher();
            True(FindDockContent(manager, MainWindow.ArtWorkspaceContentId) is null,
                "关闭美术工作区后布局中不应残留旧文档模型");
            shell.ShowPanelCommand.Execute(MainWindow.ArtWorkspaceContentId);
            PumpDispatcher();
            var restored = FindDockContent(manager, MainWindow.ArtWorkspaceContentId) as LayoutDocument
                ?? throw new ArtWorkspaceSmokeTestException("视图命令未重新创建美术工作区文档。");
            False(ReferenceEquals(document, restored),
                "关闭后恢复应创建新的美术工作区 LayoutDocument 模型");
            True(ReferenceEquals(host, restored.Content),
                "关闭后恢复应复用原有美术工作区 host，避免画布状态丢失");
            True(ReferenceEquals(view, host.Content),
                "关闭后恢复应复用原有 ArtWorkspaceView 实例");
            True(restored.IsSelected && restored.IsActive,
                "恢复的美术工作区文档应立即成为选中活动页");

            var blueprint = FindDockContent(manager, MainWindow.BlueprintContentId) as LayoutAnchorable
                ?? throw new ArtWorkspaceSmokeTestException("美术工作区命令门禁测试缺少蓝图窗口。");
            blueprint.IsSelected = true;
            blueprint.IsActive = true;
            manager.ActiveContent = blueprint.Content;
            PumpDispatcher();
            Equal(MainWindow.BlueprintContentId, shell.ActiveDocumentContentId,
                "蓝图窗口激活时应成为非 UI 工具上下文");
            True(toolsHost.Content is BlueprintToolPaletteView,
                "蓝图窗口激活时工具栏应切换到蓝图工具集");
            AssertUiCommandsDisabled(shell, "蓝图窗口激活时");
            False(shell.ActiveDocumentSaveCommand.CanExecute(null),
                "蓝图窗口当前没有文档保存合同，标准保存入口应禁用");

            foreach (var openDocument in manager.Layout.Descendents().OfType<LayoutDocument>().ToList())
            {
                openDocument.Close();
            }

            PumpDispatcher();
            Equal(0, manager.Layout.Descendents().OfType<LayoutDocument>().Count(),
                "关闭全部中央文档后布局中不得残留 LayoutDocument");
            var projectPanel = FindDockContent(manager, MainWindow.ProjectContentId) as LayoutAnchorable
                ?? throw new ArtWorkspaceSmokeTestException("无文档命令门禁测试缺少项目面板。");
            projectPanel.IsSelected = true;
            projectPanel.IsActive = true;
            manager.ActiveContent = projectPanel.Content;
            PumpDispatcher();
            False(shell.IsUiDocumentActive,
                "关闭全部 LayoutDocument 后不得继续报告页面 UI 文档处于活动状态");
            AssertUiCommandsDisabled(shell, "关闭全部 LayoutDocument 后");
            False(shell.ActiveDocumentSaveCommand.CanExecute(null),
                "关闭全部中央文档后标准保存入口应禁用");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static ArtWorkspaceManagedAsset CreateManagedAsset(
        string relativePath,
        int pixelWidth = 1,
        int pixelHeight = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        RelativePath = relativePath,
        OriginalFileName = "outside.png",
        MediaType = "image/png",
        Sha256 = new string('a', 64),
        SizeBytes = 1,
        PixelWidth = pixelWidth,
        PixelHeight = pixelHeight,
        ImportedAtUtc = DateTimeOffset.UtcNow
    };

    private static ArtWorkspaceConversationMessageViewModel CreateConversationMessage(
        int index,
        string text) =>
        new(new ArtWorkspaceConversationMessage
        {
            Id = $"capacity-{index:D4}-{Guid.NewGuid():N}",
            Role = ArtWorkspaceMessageRole.User,
            Text = text,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            State = "captured"
        });

    private static StackPanel FindBottomUiStatusPanel(MainWindow window)
    {
        var root = window.Content as Grid
            ?? throw new ArtWorkspaceSmokeTestException("MainWindow 根内容必须是 Grid。");
        var statusBorder = root.Children
            .OfType<Border>()
            .SingleOrDefault(border => Grid.GetRow(border) == 3)
            ?? throw new ArtWorkspaceSmokeTestException("主窗口缺少底部状态栏。");
        var dockPanel = statusBorder.Child as DockPanel
            ?? throw new ArtWorkspaceSmokeTestException("底部状态栏必须使用 DockPanel。");
        return dockPanel.Children
                   .OfType<StackPanel>()
                   .SingleOrDefault(panel => DockPanel.GetDock(panel) == Dock.Right)
               ?? throw new ArtWorkspaceSmokeTestException("底部状态栏缺少 UI 编辑状态区。");
    }

    private static void AssertUiDocumentVisibilityTrigger(FrameworkElement element, string label)
    {
        var trigger = element.Style?.Triggers
            .OfType<DataTrigger>()
            .SingleOrDefault(candidate =>
                candidate.Binding is Binding binding &&
                string.Equals(
                    binding.Path?.Path,
                    nameof(ShellViewModel.IsUiDocumentActive),
                    StringComparison.Ordinal));
        True(trigger is not null,
            $"{label}必须通过 IsUiDocumentActive DataTrigger 控制显示状态");
        True(trigger!.Value is false ||
             string.Equals(trigger.Value?.ToString(), "False", StringComparison.OrdinalIgnoreCase),
            $"{label}应在 IsUiDocumentActive=False 时触发");
        True(trigger.Setters
                .OfType<Setter>()
                .Any(setter =>
                    setter.Property == UIElement.VisibilityProperty &&
                    Equals(setter.Value, Visibility.Collapsed)),
            $"{label}的非 UI 文档触发器必须设置 Visibility=Collapsed");
    }

    private static void AssertUiCommandsDisabled(ShellViewModel shell, string context)
    {
        False(shell.UiSaveCommand.CanExecute(null), $"{context}不得误执行页面 UI 保存命令");
        False(shell.UiImportCommand.CanExecute(null), $"{context}不得误执行页面 UI 导入命令");
        False(shell.UiZoomInCommand.CanExecute(null), $"{context}不得误执行页面 UI 放大命令");
        False(shell.UiZoomOutCommand.CanExecute(null), $"{context}不得误执行页面 UI 缩小命令");
        False(shell.UiResetZoomCommand.CanExecute(null), $"{context}不得误执行页面 UI 重置缩放命令");
    }

    private static void AssertSemanticPrimaryFallsBackToBackup(
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace project,
        string validBackupJson,
        Action<JsonObject> corruptPrimary,
        string label)
    {
        var root = JsonNode.Parse(validBackupJson)?.AsObject()
            ?? throw new ArtWorkspaceSmokeTestException("有效美术工作区备份无法解析为 JSON 对象。");
        corruptPrimary(root);
        File.WriteAllText(project.Paths.ArtWorkspacePath, root.ToJsonString());

        var recovered = repository.LoadOrCreate(project.Paths.RootDirectory);
        Equal("backup", recovered.RecoverySource,
            $"主 JSON 出现语义损坏（{label}）时应回退到有效 .bak");
        Equal("备份可恢复状态", recovered.Document.OutputFrame.Name,
            $"主 JSON 出现语义损坏（{label}）时不得返回部分反序列化状态");
    }

    private static void AssertImageDisplayDimensionsWithinContract(
        ArtWorkspaceImageItemViewModel image,
        string label)
    {
        True(double.IsFinite(image.Width) && image.Width is >= 1d and <= 65536d,
            $"{label}导入后的画布宽度必须位于图片对象合同范围内，实际 {image.Width:0.###}");
        True(double.IsFinite(image.Height) && image.Height is >= 1d and <= 65536d,
            $"{label}导入后的画布高度必须位于图片对象合同范围内，实际 {image.Height:0.###}");
    }

    private static IEnumerable<Point> GetRotatedCorners(ArtWorkspaceImageItemViewModel image)
    {
        var centerX = image.X + image.Width / 2d;
        var centerY = image.Y + image.Height / 2d;
        var radians = image.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        foreach (var corner in new[]
                 {
                     new Point(image.X, image.Y),
                     new Point(image.X + image.Width, image.Y),
                     new Point(image.X + image.Width, image.Y + image.Height),
                     new Point(image.X, image.Y + image.Height)
                 })
        {
            var relativeX = corner.X - centerX;
            var relativeY = corner.Y - centerY;
            yield return new Point(
                centerX + relativeX * cosine - relativeY * sine,
                centerY + relativeX * sine + relativeY * cosine);
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool StoredOutputFrameNameEquals(string documentPath, string expected)
    {
        try
        {
            var document = ArtWorkspaceJson.Deserialize(File.ReadAllText(documentPath));
            return string.Equals(document.OutputFrame.Name, expected, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void WaitUntilWithDispatcher(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            PumpDispatcher();
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        PumpDispatcher();
        True(condition(), failureMessage);
    }

    private static void AssertReparsePointIsRejected(
        JsonArtWorkspaceRepository repository,
        ProjectWorkspace project,
        string testRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideDirectory = Path.Combine(testRoot, "reparse-outside");
        var linkParent = Path.Combine(project.Paths.AssetDirectory, "ArtWorkspace");
        var linkPath = Path.Combine(linkParent, "LinkedReferences");
        Directory.CreateDirectory(outsideDirectory);
        Directory.CreateDirectory(linkParent);
        File.WriteAllText(Path.Combine(outsideDirectory, "outside.png"), "not an image");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideDirectory);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            NotSupportedException)
        {
            Console.WriteLine($"SKIP  美术工作区重解析点回归：当前环境无法创建目录符号链接（{exception.GetType().Name}）");
            return;
        }

        ThrowsOneOf<IOException, InvalidDataException>(
            () => repository.ResolveAssetPath(
                project.Paths.RootDirectory,
                "Assets/ArtWorkspace/LinkedReferences/outside.png"),
            "工作区素材解析必须拒绝项目根内指向外部的重解析点");
    }

    private static int CountReferenceFiles(ProjectWorkspace project) =>
        Directory.Exists(project.Paths.ArtWorkspaceReferenceDirectory)
            ? Directory.GetFiles(project.Paths.ArtWorkspaceReferenceDirectory, "*", SearchOption.TopDirectoryOnly).Length
            : 0;

    private static void SavePngFixture(
        string path,
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)Math.Clamp(blue + x * 3, 0, byte.MaxValue);
                pixels[offset + 1] = (byte)Math.Clamp(green + y * 4, 0, byte.MaxValue);
                pixels[offset + 2] = red;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static LayoutContent? FindDockContent(DockingManager manager, string contentId) =>
        manager.Layout.Descendents()
            .OfType<LayoutContent>()
            .FirstOrDefault(content => string.Equals(content.ContentId, contentId, StringComparison.Ordinal))
        ?? manager.Layout.Hidden.FirstOrDefault(content =>
            string.Equals(content.ContentId, contentId, StringComparison.Ordinal));

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullRoot, Path.TrimEndingDirectorySeparator(fullCandidate), comparison) ||
               fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

    private static T AwaitWithDispatcher<T>(Task<T> task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        var deadline = DateTime.UtcNow + timeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new ArtWorkspaceSmokeTestException("等待美术工作区异步导入完成超时。");
            }

            PumpDispatcher();
            Thread.Yield();
        }

        return task.GetAwaiter().GetResult();
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArtWorkspaceSmokeTestException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ArtWorkspaceSmokeTestException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Near(double expected, double actual, string message)
    {
        if (Math.Abs(expected - actual) > 0.000001d)
        {
            throw new ArtWorkspaceSmokeTestException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new ArtWorkspaceSmokeTestException(message);
    }

    private static void ThrowsOneOf<TFirst, TSecond>(Action action, string message)
        where TFirst : Exception
        where TSecond : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is TFirst or TSecond)
        {
            return;
        }

        throw new ArtWorkspaceSmokeTestException(message);
    }

    private sealed class StubImagePicker(params string[] paths) : IArtWorkspaceImagePicker
    {
        public IReadOnlyList<string> SelectImages(string? initialDirectory) => paths;
    }

    private sealed class StubGenerationGateway(
        bool isConnected = true,
        bool isBusy = false) : IArtWorkspaceGenerationGateway
    {
        public bool IsConnected { get; private set; } = isConnected;

        public bool IsBusy { get; private set; } = isBusy;

        public string AgentDisplayName { get; private set; } = "Codex";

        public string StatusText { get; private set; } = isConnected
            ? "Codex App Server 会话已就绪"
            : "Agent 会话未启动";

        public Func<
            ArtWorkspaceGenerationRequest,
            IProgress<ArtWorkspaceGenerationProgress>?,
            CancellationToken,
            Task<ArtWorkspaceGenerationResult>>? GenerateHandler { get; set; }

        public event EventHandler? StateChanged;

        public Task<ArtWorkspaceGenerationResult> GenerateAsync(
            ArtWorkspaceGenerationRequest frozenRequest,
            IProgress<ArtWorkspaceGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            GenerateHandler?.Invoke(frozenRequest, progress, cancellationToken) ??
            Task.FromResult(new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                ErrorMessage = "测试网关未配置生成产物。"
            });

        public void SetState(bool isConnected, bool isBusy, string statusText)
        {
            IsConnected = isConnected;
            IsBusy = isBusy;
            StatusText = statusText;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ArtWorkspaceSmokeTestException(string message) : Exception(message);
}
