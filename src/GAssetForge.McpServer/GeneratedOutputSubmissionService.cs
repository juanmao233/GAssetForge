using System.Globalization;
using System.Security.Cryptography;
using GAssetForge.Core.Generation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GAssetForge.McpServer;

internal sealed record GeneratedOutputSubmissionResult(
    int OutputIndex,
    string SourcePath,
    string ModelOutputPath,
    string NormalizedPath,
    string? ProcessedPath,
    string TargetPath,
    string SourceSha256,
    bool SourceResized,
    GeneratedOutputProcessingResult Processing);

/// <summary>
/// Imports an image-model return into the frozen project workspace and executes every deterministic
/// image step that previously required Agent-authored PowerShell or ad-hoc file manipulation.
/// </summary>
internal sealed class GeneratedOutputSubmissionService(
    WorkspaceContextResolver contextResolver,
    GeneratedAssetPostProcessor postProcessor)
{
    private const long MaximumSourceFileBytes = 544L * 1024L * 1024L;
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<GeneratedOutputSubmissionResult> SubmitAsync(
        GenerationTask task,
        GenerationDeliveryPlan delivery,
        string agentId,
        int outputIndex,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (delivery.Workspace is not { } workspace)
        {
            throw new InvalidDataException(
                "该任务没有项目内 generation workspace；旧任务请使用 process_generation_output。 ");
        }

        if (task.Status != GenerationTaskStatus.Claimed ||
            !string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"任务 {task.Id} 必须由当前 agentId 认领后才能提交模型产物。");
        }

        if (outputIndex < 0 || outputIndex >= task.Constraints.Output.Count ||
            workspace.ModelOutputPaths.Length != task.Constraints.Output.Count ||
            delivery.StagingPaths.Length != task.Constraints.Output.Count ||
            delivery.TargetPaths.Length != task.Constraints.Output.Count)
        {
            throw new InvalidDataException("运行工作区输出路径与 output.count 不一致。");
        }

        var normalizedSourcePath = ValidateSourcePath(sourcePath, delivery.AssetRoot);
        var modelOutput = contextResolver.ResolveDeliveryAssetPath(
            workspace.ModelOutputPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "模型原图归档");
        var normalized = contextResolver.ResolveDeliveryAssetPath(
            delivery.StagingPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "软件归一化输入");
        var target = contextResolver.ResolveDeliveryAssetPath(
            delivery.TargetPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "最终交付");

        string sourceSha256;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "model_output.import",
                   agentId,
                   outputIndex,
                   modelOutput.RelativePath,
                   normalizedSourcePath))
        {
            try
            {
                sourceSha256 = await ImportModelOutputAsync(
                        normalizedSourcePath,
                        modelOutput.FullPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(
                    $"模型原图已由软件归档，sha256={sourceSha256}",
                    modelOutput.RelativePath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        bool resized;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "model_output.normalize",
                   agentId,
                   outputIndex,
                   normalized.RelativePath,
                   modelOutput.RelativePath))
        {
            try
            {
                resized = await NormalizeAsync(
                        modelOutput.FullPath,
                        normalized.FullPath,
                        task.Constraints.Output,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(
                    resized
                        ? "软件已按冻结画布 contain-fit 并补齐精确首道底色。"
                        : "模型原图尺寸已匹配；软件仍完成了不透明底色与边框归一化。",
                    normalized.RelativePath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        GeneratedOutputProcessingResult processing;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "output.process_and_commit",
                   agentId,
                   outputIndex,
                   target.RelativePath,
                   normalized.RelativePath))
        {
            try
            {
                processing = await postProcessor.ProcessAsync(
                        task,
                        delivery,
                        agentId,
                        outputIndex,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(processing.Message, processing.RelativeTargetPath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        return new GeneratedOutputSubmissionResult(
            outputIndex,
            normalizedSourcePath,
            modelOutput.RelativePath,
            normalized.RelativePath,
            processing.RelativeProcessedPath,
            processing.RelativeTargetPath ?? target.RelativePath,
            sourceSha256,
            resized,
            processing);
    }

    private static string ValidateSourcePath(string sourcePath, string assetRoot)
    {
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new InvalidDataException("sourcePath 必须是图像模型返回的绝对本地文件路径。");
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var allowedRoots = new[]
            {
                Path.GetFullPath(assetRoot),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
                Path.GetFullPath(Path.GetTempPath()),
                Path.GetFullPath(Environment.CurrentDirectory)
            }
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        if (!allowedRoots.Any(root => IsWithinRoot(root, fullPath)))
        {
            throw new UnauthorizedAccessException(
                "sourcePath 只能来自当前项目、Codex 生成目录、系统临时目录或 MCP 工作目录。");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new FileNotFoundException("图像模型返回文件不存在。", fullPath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("拒绝导入重解析点模型产物。");
        }

        if (info.Length is <= 0 or > MaximumSourceFileBytes)
        {
            throw new InvalidDataException(
                $"模型产物大小必须位于 1 到 {MaximumSourceFileBytes / 1024 / 1024} MiB 之间。");
        }

        using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        try
        {
            stream.ReadExactly(signature);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("图像模型返回文件不是完整 PNG。", exception);
        }

        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("submit_generation_output 当前只接受真实 PNG 模型产物。");
        }

        stream.Position = 0;
        _ = Image.Identify(stream)
            ?? throw new InvalidDataException("无法识别图像模型返回的 PNG。");
        return fullPath;
    }

    private static async Task<string> ImportModelOutputAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(targetPath))
        {
            var existingHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return sourceHash;
            }

            throw new IOException("项目运行工作区已归档不同的模型原图；为保留审计链拒绝覆盖。");
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath);
            return sourceHash;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<bool> NormalizeAsync(
        string sourcePath,
        string targetPath,
        GenerationOutputSpec output,
        CancellationToken cancellationToken)
    {
        var removal = output.BackgroundRemoval;
        var background = removal is null
            ? new Rgba32(0, 0, 0, 0)
            : ParseColor(removal.ColorHex);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp.png";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = Image.Load<Rgba32>(sourcePath);
                var wasResized = source.Width != output.Width || source.Height != output.Height;
                source.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(output.Width, output.Height),
                    Mode = ResizeMode.Max,
                    Sampler = KnownResamplers.Lanczos3,
                    Compand = true
                }));
                using var normalized = new Image<Rgba32>(output.Width, output.Height, background);
                var location = new Point(
                    (output.Width - source.Width) / 2,
                    (output.Height - source.Height) / 2);
                normalized.Mutate(context => context.DrawImage(source, location, 1f));
                if (removal is not null)
                {
                    ForceExactBorder(normalized, background, Math.Max(1, removal.EdgeContract));
                }

                normalized.SaveAsPng(temporaryPath);
                return wasResized;
            }, cancellationToken).ConfigureAwait(false);

            File.Move(temporaryPath, targetPath, overwrite: true);
            return result;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ForceExactBorder(Image<Rgba32> image, Rgba32 color, int thickness)
    {
        var safeThickness = Math.Min(thickness, Math.Min(image.Width, image.Height) / 2);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                if (y < safeThickness || y >= image.Height - safeThickness)
                {
                    row.Fill(color);
                    continue;
                }

                for (var x = 0; x < safeThickness; x++)
                {
                    row[x] = color;
                    row[image.Width - 1 - x] = color;
                }
            }
        });
    }

    private static Rgba32 ParseColor(string colorHex)
    {
        var value = colorHex.AsSpan().Trim();
        if (value.Length != 7 || value[0] != '#')
        {
            throw new InvalidDataException("背景剔除颜色必须是 #RRGGBB。");
        }

        return new Rgba32(
            byte.Parse(value.Slice(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Slice(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Slice(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.MaxValue);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.GetFullPath(candidate));
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
