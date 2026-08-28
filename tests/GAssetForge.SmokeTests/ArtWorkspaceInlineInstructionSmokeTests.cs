using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.ArtWorkspaces;
using GAssetForge.Core.Generation;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceInlineInstructionSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        StructuredConversationRoundTripsAndLegacyJsonFallsBack();
        InlineDraftFreezesOrderRegionsAndFailsClosed(temporaryRoot);
        SemanticBudgetCountsImageTokens(temporaryRoot);
    }

    private static void StructuredConversationRoundTripsAndLegacyJsonFallsBack()
    {
        var armorImageId = Guid.NewGuid().ToString("N");
        var hairImageId = Guid.NewGuid().ToString("N");
        var message = new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.User,
            Text = "我需要图片的护甲加上图片的发型。",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            State = "captured",
            Segments =
            [
                ConversationText("我需要这张"),
                ConversationImage("A", armorImageId, "护甲参考", 0.1d, 0.2d, 0.3d, 0.4d),
                ConversationText("图片的护甲加上这张"),
                ConversationImage("B", hairImageId, "发型参考"),
                ConversationText("图片的发型。")
            ]
        };
        var document = ArtWorkspaceDocumentFactory.Create();
        document.Conversation.Add(message);

        var json = ArtWorkspaceJson.Serialize(document);
        var roundTrip = ArtWorkspaceJson.Deserialize(json);
        var restored = roundTrip.Conversation.Single(candidate => candidate.Id == message.Id);
        Equal(5, restored.Segments.Count, "结构化对话片段数量");
        Equal("我需要这张", restored.Segments[0].Text, "结构化对话首段文字");
        Equal(armorImageId, restored.Segments[1].ImageId, "结构化对话 A 图片身份");
        Equal("A", restored.Segments[1].DisplayLabel, "结构化对话 A 标签");
        Near(0.1d, restored.Segments[1].Region!.X, "结构化对话 A 局部 X");
        Equal(hairImageId, restored.Segments[3].ImageId, "结构化对话 B 图片身份");
        True(restored.Segments[3].Region is null, "整图引用往返后不得凭空产生局部区域");

        var visibleMessage = new ArtWorkspaceConversationMessageViewModel(restored);
        var visibleReferenceTexts = visibleMessage.Segments
            .Where(segment => segment.IsImageReference)
            .Select(segment => segment.ReferenceText)
            .ToArray();
        Equal("A · 护甲参考 · 局部框选", visibleReferenceTexts[0], "历史对话 A 可见摘要");
        Equal("B · 发型参考 · 整张图片", visibleReferenceTexts[1], "历史对话 B 可见摘要");
        False(visibleReferenceTexts.Any(text =>
                text.Contains(armorImageId, StringComparison.Ordinal) ||
                text.Contains(hairImageId, StringComparison.Ordinal) ||
                text.Contains("0.1", StringComparison.Ordinal) ||
                text.Contains("0.2", StringComparison.Ordinal) ||
                text.Contains("\\", StringComparison.Ordinal) ||
                text.Contains("/", StringComparison.Ordinal)),
            "历史对话 UI 不得泄露图片 ID、坐标或本地路径");

        var legacyRoot = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("旧版工作区 JSON 夹具无效。");
        var legacyConversation = legacyRoot["conversation"] as JsonArray
            ?? throw new InvalidOperationException("旧版工作区 JSON 缺少 conversation。");
        var legacyMessage = legacyConversation
            .OfType<JsonObject>()
            .Single(node => string.Equals(node["id"]?.GetValue<string>(), message.Id, StringComparison.Ordinal));
        True(legacyMessage.Remove("segments"), "旧纯文本 JSON 夹具应真实移除 segments 字段");
        var legacyRoundTrip = ArtWorkspaceJson.Deserialize(legacyRoot.ToJsonString());
        var restoredLegacy = legacyRoundTrip.Conversation.Single(candidate => candidate.Id == message.Id);
        Equal(message.Text, restoredLegacy.Text, "旧纯文本 JSON 应保留可见对话正文");
        Equal(0, restoredLegacy.Segments.Count, "旧纯文本 JSON 缺少 segments 时应安全回退而非伪造引用");

        var excessiveHistory = ArtWorkspaceDocumentFactory.Create();
        excessiveHistory.Conversation.Add(new ArtWorkspaceConversationMessage
        {
            Role = ArtWorkspaceMessageRole.User,
            Text = "非法的超量图片引用历史",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            State = "captured",
            Segments = Enumerable.Range(
                    0,
                    ArtWorkspaceInstructionSegmentSemantics.MaximumImageReferenceCount + 1)
                .Select(index => ConversationImage(
                    $"R{index + 1}",
                    armorImageId,
                    "护甲参考"))
                .ToList()
        });
        Throws<InvalidDataException>(
            () => ArtWorkspaceJson.Serialize(excessiveHistory),
            "持久化历史不得接受超过生成合同上限的图片 token 数量");
    }

    private static void InlineDraftFreezesOrderRegionsAndFailsClosed(string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "art-workspace-inline-instruction",
            Guid.NewGuid().ToString("N"));
        var fixtureDirectory = Path.Combine(root, "fixtures");
        var projectParent = Path.Combine(root, "projects");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(projectParent);
        var armorFixture = Path.Combine(fixtureDirectory, "armor.png");
        var hairFixture = Path.Combine(fixtureDirectory, "hair.png");
        var characterFixture = Path.Combine(fixtureDirectory, "character.png");
        SavePngFixture(armorFixture, 10, 8, 0x35, 0x62, 0xA8);
        SavePngFixture(hairFixture, 8, 12, 0xB5, 0x64, 0x92);
        SavePngFixture(characterFixture, 12, 12, 0x66, 0xA2, 0x58);

        var paths = new WorkspacePaths(Path.Combine(root, "desktop-data"));
        var project = new ProjectWorkspaceService().Create(
            projectParent,
            "InlineInstruction",
            "ui");
        var repository = new JsonArtWorkspaceRepository(paths);
        using var console = new WorkbenchConsoleService(paths);
        using var viewModel = new ArtWorkspaceViewModel(repository, new EmptyImagePicker(), console);
        True(viewModel.Activate(project.Paths.RootDirectory), "内联图片指令测试应激活正式项目");
        Equal(3, viewModel.AddImagesAt(
                [armorFixture, hairFixture, characterFixture],
                120d,
                180d),
            "内联图片指令测试应导入 A/B/C 三张受管图片");
        var armor = viewModel.Images.Single(image => image.Name == Path.GetFileNameWithoutExtension(armorFixture));
        var hair = viewModel.Images.Single(image => image.Name == Path.GetFileNameWithoutExtension(hairFixture));
        var character = viewModel.Images.Single(image => image.Name == Path.GetFileNameWithoutExtension(characterFixture));

        viewModel.SelectImage(armor);
        True(viewModel.SetSelectedImageRegionNormalized(0.1d, 0.2d, 0.3d, 0.4d, commit: true),
            "护甲图片第一处框选应建立");
        var armorBodyToken = viewModel.InsertSelectedImageReference()
            ?? throw new InvalidOperationException("护甲图片第一处 token 创建失败。");
        True(viewModel.SetSelectedImageRegionNormalized(0.6d, 0.55d, 0.25d, 0.3d, commit: true),
            "护甲图片第二处框选应建立");
        var armorEmblemToken = viewModel.InsertSelectedImageReference()
            ?? throw new InvalidOperationException("护甲图片第二处 token 创建失败。");

        viewModel.SelectImage(hair);
        var hairToken = viewModel.InsertSelectedImageReference()
            ?? throw new InvalidOperationException("发型整图 token 创建失败。");
        viewModel.SelectImage(character);
        True(viewModel.SetSelectedImageRegionNormalized(0.2d, 0.15d, 0.6d, 0.75d, commit: true),
            "角色图片局部框选应建立");
        var characterToken = viewModel.InsertSelectedImageReference()
            ?? throw new InvalidOperationException("角色局部 token 创建失败。");

        var orderedDraft = new[]
        {
            ArtWorkspaceInstructionDraftSegment.CreateText("我需要这张"),
            armorBodyToken,
            ArtWorkspaceInstructionDraftSegment.CreateText("图片的护甲加上这张"),
            hairToken,
            ArtWorkspaceInstructionDraftSegment.CreateText("图片的发型，套在这张"),
            characterToken,
            ArtWorkspaceInstructionDraftSegment.CreateText("图片的角色上，并使用这张"),
            armorEmblemToken,
            ArtWorkspaceInstructionDraftSegment.CreateText("图片局部的纹章。")
        };
        True(viewModel.ReplaceInstructionDraftSegments(orderedDraft),
            "A/B/C 与同图重复区域应按对话顺序进入编辑器模型");
        Equal(9, viewModel.InstructionDraftSegments.Count, "编辑器有序片段数量");
        Equal(armor.Id, viewModel.InstructionDraftSegments[1].ImageId, "A token 图片身份");
        Equal(hair.Id, viewModel.InstructionDraftSegments[3].ImageId, "B token 图片身份");
        Equal(character.Id, viewModel.InstructionDraftSegments[5].ImageId, "C token 图片身份");
        Equal(armor.Id, viewModel.InstructionDraftSegments[7].ImageId, "重复 A token 图片身份");
        False(string.Equals(
                viewModel.InstructionDraftSegments[1].TokenId,
                viewModel.InstructionDraftSegments[7].TokenId,
                StringComparison.Ordinal),
            "同图重复引用必须保留独立 token 身份");
        Near(0.1d, viewModel.InstructionDraftSegments[1].Region!.X,
            "第一处护甲 token 应冻结第一块局部");
        Near(0.6d, viewModel.InstructionDraftSegments[7].Region!.X,
            "第二处护甲 token 应冻结第二块局部");

        viewModel.SelectImage(armor);
        True(viewModel.SetSelectedImageRegionNormalized(0.75d, 0.05d, 0.2d, 0.2d, commit: true),
            "插入 token 后仍可重新选择其它护甲局部");
        viewModel.SelectImage(hair);
        Near(0.1d, viewModel.InstructionDraftSegments[1].Region!.X,
            "切换选图和重画区域不得漂移已插入 token A");
        Near(0.6d, viewModel.InstructionDraftSegments[7].Region!.X,
            "切换选图和重画区域不得漂移已插入的重复 token A");

        AssertEditorShowsOnlySafeInlineText(viewModel, armor, hair, character);

        ArtWorkspaceGenerationRequest? capturedRequest = null;
        var gateway = new CapturingGateway
        {
            GenerateHandler = request =>
            {
                capturedRequest = request;
                return Task.FromResult(new ArtWorkspaceGenerationResult
                {
                    IsSuccess = false,
                    ErrorMessage = "结构化内联引用 Smoke 到此结束"
                });
            }
        };
        viewModel.ConfigureGenerationGateway(gateway);
        True(viewModel.SubmitInstructionCommand.CanExecute(null),
            "A/B/C 引用全部有效时应允许提交");
        viewModel.SubmitInstructionCommand.Execute(null);
        WaitUntilWithDispatcher(
            () => !viewModel.IsGenerating && capturedRequest is not null,
            TimeSpan.FromSeconds(10),
            "内联图片指令应送达生成网关");

        Equal(9, capturedRequest!.InstructionSegments.Length, "冻结请求有序片段数量");
        Equal(armorBodyToken.TokenId, capturedRequest.InstructionSegments[1].TokenId,
            "冻结请求 A token 身份");
        Equal(hairToken.TokenId, capturedRequest.InstructionSegments[3].TokenId,
            "冻结请求 B token 身份");
        Equal(characterToken.TokenId, capturedRequest.InstructionSegments[5].TokenId,
            "冻结请求 C token 身份");
        Equal(armorEmblemToken.TokenId, capturedRequest.InstructionSegments[7].TokenId,
            "冻结请求重复 A token 身份");
        Near(0.1d, capturedRequest.InstructionSegments[1].Region!.X,
            "冻结请求应使用插入时 A 局部而非提交时当前框选");
        Near(0.6d, capturedRequest.InstructionSegments[7].Region!.X,
            "冻结请求应独立保存重复 A 的第二处局部");
        True(capturedRequest.InstructionReference is null,
            "新版有序 token 不得回写旧单图片引用并覆盖插入职责");

        var userMessage = viewModel.Conversation.Last(message => message.IsUser);
        Equal(9, userMessage.Segments.Count, "用户历史应结构化保留全部插入位置");
        Equal("我需要这张", userMessage.Segments[0].Text, "历史首段文字");
        Equal("A · armor · 局部框选", userMessage.Segments[1].ReferenceText,
            "历史 A 图片可见标签");
        Equal("B · hair · 整张图片", userMessage.Segments[3].ReferenceText,
            "历史 B 图片可见标签");
        Equal("C · character · 局部框选", userMessage.Segments[5].ReferenceText,
            "历史 C 图片可见标签");
        Equal("D · armor · 局部框选", userMessage.Segments[7].ReferenceText,
            "历史重复 A 图片应获得独立 D 标签");
        False(userMessage.Segments.Any(segment =>
                segment.ReferenceText.Contains(armor.AbsolutePath, StringComparison.OrdinalIgnoreCase) ||
                segment.ReferenceText.Contains(armor.Id, StringComparison.Ordinal) ||
                segment.ReferenceText.Contains("0.1", StringComparison.Ordinal)),
            "历史对话 UI 不得展示路径、内部 ID 或归一化坐标");
        True(viewModel.SaveNow(), "结构化内联对话应可立即保存");

        using (var reloadConsole = new WorkbenchConsoleService(paths))
        using (var reloaded = new ArtWorkspaceViewModel(repository, new EmptyImagePicker(), reloadConsole))
        {
            True(reloaded.Activate(project.Paths.RootDirectory), "结构化内联对话应可重载");
            var restoredUser = reloaded.Conversation.Last(message => message.IsUser);
            Equal(9, restoredUser.Segments.Count, "重载后结构化片段数量");
            Equal("A · armor · 局部框选", restoredUser.Segments[1].ReferenceText,
                "重载后 A 标签与局部语义");
            Equal("D · armor · 局部框选", restoredUser.Segments[7].ReferenceText,
                "重载后同图重复 token 顺序与标签");
            Near(0.6d, restoredUser.Segments[7].ToModel().Region!.X,
                "重载后重复 A token 的独立区域快照");
        }

        True(viewModel.ReplaceInstructionDraftSegments(orderedDraft),
            "fail-closed 测试应恢复原始有序草稿");
        viewModel.SelectImage(armor);
        armor.IsVisible = false;
        True(viewModel.HasInvalidInstructionReferences,
            "隐藏被引用图片时必须将对应 token 标记为无效");
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "隐藏被引用图片时提交必须 fail-closed");
        armor.IsVisible = true;
        False(viewModel.HasInvalidInstructionReferences,
            "重新显示且文件存在时 token 应恢复有效");

        File.Delete(hair.AbsolutePath);
        True(viewModel.HasInvalidInstructionReferences,
            "被引用受管文件缺失时必须将对应 token 标记为无效");
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "被引用受管文件缺失时提交必须 fail-closed");
        File.Copy(hairFixture, hair.AbsolutePath);
        False(viewModel.HasInvalidInstructionReferences,
            "恢复同一受管文件后 token 应恢复有效");

        viewModel.SelectImage(character);
        viewModel.RemoveSelectedImageCommand.Execute(null);
        True(viewModel.HasInvalidInstructionReferences,
            "删除被引用画布图片后必须保留 token 并标记失效，不能静默改指其它图片");
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "删除被引用画布图片后提交必须 fail-closed");
        Equal(characterToken.TokenId, viewModel.InstructionDraftSegments[5].TokenId,
            "删除图片不得静默删除或重写历史 token 身份");
    }

    private static void SemanticBudgetCountsImageTokens(string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "art-workspace-inline-budget",
            Guid.NewGuid().ToString("N"));
        var fixtureDirectory = Path.Combine(root, "fixtures");
        var projectParent = Path.Combine(root, "projects");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(projectParent);
        var fixture = Path.Combine(fixtureDirectory, "budget.png");
        SavePngFixture(fixture, 4, 4, 0x55, 0x77, 0x99);
        var paths = new WorkspacePaths(Path.Combine(root, "desktop-data"));
        var project = new ProjectWorkspaceService().Create(projectParent, "InlineBudget", "ui");
        using var console = new WorkbenchConsoleService(paths);
        using var viewModel = new ArtWorkspaceViewModel(
            new JsonArtWorkspaceRepository(paths),
            new EmptyImagePicker(),
            console);
        True(viewModel.Activate(project.Paths.RootDirectory), "内联预算测试应激活正式项目");
        Equal(1, viewModel.AddImagesAt([fixture], 0d, 0d), "内联预算测试应导入一张图片");
        viewModel.ConfigureGenerationGateway(new CapturingGateway());
        var token = viewModel.InsertSelectedImageReference()
            ?? throw new InvalidOperationException("内联预算测试 token 创建失败。");

        var exactTextLength = ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength -
                              ArtWorkspaceInstructionSegmentSemantics.ImageReferenceSemanticLength;
        True(viewModel.ReplaceInstructionDraftSegments(
                [
                    ArtWorkspaceInstructionDraftSegment.CreateText(new string('a', exactTextLength)),
                    token
                ]),
            "可见文字加一个 token 恰好等于 16000 语义字符时应接受");
        Equal(ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength,
            viewModel.InstructionDraftSemanticLength,
            "恰好边界的内联指令语义长度");
        True(viewModel.SubmitInstructionCommand.CanExecute(null),
            "恰好 16000 语义字符仍应允许提交");

        False(viewModel.ReplaceInstructionDraftSegments(
                [
                    ArtWorkspaceInstructionDraftSegment.CreateText(new string('a', exactTextLength + 1)),
                    token
                ]),
            "15969 个可见字符加一个 32 字符 token 必须因总预算 16001 被拒绝");
        Equal(ArtWorkspaceInstructionSegmentSemantics.MaximumSemanticLength,
            viewModel.InstructionDraftSemanticLength,
            "被预算拒绝的替换不得部分污染原草稿");

        True(viewModel.ReplaceInstructionDraftSegments(
                [
                    ArtWorkspaceInstructionDraftSegment.CreateText(new string('a', 16000)),
                    token
                ],
                enforceBudget: false),
            "测试应可注入超预算草稿验证命令门禁");
        Equal(16000 + ArtWorkspaceInstructionSegmentSemantics.ImageReferenceSemanticLength,
            viewModel.InstructionDraftSemanticLength,
            "16000 可见文字加 token 必须计入非零语义成本");
        False(viewModel.SubmitInstructionCommand.CanExecute(null),
            "即使调用方绕过编辑器即时检查，提交门禁仍必须拒绝超预算 token 指令");

        var maximumTokens = Enumerable.Range(
                0,
                ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInlineInstructionReferenceCount)
            .Select(_ => ArtWorkspaceInstructionDraftSegment.CreateImageReference(
                viewModel.SelectedImage!.Id,
                viewModel.SelectedImage.Name))
            .ToArray();
        True(viewModel.ReplaceInstructionDraftSegments(maximumTokens),
            "允许数量上限内的纯图片 token 指令");
        Equal(
            ArtWorkspaceGenerationConstraintPackageBuilder.MaximumInlineInstructionReferenceCount *
            ArtWorkspaceInstructionSegmentSemantics.ImageReferenceSemanticLength,
            viewModel.InstructionDraftSemanticLength,
            "大量 chip 必须累计非零语义成本而非按零长度绕过预算");
        Throws<ArgumentException>(
            () => viewModel.ReplaceInstructionDraftSegments(
                [
                    .. maximumTokens,
                    ArtWorkspaceInstructionDraftSegment.CreateImageReference(
                        viewModel.SelectedImage!.Id,
                        viewModel.SelectedImage.Name)
                ]),
            "超过图片 token 数量上限时必须 fail-closed");
    }

    private static void AssertEditorShowsOnlySafeInlineText(
        ArtWorkspaceViewModel viewModel,
        params ArtWorkspaceImageItemViewModel[] images)
    {
        var propertiesView = new ArtWorkspacePropertiesView { DataContext = viewModel };
        var window = new Window
        {
            Width = 520d,
            Height = 900d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = propertiesView
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            var editor = propertiesView.FindName("ArtWorkspaceInstructionRichTextBox") as RichTextBox
                ?? throw new InvalidOperationException("美术工作区属性页缺少内联图片 RichTextBox。");
            var containers = editor.Document.Blocks
                .OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<InlineUIContainer>())
                .ToArray();
            Equal(4, containers.Length, "RichTextBox 应按插入位置渲染四个图片 chip");
            var visibleText = string.Join(
                " | ",
                containers.SelectMany(container => FindVisualDescendants<TextBlock>(container.Child))
                    .Select(text => text.Text));
            True(visibleText.Contains("armor", StringComparison.Ordinal) &&
                 visibleText.Contains("hair", StringComparison.Ordinal) &&
                 visibleText.Contains("character", StringComparison.Ordinal) &&
                 visibleText.Contains("局部框选", StringComparison.Ordinal) &&
                 visibleText.Contains("整张图片", StringComparison.Ordinal),
                "图片 chip 应只显示用户可理解的名称与整图/局部语义");
            foreach (var image in images)
            {
                False(visibleText.Contains(image.AbsolutePath, StringComparison.OrdinalIgnoreCase) ||
                      visibleText.Contains(image.Id, StringComparison.Ordinal) ||
                      visibleText.Contains(image.AssetId, StringComparison.Ordinal),
                    "图片 chip 不得泄露绝对路径、图片 ID 或素材 ID");
            }

            False(visibleText.Contains("0.1", StringComparison.Ordinal) ||
                  visibleText.Contains("0.6", StringComparison.Ordinal),
                "图片 chip 不得展示归一化局部坐标");

            var editorDocumentBeforeCanvasMove = editor.Document;
            var draftRevisionBeforeCanvasMove = viewModel.InstructionDraftRevision;
            images[0].X += 17d;
            images[0].RotationDegrees += 5d;
            PumpDispatcher();
            Equal(draftRevisionBeforeCanvasMove, viewModel.InstructionDraftRevision,
                "图片位置或旋转变化不得把冻结 token 误判为草稿内容变化");
            True(ReferenceEquals(editorDocumentBeforeCanvasMove, editor.Document),
                "图片变换不得重建 RichTextBox 并丢失用户光标、选择或撤销历史");

            var originalSegments = viewModel.InstructionDraftSegments
                .Select(segment => segment.DeepClone())
                .ToArray();
            True(viewModel.ReplaceInstructionDraftSegments(
                    [ArtWorkspaceInstructionDraftSegment.CreateText("前半后半")]),
                "光标插入测试应先建立纯文字草稿");
            PumpDispatcher();
            var paragraph = editor.Document.Blocks.OfType<Paragraph>().Single();
            var run = paragraph.Inlines.OfType<Run>().Single();
            editor.CaretPosition = run.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward)
                ?? throw new InvalidOperationException("无法把 RichTextBox 光标定位到文字中间。");
            var insertButton = propertiesView.FindName("ArtWorkspaceInsertSelectedImageButton") as Button
                ?? throw new InvalidOperationException("美术工作区属性页缺少插入所选图片按钮。");
            insertButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();
            Equal(3, viewModel.InstructionDraftSegments.Count,
                "点击插入图片后应把文字拆为前文、token、后文三个有序片段");
            Equal("前半", viewModel.InstructionDraftSegments[0].Text,
                "光标前文字应保持在图片 token 之前");
            True(viewModel.InstructionDraftSegments[1].IsImageReference,
                "光标位置应真实插入图片 token");
            Equal(viewModel.SelectedImage!.Id, viewModel.InstructionDraftSegments[1].ImageId,
                "插入按钮应捕获点击当时所选图片");
            Equal("后半", viewModel.InstructionDraftSegments[2].Text,
                "光标后文字应保持在图片 token 之后");
            True(viewModel.ReplaceInstructionDraftSegments(originalSegments),
                "完成真实光标插入回归后应恢复 A/B/C 主测试草稿");
            PumpDispatcher();
        }
        finally
        {
            window.Close();
        }
    }

    private static ArtWorkspaceConversationSegment ConversationText(string text) => new()
    {
        Kind = ArtWorkspaceConversationSegmentKinds.Text,
        Text = text
    };

    private static ArtWorkspaceConversationSegment ConversationImage(
        string label,
        string imageId,
        string imageName,
        double? x = null,
        double? y = null,
        double? width = null,
        double? height = null) => new()
    {
        Kind = ArtWorkspaceConversationSegmentKinds.ImageReference,
        TokenId = Guid.NewGuid().ToString("N"),
        ImageId = imageId,
        ImageName = imageName,
        DisplayLabel = label,
        Region = x is null || y is null || width is null || height is null
            ? null
            : new ArtWorkspaceImageRegionSelection
            {
                ImageId = imageId,
                X = x.Value,
                Y = y.Value,
                Width = width.Value,
                Height = height.Value
            }
    };

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

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
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = blue;
            pixels[index + 1] = green;
            pixels[index + 2] = red;
            pixels[index + 3] = byte.MaxValue;
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

    private static void WaitUntilWithDispatcher(
        Func<bool> predicate,
        TimeSpan timeout,
        string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(message);
            }

            PumpDispatcher();
            Thread.Yield();
        }
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

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

        throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.000001d)
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private sealed class EmptyImagePicker : IArtWorkspaceImagePicker
    {
        public IReadOnlyList<string> SelectImages(string? initialDirectory) => [];
    }

    private sealed class CapturingGateway : IArtWorkspaceGenerationGateway
    {
        public bool IsConnected => true;

        public bool IsBusy => false;

        public string AgentDisplayName => "Codex Smoke";

        public string StatusText => "Agent 已就绪";

        public Func<ArtWorkspaceGenerationRequest, Task<ArtWorkspaceGenerationResult>>? GenerateHandler { get; init; }

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<ArtWorkspaceGenerationResult> GenerateAsync(
            ArtWorkspaceGenerationRequest frozenRequest,
            IProgress<ArtWorkspaceGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            GenerateHandler?.Invoke(frozenRequest) ?? Task.FromResult(new ArtWorkspaceGenerationResult
            {
                IsSuccess = false,
                ErrorMessage = "Smoke 网关不执行真实生成"
            });
    }
}
