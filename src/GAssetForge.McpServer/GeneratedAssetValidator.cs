using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using GAssetForge.Core.Generation;

namespace GAssetForge.McpServer;

internal static class GeneratedAssetValidator
{
    private const long Mebibyte = 1024L * 1024L;
    private const long MaximumGeneratedAssetBytes = 544L * Mebibyte;
    private const long MaximumDecodedImageBytes = 513L * Mebibyte;
    private const long MaximumNonImageChunkBytes = 16L * Mebibyte;
    private const long MinimumCompressedImageBudget = 1L * Mebibyte;
    private const int MaximumChunkCount = 4_096;
    private const int MaximumIdatChunkCount = 2_048;
    private const string TilemapGridPlane = "XY";

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Validate(string fullPath, GenerationConstraintPackage constraints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(constraints);

        var geometry = ValidateWorkflowConstraints(constraints);
        var output = constraints.Output
            ?? throw new InvalidDataException("生成任务缺少输出规格。");

        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length <= 0)
        {
            throw new InvalidDataException($"生成产物不存在或为空：{fullPath}");
        }

        if (file.Length > MaximumGeneratedAssetBytes)
        {
            throw new InvalidDataException(
                $"生成产物超过 {MaximumGeneratedAssetBytes / Mebibyte} MiB 校验上限：{fullPath}");
        }

        var format = output.Format?.Trim().TrimStart('.').ToLowerInvariant()
            ?? throw new InvalidDataException("生成任务的输出格式为空。");
        if (format == "png")
        {
            ValidatePng(fullPath, constraints, geometry);
        }
    }

    private static void ValidatePng(
        string fullPath,
        GenerationConstraintPackage constraints,
        TilemapAlphaGeometry? geometry)
    {
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        Span<byte> signature = stackalloc byte[8];
        ReadExactly(stream, signature, fullPath);
        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException($"生成产物不是真实 PNG：签名无效（{fullPath}）。");
        }

        var firstChunk = true;
        var sawHeader = false;
        var sawImageData = false;
        var sawChunkAfterImageData = false;
        var sawEnd = false;
        long imageDataBytes = 0;
        long nonImageChunkBytes = 0;
        var chunkCount = 0;
        var idatChunkCount = 0;
        PngHeader? header = null;
        var idatSegments = new List<IdatSegment>();
        var buffer = new byte[64 * 1024];
        var chunkHeaderBuffer = new byte[8];
        var storedCrcBuffer = new byte[4];

        while (!sawEnd)
        {
            chunkCount++;
            if (chunkCount > MaximumChunkCount)
            {
                throw new InvalidDataException(
                    $"PNG 数据块数量超过 {MaximumChunkCount} 个校验上限（{fullPath}）。");
            }

            var chunkHeader = chunkHeaderBuffer.AsSpan();
            ReadExactly(stream, chunkHeader, fullPath);
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
            var chunkTypeBytes = chunkHeader[4..8];
            if (!IsAsciiChunkType(chunkTypeBytes))
            {
                throw new InvalidDataException($"PNG 数据块类型必须是四个 ASCII 字母（{fullPath}）。");
            }

            var chunkType = Encoding.ASCII.GetString(chunkTypeBytes);
            if (firstChunk && (!string.Equals(chunkType, "IHDR", StringComparison.Ordinal) || chunkLength != 13))
            {
                throw new InvalidDataException($"PNG 首个数据块必须是长度为 13 的 IHDR（{fullPath}）。");
            }

            if (string.Equals(chunkType, "IHDR", StringComparison.Ordinal) &&
                (sawHeader || !firstChunk || chunkLength != 13))
            {
                throw new InvalidDataException($"PNG IHDR 数据块重复或位置无效（{fullPath}）。");
            }

            if (string.Equals(chunkType, "IEND", StringComparison.Ordinal) && chunkLength != 0)
            {
                throw new InvalidDataException($"PNG IEND 数据块长度必须为 0（{fullPath}）。");
            }

            if (chunkLength > stream.Length - stream.Position - 4)
            {
                throw new InvalidDataException($"PNG 数据块 {chunkType} 被截断（{fullPath}）。");
            }

            if (string.Equals(chunkType, "IDAT", StringComparison.Ordinal))
            {
                if (!sawHeader || header is null)
                {
                    throw new InvalidDataException($"PNG IDAT 出现在 IHDR 之前（{fullPath}）。");
                }

                if (sawChunkAfterImageData)
                {
                    throw new InvalidDataException($"PNG IDAT 数据块必须连续出现（{fullPath}）。");
                }

                idatChunkCount++;
                if (idatChunkCount > MaximumIdatChunkCount)
                {
                    throw new InvalidDataException(
                        $"PNG IDAT 数据块数量超过 {MaximumIdatChunkCount} 个校验上限（{fullPath}）。");
                }

                if (chunkLength > header.MaximumCompressedImageBytes - imageDataBytes)
                {
                    throw new InvalidDataException(
                        $"PNG IDAT 压缩数据超过与画布匹配的校验上限（{fullPath}）。");
                }

                imageDataBytes += chunkLength;
                idatSegments.Add(new IdatSegment(stream.Position, checked((int)chunkLength)));
            }
            else
            {
                if (chunkLength > MaximumNonImageChunkBytes - nonImageChunkBytes)
                {
                    throw new InvalidDataException(
                        $"PNG 非图像数据累计超过 {MaximumNonImageChunkBytes / Mebibyte} MiB 校验上限（{fullPath}）。");
                }

                nonImageChunkBytes += chunkLength;
                if (sawImageData)
                {
                    sawChunkAfterImageData = true;
                }
            }

            var crc = UpdateCrc(0xFFFFFFFFu, chunkTypeBytes);
            var remaining = (long)chunkLength;
            var headerData = chunkType == "IHDR" ? new byte[13] : null;
            var headerOffset = 0;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, count);
                if (read <= 0)
                {
                    throw new InvalidDataException($"PNG 数据块 {chunkType} 被截断（{fullPath}）。");
                }

                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                if (headerData is not null)
                {
                    buffer.AsSpan(0, read).CopyTo(headerData.AsSpan(headerOffset));
                    headerOffset += read;
                }

                remaining -= read;
            }

            var storedCrcBytes = storedCrcBuffer.AsSpan();
            ReadExactly(stream, storedCrcBytes, fullPath);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(storedCrcBytes);
            var calculatedCrc = crc ^ 0xFFFFFFFFu;
            if (storedCrc != calculatedCrc)
            {
                throw new InvalidDataException($"PNG 数据块 {chunkType} 的 CRC 无效（{fullPath}）。");
            }

            switch (chunkType)
            {
                case "IHDR":
                    if (headerData is null)
                    {
                        throw new InvalidDataException($"PNG IHDR 数据块重复或位置无效（{fullPath}）。");
                    }

                    header = ValidateHeader(
                        headerData,
                        constraints.Output.Width,
                        constraints.Output.Height,
                        fullPath);
                    sawHeader = true;
                    break;

                case "IDAT":
                    sawImageData = true;
                    break;

                case "IEND":
                    if (chunkLength != 0 || !sawHeader || !sawImageData || imageDataBytes <= 0)
                    {
                        throw new InvalidDataException($"PNG IEND 或图像数据结构无效（{fullPath}）。");
                    }

                    sawEnd = true;
                    break;
            }

            firstChunk = false;
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException($"PNG IEND 后存在尾随数据（{fullPath}）。");
        }

        ValidateDecodedPixels(
            fullPath,
            constraints,
            header ?? throw new InvalidDataException($"PNG 缺少有效 IHDR（{fullPath}）。"),
            idatSegments,
            geometry);
    }

    private static PngHeader ValidateHeader(
        ReadOnlySpan<byte> header,
        int expectedWidth,
        int expectedHeight,
        string fullPath)
    {
        if (expectedWidth <= 0 || expectedHeight <= 0)
        {
            throw new InvalidDataException(
                $"生成任务的 PNG 画布尺寸必须为正数（{fullPath}）。");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        if (width != (uint)expectedWidth || height != (uint)expectedHeight)
        {
            throw new InvalidDataException(
                $"PNG 尺寸不匹配：任务要求 {expectedWidth}×{expectedHeight}，文件为 {width}×{height}（{fullPath}）。");
        }

        var bitDepth = header[8];
        var colorType = header[9];
        var compressionMethod = header[10];
        var filterMethod = header[11];
        var interlaceMethod = header[12];
        if (colorType is not (4 or 6) || bitDepth is not (8 or 16))
        {
            throw new InvalidDataException(
                $"PNG 必须使用 color type 4 或 6 的真实 Alpha 通道，且位深为 8 或 16（{fullPath}）。");
        }

        if (compressionMethod != 0 || filterMethod != 0 || interlaceMethod != 0)
        {
            throw new InvalidDataException(
                $"PNG 必须使用标准压缩/过滤且当前仅接受非隔行扫描，以便完整校验像素（{fullPath}）。");
        }

        var channels = colorType == 4 ? 2 : 4;
        var bytesPerSample = bitDepth / 8;
        var bytesPerPixel = checked(channels * bytesPerSample);
        long rowBytes;
        long expectedDecodedBytes;
        try
        {
            rowBytes = checked((long)expectedWidth * bytesPerPixel);
            expectedDecodedBytes = checked((rowBytes + 1L) * expectedHeight);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"PNG 解压像素数据过大，超过校验上限（{fullPath}）。",
                exception);
        }

        if (rowBytes > int.MaxValue || expectedDecodedBytes > MaximumDecodedImageBytes)
        {
            throw new InvalidDataException(
                $"PNG 解压像素数据过大，超过 {MaximumDecodedImageBytes / Mebibyte} MiB 校验上限（{fullPath}）。");
        }

        var compressedOverhead = Math.Max(MinimumCompressedImageBudget, expectedDecodedBytes / 16L);
        var maximumCompressedImageBytes = checked(expectedDecodedBytes + compressedOverhead);
        return new PngHeader(
            expectedWidth,
            expectedHeight,
            bitDepth,
            colorType,
            bytesPerPixel,
            checked((int)rowBytes),
            expectedDecodedBytes,
            maximumCompressedImageBytes);
    }

    private static void ValidateDecodedPixels(
        string fullPath,
        GenerationConstraintPackage constraints,
        PngHeader header,
        IReadOnlyList<IdatSegment> idatSegments,
        TilemapAlphaGeometry? geometry)
    {
        if (idatSegments.Count == 0)
        {
            throw new InvalidDataException($"PNG 缺少 IDAT 像素数据（{fullPath}）。");
        }

        var previousRow = new byte[header.RowBytes];
        var encodedRow = new byte[header.RowBytes];
        var decodedRow = new byte[header.RowBytes];
        var sawFullyTransparentPixel = false;
        var sawNonTransparentTilemapPixel = false;
        var requiredCornerCoverage = geometry?.RequiresAxisAlignedSquareCornerCoverage == true
            ? new bool[4]
            : null;
        var cornerAnchorWidth = Math.Max(1, header.Width / 8);
        var cornerAnchorHeight = Math.Max(1, header.Height / 8);
        long enforcedOutsidePixelCount = 0;
        long decodedByteCount = 0;
        var decodedReadBudget = checked(header.ExpectedDecodedBytes + 1L);

        try
        {
            using var idat = new IdatSegmentStream(fullPath, idatSegments);
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: false);
            for (var y = 0; y < header.Height; y++)
            {
                var filterType = ReadDecodedByte(
                    zlib,
                    ref decodedByteCount,
                    decodedReadBudget,
                    fullPath);
                if (filterType < 0)
                {
                    throw new InvalidDataException(
                        $"PNG IDAT 解压数据不足：缺少第 {y + 1} 行过滤器（{fullPath}）。");
                }

                if (filterType > 4)
                {
                    throw new InvalidDataException(
                        $"PNG 第 {y + 1} 行使用了无效过滤器 {filterType}（{fullPath}）。");
                }

                ReadDecodedExactly(
                    zlib,
                    encodedRow,
                    ref decodedByteCount,
                    decodedReadBudget,
                    fullPath);
                UnfilterRow((byte)filterType, encodedRow, decodedRow, previousRow, header.BytesPerPixel);

                for (var x = 0; x < header.Width; x++)
                {
                    var alphaIsZero = IsAlphaZero(decodedRow, x, header);
                    sawFullyTransparentPixel |= alphaIsZero;
                    if (geometry is not null)
                    {
                        var outlineDistance = geometry.GetOutlineDistance(x, y);
                        if (outlineDistance <= 1d && !alphaIsZero)
                        {
                            sawNonTransparentTilemapPixel = true;
                            if (requiredCornerCoverage is not null)
                            {
                                requiredCornerCoverage[0] |=
                                    x < cornerAnchorWidth && y < cornerAnchorHeight;
                                requiredCornerCoverage[1] |=
                                    x >= header.Width - cornerAnchorWidth && y < cornerAnchorHeight;
                                requiredCornerCoverage[2] |=
                                    x < cornerAnchorWidth && y >= header.Height - cornerAnchorHeight;
                                requiredCornerCoverage[3] |=
                                    x >= header.Width - cornerAnchorWidth &&
                                    y >= header.Height - cornerAnchorHeight;
                            }
                        }

                        if (geometry.IsEnforcedOutside(outlineDistance))
                        {
                            enforcedOutsidePixelCount++;
                            if (!alphaIsZero)
                            {
                                throw new InvalidDataException(
                                    $"PNG 瓦片有效轮廓外像素必须为 Alpha=0，检测到 ({x},{y}) 不透明（{fullPath}）。");
                            }
                        }
                    }
                }

                (previousRow, decodedRow) = (decodedRow, previousRow);
            }

            if (decodedByteCount != header.ExpectedDecodedBytes)
            {
                throw new InvalidDataException(
                    $"PNG IDAT 解压字节数与 {header.Width}×{header.Height} 画布不一致（{fullPath}）。");
            }

            if (ReadDecodedByte(
                    zlib,
                    ref decodedByteCount,
                    decodedReadBudget,
                    fullPath) != -1)
            {
                throw new InvalidDataException(
                    $"PNG IDAT 解压后包含超过 {header.Width}×{header.Height} 画布的额外像素数据（{fullPath}）。");
            }
        }
        catch (InvalidDataException exception) when (!exception.Message.Contains(fullPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"PNG IDAT 无法完整解码（{fullPath}）：{exception.Message}", exception);
        }

        if (geometry is not null && !sawNonTransparentTilemapPixel)
        {
            throw new InvalidDataException(
                $"Tilemap PNG 在瓦片有效轮廓内全透明，缺少可交付的有效内容（{fullPath}）。");
        }

        if (requiredCornerCoverage is not null && requiredCornerCoverage.Any(covered => !covered))
        {
            throw new InvalidDataException(
                "Tilemap 90° 控制角且旋转 0° 时必须生成画布轴对齐正方形；" +
                $"检测到方形角部缺少内容，产物可能被错误旋转成 45° 钻石形（{fullPath}）。");
        }

        if (constraints.Output.TransparentBackground &&
            !sawFullyTransparentPixel &&
            (geometry is null || enforcedOutsidePixelCount > 0))
        {
            throw new InvalidDataException(
                $"PNG 声明透明背景，但解码后没有任何 Alpha=0 像素（{fullPath}）。");
        }
    }

    private static void UnfilterRow(
        byte filterType,
        ReadOnlySpan<byte> encoded,
        Span<byte> decoded,
        ReadOnlySpan<byte> previous,
        int bytesPerPixel)
    {
        for (var index = 0; index < encoded.Length; index++)
        {
            var left = index >= bytesPerPixel ? decoded[index - bytesPerPixel] : (byte)0;
            var up = previous[index];
            var upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : (byte)0;
            var predictor = filterType switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upLeft),
                _ => throw new InvalidDataException($"PNG 使用了无效过滤器 {filterType}。")
            };
            decoded[index] = unchecked((byte)(encoded[index] + predictor));
        }
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upLeftDistance = Math.Abs(estimate - upLeft);
        return leftDistance <= upDistance && leftDistance <= upLeftDistance
            ? left
            : upDistance <= upLeftDistance
                ? up
                : upLeft;
    }

    private static bool IsAlphaZero(ReadOnlySpan<byte> row, int x, PngHeader header)
    {
        var pixelOffset = checked(x * header.BytesPerPixel);
        var alphaOffset = header.ColorType == 4
            ? pixelOffset + header.BytesPerPixel / 2
            : pixelOffset + 3 * (header.BitDepth / 8);
        return header.BitDepth == 8
            ? row[alphaOffset] == 0
            : row[alphaOffset] == 0 && row[alphaOffset + 1] == 0;
    }

    private static TilemapAlphaGeometry? ValidateWorkflowConstraints(
        GenerationConstraintPackage constraints)
    {
        if (!string.Equals(
                constraints.WorkflowId,
                TilemapConstraintPackageBuilder.WorkflowId,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(
                constraints.WorkflowId,
                TilemapConstraintPackageBuilder.WorkflowId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Tilemap workflowId 必须精确为 '{TilemapConstraintPackageBuilder.WorkflowId}'。");
        }

        var output = constraints.Output
            ?? throw new InvalidDataException("Tilemap 任务缺少 output 输出规格。");
        var normalizedFormat = output.Format?.Trim().TrimStart('.');
        if (!string.Equals(normalizedFormat, "png", StringComparison.OrdinalIgnoreCase) ||
            !output.TransparentBackground)
        {
            throw new InvalidDataException(
                "Tilemap 输出必须为带真实透明背景的 PNG。");
        }

        var parameters = constraints.Parameters
            ?? throw new InvalidDataException("Tilemap 任务缺少 parameters 参数对象。");
        var tilemap = ReadRequiredObject(parameters, "tilemap", "parameters.tilemap");
        RequireExactString(tilemap, "gridPlane", TilemapGridPlane, "parameters.tilemap.gridPlane");

        var gridUnitSize = ReadRequiredFiniteDouble(
            tilemap,
            "gridUnitSize",
            8d,
            512d,
            "parameters.tilemap.gridUnitSize");
        var artWidth = ReadRequiredInt(
            tilemap,
            "artWidth",
            16,
            8_192,
            "parameters.tilemap.artWidth");
        var artHeight = ReadRequiredInt(
            tilemap,
            "artHeight",
            16,
            8_192,
            "parameters.tilemap.artHeight");
        var diamondAngle = ReadRequiredFiniteDouble(
            tilemap,
            "diamondAngle",
            15d,
            165d,
            "parameters.tilemap.diamondAngle");
        var rotationAngle = ReadRequiredFiniteDouble(
            tilemap,
            "rotationAngle",
            -180d,
            180d,
            "parameters.tilemap.rotationAngle");
        var rotationConventionValue = ReadRequiredString(
            tilemap,
            "rotationConvention",
            "parameters.tilemap.rotationConvention");
        if (!TilemapGeometryCalculator.TryParseRotationConvention(
                rotationConventionValue,
                out var rotationConvention))
        {
            throw new InvalidDataException(
                "Tilemap 参数 parameters.tilemap.rotationConvention 不是受支持的契约值；" +
                $"必须为 '{TilemapGeometryCalculator.AxisAAlignedRotationConventionId}' 或 " +
                $"'{TilemapGeometryCalculator.LegacyDiamondAxisAlignedRotationConventionId}'。");
        }

        if (artWidth != output.Width || artHeight != output.Height)
        {
            throw new InvalidDataException(
                $"Tilemap artWidth/artHeight 必须与 output.width/output.height 一致；参数为 " +
                $"{artWidth}×{artHeight}，输出为 {output.Width}×{output.Height}。");
        }

        var expectedMetrics = TilemapConstraintPackageBuilder.CalculateMetrics(
            new TilemapGenerationConstraintInput
            {
                GridUnitSize = gridUnitSize,
                ArtWidth = artWidth,
                ArtHeight = artHeight,
                DiamondAngle = diamondAngle,
                RotationAngle = rotationAngle
            },
            rotationConvention);
        RequireApproximately(
            tilemap,
            "projectedTileWidth",
            expectedMetrics.ProjectedTileWidth,
            "parameters.tilemap.projectedTileWidth");
        RequireApproximately(
            tilemap,
            "projectedTileHeight",
            expectedMetrics.ProjectedTileHeight,
            "parameters.tilemap.projectedTileHeight");
        RequireApproximately(
            tilemap,
            "rotatedFootprintWidth",
            expectedMetrics.RotatedFootprintWidth,
            "parameters.tilemap.rotatedFootprintWidth");
        RequireApproximately(
            tilemap,
            "rotatedFootprintHeight",
            expectedMetrics.RotatedFootprintHeight,
            "parameters.tilemap.rotatedFootprintHeight");
        RequireApproximately(
            tilemap,
            "pixelDensityX",
            expectedMetrics.PixelDensityX,
            "parameters.tilemap.pixelDensityX");
        RequireApproximately(
            tilemap,
            "pixelDensityY",
            expectedMetrics.PixelDensityY,
            "parameters.tilemap.pixelDensityY");
        RequireExactString(
            tilemap,
            "imageAspectRatio",
            expectedMetrics.ImageAspectRatio,
            "parameters.tilemap.imageAspectRatio");

        var axisA = ReadRequiredObject(tilemap, "axisA", "parameters.tilemap.axisA");
        RequireApproximately(axisA, "x", expectedMetrics.AxisAX, "parameters.tilemap.axisA.x");
        RequireApproximately(axisA, "y", expectedMetrics.AxisAY, "parameters.tilemap.axisA.y");
        var axisB = ReadRequiredObject(tilemap, "axisB", "parameters.tilemap.axisB");
        RequireApproximately(axisB, "x", expectedMetrics.AxisBX, "parameters.tilemap.axisB.x");
        RequireApproximately(axisB, "y", expectedMetrics.AxisBY, "parameters.tilemap.axisB.y");

        var background = ReadRequiredObject(parameters, "background", "parameters.background");
        ValidateTilemapBackgroundContract(background, output);

        return BuildTilemapAlphaGeometry(
            artWidth,
            artHeight,
            diamondAngle,
            rotationAngle,
            rotationConvention);
    }

    private static void ValidateTilemapBackgroundContract(
        JsonObject background,
        GenerationOutputSpec output)
    {
        var hasGenerationStage = background.ContainsKey("generationStage");
        var hasFinalStage = background.ContainsKey("finalStage");
        if (hasGenerationStage || hasFinalStage)
        {
            var generationStage = ReadRequiredObject(
                background,
                "generationStage",
                "parameters.background.generationStage");
            var finalStage = ReadRequiredObject(
                background,
                "finalStage",
                "parameters.background.finalStage");

            RequireExactString(
                finalStage,
                "mode",
                TilemapConstraintPackageBuilder.TransparentAlphaBackgroundMode,
                "parameters.background.finalStage.mode");
            var finalOutsideDiamondAlpha = ReadRequiredInt(
                finalStage,
                "outsideDiamondAlpha",
                0,
                0,
                "parameters.background.finalStage.outsideDiamondAlpha");
            if (finalOutsideDiamondAlpha != 0)
            {
                throw new InvalidDataException(
                    "Tilemap 参数 parameters.background.finalStage.outsideDiamondAlpha 必须为 0。");
            }

            if (output.BackgroundRemoval is null)
            {
                RequireExactString(
                    generationStage,
                    "mode",
                    TilemapConstraintPackageBuilder.TransparentAlphaBackgroundMode,
                    "parameters.background.generationStage.mode");
                var generationOutsideDiamondAlpha = ReadRequiredInt(
                    generationStage,
                    "outsideDiamondAlpha",
                    0,
                    0,
                    "parameters.background.generationStage.outsideDiamondAlpha");
                if (generationOutsideDiamondAlpha != 0)
                {
                    throw new InvalidDataException(
                        "Tilemap 参数 parameters.background.generationStage.outsideDiamondAlpha 必须为 0。");
                }

                return;
            }

            RequireExactString(
                generationStage,
                "mode",
                TilemapConstraintPackageBuilder.SolidOpaqueColorBackgroundMode,
                "parameters.background.generationStage.mode");
            RequireExactString(
                generationStage,
                "colorHex",
                output.BackgroundRemoval.ColorHex,
                "parameters.background.generationStage.colorHex");
            var generationAlpha = ReadRequiredInt(
                generationStage,
                "alpha",
                255,
                255,
                "parameters.background.generationStage.alpha");
            if (generationAlpha != 255)
            {
                throw new InvalidDataException(
                    "Tilemap 参数 parameters.background.generationStage.alpha 必须为 255。");
            }

            return;
        }

        // Backward compatibility for persisted v1 Tilemap packages created before the
        // generationStage/finalStage contract made the two phases explicit.
        if (!TryReadBoolean(background, "transparent", out var transparent) || !transparent)
        {
            throw new InvalidDataException(
                "Tilemap 参数 parameters.background.transparent 必须为 true。");
        }

        var outsideDiamondAlpha = ReadRequiredInt(
            background,
            "outsideDiamondAlpha",
            0,
            0,
            "parameters.background.outsideDiamondAlpha");
        if (outsideDiamondAlpha != 0)
        {
            throw new InvalidDataException(
                "Tilemap 参数 parameters.background.outsideDiamondAlpha 必须为 0。");
        }
    }

    private static TilemapAlphaGeometry BuildTilemapAlphaGeometry(
        int width,
        int height,
        double diamondAngle,
        double rotationAngle,
        TilemapRotationConvention rotationConvention)
    {
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
            Rotate(-halfWidth, 0d, cosine, sine),
            Rotate(0d, -halfHeight, cosine, sine),
            Rotate(halfWidth, 0d, cosine, sine),
            Rotate(0d, halfHeight, cosine, sine)
        };
        var minimumX = vertices.Min(vertex => vertex.X);
        var maximumX = vertices.Max(vertex => vertex.X);
        var minimumY = vertices.Min(vertex => vertex.Y);
        var maximumY = vertices.Max(vertex => vertex.Y);
        var spanX = maximumX - minimumX;
        var spanY = maximumY - minimumY;
        if (!(spanX > 0d) || !(spanY > 0d))
        {
            throw new InvalidDataException("Tilemap 瓦片几何无法形成有效包围盒。");
        }

        var pixelStepX = spanX / width;
        var pixelStepY = spanY / height;
        var boundaryMargin = 2d *
            ((Math.Abs(pixelStepX * cosine) + Math.Abs(pixelStepY * sine)) / halfWidth +
             (Math.Abs(pixelStepX * sine) + Math.Abs(pixelStepY * cosine)) / halfHeight);
        return new TilemapAlphaGeometry(
            width,
            height,
            halfWidth,
            halfHeight,
            cosine,
            sine,
            minimumX,
            minimumY,
            spanX,
            spanY,
            boundaryMargin,
            rotationConvention == TilemapRotationConvention.AxisAAligned &&
            Math.Abs(diamondAngle - 90d) <= 0.000001d &&
            Math.Abs(rotationAngle) <= 0.000001d);
    }

    private static JsonObject ReadRequiredObject(
        JsonObject node,
        string name,
        string path)
    {
        if (node[name] is JsonObject value)
        {
            return value;
        }

        throw new InvalidDataException($"Tilemap 参数 {path} 缺失或类型错误。");
    }

    private static double ReadRequiredFiniteDouble(
        JsonObject node,
        string name,
        double minimum,
        double maximum,
        string path)
    {
        if (node[name] is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<double>(out var value) ||
            !double.IsFinite(value))
        {
            throw new InvalidDataException($"Tilemap 参数 {path} 缺失、类型错误或不是有限数值。");
        }

        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Tilemap 参数 {path} 越界；必须位于 {minimum} 到 {maximum} 之间。");
        }

        return value;
    }

    private static int ReadRequiredInt(
        JsonObject node,
        string name,
        int minimum,
        int maximum,
        string path)
    {
        if (node[name] is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<int>(out var value))
        {
            throw new InvalidDataException($"Tilemap 参数 {path} 缺失或不是整数。");
        }

        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Tilemap 参数 {path} 越界；必须位于 {minimum} 到 {maximum} 之间。");
        }

        return value;
    }

    private static void RequireExactString(
        JsonObject node,
        string name,
        string expected,
        string path)
    {
        if (node[name] is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var value) ||
            !string.Equals(value, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Tilemap 参数 {path} 缺失、类型错误或不是契约值 '{expected}'。");
        }
    }

    private static string ReadRequiredString(
        JsonObject node,
        string name,
        string path)
    {
        if (node[name] is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidDataException($"Tilemap 参数 {path} 缺失、类型错误或为空。");
    }

    private static void RequireApproximately(
        JsonObject node,
        string name,
        double expected,
        string path)
    {
        var actual = ReadRequiredFiniteDouble(
            node,
            name,
            double.MinValue,
            double.MaxValue,
            path);
        var scale = Math.Max(1d, Math.Max(Math.Abs(actual), Math.Abs(expected)));
        if (Math.Abs(actual - expected) > scale * 1e-9d)
        {
            throw new InvalidDataException(
                $"Tilemap 派生几何参数 {path} 与基础参数不一致。");
        }
    }

    private static bool TryReadBoolean(JsonObject node, string name, out bool value)
    {
        value = false;
        return node[name] is JsonValue jsonValue &&
               jsonValue.TryGetValue<bool>(out value);
    }

    private static (double X, double Y) Rotate(
        double x,
        double y,
        double cosine,
        double sine) =>
        (x * cosine - y * sine, x * sine + y * cosine);

    private static int ReadDecodedByte(
        Stream stream,
        ref long decodedByteCount,
        long decodedReadBudget,
        string fullPath)
    {
        if (decodedByteCount >= decodedReadBudget)
        {
            throw new InvalidDataException(
                $"PNG IDAT 解压数据超过画布字节预算（{fullPath}）。");
        }

        var value = stream.ReadByte();
        if (value >= 0)
        {
            decodedByteCount++;
        }

        return value;
    }

    private static void ReadDecodedExactly(
        Stream stream,
        Span<byte> buffer,
        ref long decodedByteCount,
        long decodedReadBudget,
        string fullPath)
    {
        if (buffer.Length > decodedReadBudget - decodedByteCount)
        {
            throw new InvalidDataException(
                $"PNG IDAT 解压数据超过画布字节预算（{fullPath}）。");
        }

        try
        {
            stream.ReadExactly(buffer);
            decodedByteCount += buffer.Length;
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException(
                $"PNG IDAT 解压数据不足，无法填满目标画布（{fullPath}）。",
                exception);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string fullPath)
    {
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"PNG 文件被截断：{fullPath}", exception);
        }
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static bool IsAsciiChunkType(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= (byte)'A' and <= (byte)'Z') and
                not (>= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return true;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? 0xEDB88320u ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private sealed record PngHeader(
        int Width,
        int Height,
        byte BitDepth,
        byte ColorType,
        int BytesPerPixel,
        int RowBytes,
        long ExpectedDecodedBytes,
        long MaximumCompressedImageBytes);

    private readonly record struct IdatSegment(long Offset, int Length);

    private sealed record TilemapAlphaGeometry(
        int Width,
        int Height,
        double HalfWidth,
        double HalfHeight,
        double Cosine,
        double Sine,
        double MinimumX,
        double MinimumY,
        double SpanX,
        double SpanY,
        double BoundaryMargin,
        bool RequiresAxisAlignedSquareCornerCoverage)
    {
        public double GetOutlineDistance(int x, int y)
        {
            var rotatedX = MinimumX + (x + 0.5d) * SpanX / Width;
            var rotatedY = MinimumY + (y + 0.5d) * SpanY / Height;
            var localX = rotatedX * Cosine + rotatedY * Sine;
            var localY = -rotatedX * Sine + rotatedY * Cosine;
            return Math.Abs(localX) / HalfWidth + Math.Abs(localY) / HalfHeight;
        }

        public bool IsEnforcedOutside(double outlineDistance) =>
            outlineDistance > 1d + BoundaryMargin;
    }

    private sealed class IdatSegmentStream : Stream
    {
        private readonly FileStream _file;
        private readonly IReadOnlyList<IdatSegment> _segments;
        private int _segmentIndex;
        private int _offsetWithinSegment;
        private long _position;
        private readonly long _length;

        public IdatSegmentStream(string fullPath, IReadOnlyList<IdatSegment> segments)
        {
            _segments = segments;
            _length = segments.Sum(segment => (long)segment.Length);
            _file = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var totalRead = 0;
            while (!buffer.IsEmpty && _segmentIndex < _segments.Count)
            {
                var segment = _segments[_segmentIndex];
                var available = segment.Length - _offsetWithinSegment;
                if (available <= 0)
                {
                    _segmentIndex++;
                    _offsetWithinSegment = 0;
                    continue;
                }

                var count = Math.Min(buffer.Length, available);
                var expectedPosition = segment.Offset + _offsetWithinSegment;
                if (_file.Position != expectedPosition)
                {
                    _file.Position = expectedPosition;
                }

                var read = _file.Read(buffer[..count]);
                if (read <= 0)
                {
                    throw new EndOfStreamException("PNG IDAT 数据块被截断。");
                }

                buffer = buffer[read..];
                totalRead += read;
                _offsetWithinSegment += read;
                _position += read;
            }

            return totalRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
