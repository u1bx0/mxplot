using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MxPlot.Extensions.Tiff
{
    /// <summary>
    /// OME-TIFF形式のhyperstackファイルの読み書きを行うクラス
    /// signed/unsigned 16bit、8bit、32bit、floatに対応
    /// 
    /// 生成：us.anthropic.claude-sonnet-4-20250514-v1:0 
    /// から、少し改変（BigTiff対応、圧縮オプション追加）
    /// 
    /// </summary>
    /// <typeparam name="T">画像データの型（short, ushort, byte, sbyte, int, uint, float）</typeparam>
    public class OmeTiffHandlerInstance<T> where T : unmanaged
    {

        #region 書き込みメソッド

        /// <summary>
        /// Hyperstackデータを全てメモリに保持してOME-TIFFファイルに書き込み
        /// </summary>
        public void WriteHyperstack(string filename, List<T[]> imageStack,
            int width, int height, int channels, int zSlices, int timePoints,
            int fovCount = 1, //タイルデータにも対応
            double pixelSizeX = 1.0, double pixelSizeY = 1.0, double pixelSizeZ = 1.0)
        {
            var data = new HyperstackData<T>();
            data.Width = width;
            data.Height = height;
            data.ZSlices = zSlices;
            data.TimePoints = timePoints;
            data.Channels = channels;
            data.PixelSizeX = pixelSizeX;
            data.PixelSizeY = pixelSizeY;
            data.PixelSizeZ = pixelSizeZ;
            data.FovCount = fovCount;
            WriteHyperstack(filename, imageStack.AsEnumerable(), data);
        }

        /// <summary>
        /// Hyperstackデータを遅延処理でOME-TIFFファイルに書き込み（メモリ効率版）
        /// </summary>
        public void WriteHyperstack(string filename, IEnumerable<T[]> imageFrames,
            HyperstackData<T> data,
            OmeTiffOptions? options = null, string? customParameters = null, IProgress<int>? progress = null)
        {

            int width = data.Width;
            int height = data.Height;
            int channels = data.Channels;
            int zSlices = data.ZSlices;
            int timePoints = data.TimePoints;
            double pixelSizeX = data.PixelSizeX;
            double pixelSizeY = data.PixelSizeY;
            double pixelSizeZ = data.PixelSizeZ;
            if (channels <= 0 || zSlices <= 0 || timePoints <= 0)
                throw new ArgumentException("チャンネル、Zスライス、タイムポイントは1以上である必要があります");

            // オプションがnullならデフォルト値を使用
            options = options ?? new OmeTiffOptions();
            int expectedFrames = channels * zSlices * timePoints * Math.Max(1, data.FovCount);
            //サイズの見積もり
            var pixels = width * height * expectedFrames;
            if(pixels > 4L*1024*1024*1024/GetBytesPerPixel())
            {
                //4GBを超える場合はBigTIFFを強制
                //ただし、圧縮が掛かる場合は必ずしも超えないとは限らない
                options.UseBigTiff = true;
            }
            // BigTIFFモードの判定 ("w" = 通常, "w8" = BigTIFF)
            string mode = options.UseBigTiff ? "w8" : "w";
            
            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, mode))
            {
                if (tiff == null) throw new IOException("Tiffファイルを作成できませんでした。");

                var omeXml = CreateOmeXml(data, customParameters);
                //omeXml = omeXml.Replace("\r", "").Replace("\n", ""); //1行にする
                Debug.WriteLine("[OMETiffHandler] XML: " + omeXml);

                progress?.Report(0);
                int frameIndex = 0;
                foreach (var frame in imageFrames)
                {
                    if (frameIndex >= expectedFrames) break;

                    // frameIndexとoptionsを渡すように修正
                    WriteFrameSafe(tiff, frame, width, height, frameIndex, expectedFrames, omeXml, options);
                    progress?.Report(frameIndex);
                    frameIndex++;
                }
                tiff.Flush();
                progress?.Report(expectedFrames);
            }
        }

        private void WriteFrameSafe(BitMiracle.LibTiff.Classic.Tiff tiff, T[] data, int width, int height, int frameIndex, int totalFrames, string omeXml, OmeTiffOptions options)
        {

            try
            {
                // タグ設定メソッドにoptionsを渡す
                SetBasicTiffTags(tiff, width, height, options);

                // 最初のフレームにのみOME-XMLを設定
                if (frameIndex == 0)
                {
                    tiff.SetField(TiffTag.IMAGEDESCRIPTION, omeXml);
                }
                
                WriteImageData(tiff, data, width, height);

                if (frameIndex < totalFrames - 1)
                {
                    tiff.WriteDirectory();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"フレーム {frameIndex} の処理中にエラーが発生しました: {ex.Message}", ex);
            }
        }
               

        private void SetBasicTiffTags(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, OmeTiffOptions options)
        {
            var sampleFormat = GetSampleFormat();
            tiff.SetField(TiffTag.IMAGEWIDTH, width);
            tiff.SetField(TiffTag.IMAGELENGTH, height);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.BITSPERSAMPLE, GetBitsPerSample());
            tiff.SetField(TiffTag.SAMPLEFORMAT, sampleFormat);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            //tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);

            // --- 圧縮設定の適用 ---
            tiff.SetField(TiffTag.COMPRESSION, options.Compression);

            // LZW または Deflate (Zip) の場合、Predictorを設定するとサイズが小さくなる
            if (options.Compression == Compression.LZW || options.Compression == Compression.ADOBE_DEFLATE)
            {
                //tiff.SetField(TiffTag.PREDICTOR, options.Predictor);
                // 浮動小数点のときは、事故を防ぐために予測子を使わない
                if (sampleFormat == SampleFormat.IEEEFP)
                {
                    tiff.SetField(TiffTag.PREDICTOR, Predictor.NONE);
                }
                else
                {
                    // 整数型なら Horizontal (2) を使う
                    tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);
                }

            }
            // -------------------
            // RowsPerStrip を適切に設定
            int rowsPerStrip = CalculateOptimalRowsPerStrip(width, height);
            tiff.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);
        }

        private int CalculateOptimalRowsPerStrip(int width, int height)
        {
            int bytesPerRow = width * GetBytesPerPixel();

            // 目標：1ストリップあたり64KB程度
            const int targetStripSize = 64 * 1024;
            int optimalRows = Math.Max(1, targetStripSize / bytesPerRow);

            // 画像の高さを超えないように調整
            return Math.Min(optimalRows, height);
        }

        private void WriteImageData(BitMiracle.LibTiff.Classic.Tiff tiff, T[] data, int width, int height)
        {
            // ジェネリック型 T のサイズを取得
            int typeSize = Marshal.SizeOf(typeof(T));
            int stride = width * typeSize;
            byte[] buffer = new byte[stride];

            // ここで変換用スパンを作成（C# 7.2以降、あるいはSystem.Memory参照）
            // 配列全体をSpanとして扱う
            Span<byte> sourceBytes = MemoryMarshal.AsBytes(data.AsSpan());

            for (int row = 0; row < height; row++)
            {
                // 1行分のバイトデータをバッファにコピー
                // data配列の該当位置から切り出す
                var rowSlice = sourceBytes.Slice(row * stride, stride);
                rowSlice.CopyTo(buffer);

                // ★重要: TIFF側のエンディアン設定に合わせてスワップが必要ならここでやるべきだが、
                // LibTiff.Netは通常、ネイティブオーダーで渡せばヘッダーに合わせて変換してくれる。
                // ただし BlockCopy で渡していた前回のコードは危険だった。

                if (!tiff.WriteScanline(buffer, row, 0))
                {
                    throw new InvalidOperationException($"行 {row} の書き込みに失敗しました");
                }
            }
        }

        #endregion

        #region 読み込みメソッド

        /// <summary>
        /// OME-TIFFファイルを全てメモリに読み込み
        /// </summary>
        public HyperstackData<T> ReadHyperstack(string filename, IProgress<int>? progress = null)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            var data = new HyperstackData<T>();

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                ReadOmeMetadata(tiff, data);
                ReadImageData(tiff, data, progress);
            }

            return data;
        }

        /// <summary>
        /// OME-TIFFファイルを遅延読み込み（メモリ効率版）
        /// </summary>
        public IEnumerable<T[]> ReadFramesLazy(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                int totalDirectories = tiff.NumberOfDirectories();
                var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

                for (int directory = 0; directory < totalDirectories; directory++)
                {
                    tiff.SetDirectory((short)directory);
                    yield return ReadSingleFrame(tiff, width, height);
                }
            }
        }

        /// <summary>
        /// 特定フレームのみを読み込み
        /// </summary>
        public T[] ReadSingleFrameAt(string filename, int frameIndex)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                if (frameIndex >= tiff.NumberOfDirectories())
                    throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Out of bounds:  frameIndex= {frameIndex}");

                tiff.SetDirectory((short)frameIndex);
                var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

                return ReadSingleFrame(tiff, width, height);
            }
        }

        /// <summary>
        /// メタデータのみを読み込み
        /// </summary>
        public HyperstackMetadata ReadMetadata(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            var data = new HyperstackMetadata();

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                ReadOmeMetadata(tiff, data);
            }

            return data;
        }

        private void ReadOmeMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackMetadata data)
        {
            var imageDescription = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
            if (imageDescription != null && imageDescription.Length > 0)
            {
                string omeXml = imageDescription[0].ToString();
                ParseOmeXml(omeXml, data);
                data.OMEXml = omeXml;

                ReadCoordinateSystemAnnotation(omeXml, data);

                ReadCustomParameters(omeXml, data);
            }
            else
            {
                ReadBasicTiffInfo(tiff, data);
            }
        }

        private void ParseOmeXml(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if(doc == null)
                    throw new InvalidDataException("Invalid OME-XML format.");
                var ns = doc.Root?.GetDefaultNamespace() ?? null;
                if(ns == null)
                    throw new InvalidDataException("No namespace found in OME-XML.");

                // 1. Image要素を全て取得 (これがタイル数に対応)
                var images = doc.Descendants(ns + "Image").ToList();

                if (images.Count == 0)
                    throw new InvalidDataException("No Image elements found in OME-XML.");

                // 2. FOV数の設定
                data.FovCount = images.Count;
                data.GlobalOrigins = new GlobalPoint[data.FovCount];

                // 3.最初のImage要素を使って、全体の共通プロパティ（画像サイズ、物理サイズなど）を読み込む
                // ※通常、同一ファイル内のタイル画像のサイズや物理単位は共通であるという前提です。
                var pixels = images[0].Descendants(ns + "Pixels").FirstOrDefault();
                if (pixels != null)
                {
                    data.Width = int.Parse(pixels.Attribute("SizeX")?.Value ?? "0");
                    data.Height = int.Parse(pixels.Attribute("SizeY")?.Value ?? "0");
                    data.Channels = int.Parse(pixels.Attribute("SizeC")?.Value ?? "1");
                    data.ZSlices = int.Parse(pixels.Attribute("SizeZ")?.Value ?? "1");
                    data.TimePoints = int.Parse(pixels.Attribute("SizeT")?.Value ?? "1");
                    data.DimensionOrder = pixels.Attribute("DimensionOrder")?.Value ?? "XYCZT";

                    // ピクセルサイズ
                    if (double.TryParse(pixels.Attribute("PhysicalSizeX")?.Value, out double psx))
                        data.PixelSizeX = psx;
                    if (double.TryParse(pixels.Attribute("PhysicalSizeY")?.Value, out double psy))
                        data.PixelSizeY = psy;
                    if (double.TryParse(pixels.Attribute("PhysicalSizeZ")?.Value, out double psz))
                        data.PixelSizeZ = psz;

                    // 単位
                    data.UnitX = pixels.Attribute("PhysicalSizeXUnit")?.Value ?? "µm";
                    data.UnitY = pixels.Attribute("PhysicalSizeYUnit")?.Value ?? "µm";
                    data.UnitZ = pixels.Attribute("PhysicalSizeZUnit")?.Value ?? "µm";
                }

                //最初のimageでDeltaTの平均値とDeltaTUnitを取得
                if (pixels != null && data.TimePoints > 1)
                {
                    string targetZ = "0";
                    string targetC = "0";

                    // 3. 最初のフレーム (T=0) のDeltaTを取得
                    // LINQの FirstOrDefault で条件に合うものを1つだけ検索（全走査より高速）
                    var firstPlane = pixels.Descendants(ns + "Plane")
                        .FirstOrDefault(p =>
                            p.Attribute("TheT")?.Value == "0" &&
                            p.Attribute("TheZ")?.Value == targetZ &&
                            p.Attribute("TheC")?.Value == targetC);

                    // 4. 最後のフレーム (T=SizeT-1) のDeltaTを取得
                    string lastTIndex = (data.TimePoints - 1).ToString();
                    var lastPlane = pixels.Descendants(ns + "Plane")
                        .FirstOrDefault(p =>
                            p.Attribute("TheT")?.Value == lastTIndex &&
                            p.Attribute("TheZ")?.Value == targetZ &&
                            p.Attribute("TheC")?.Value == targetC);

                    // 両方見つかった場合のみ計算
                    if (firstPlane != null && lastPlane != null)
                    { 
                        // パース (失敗時は0になるが、nullチェック済みなので概ね安全)
                        double.TryParse(firstPlane.Attribute("DeltaT")?.Value, out double tStart);
                        double.TryParse(lastPlane.Attribute("DeltaT")?.Value, out double tEnd);

                        // 平均間隔 = (終了時間 - 開始時間) / (ステップ数)
                        // ステップ数は "フレーム数 - 1"
                        data.TimeStep  = (tEnd - tStart) / (data.TimePoints - 1);
                        data.StartTime = tStart;
                        //単位があればそれも取得
                        data.UnitTime = firstPlane.Attribute("DeltaTUnit")?.Value ?? "s";
                    }
                }
                else
                {
                    // TimePointsが1の場合は間隔なし
                    data.TimeStep = 0;
                }

                var fovOrigins = new List<GlobalPoint>();

                // 中心座標からOrigin(左上/左下)へ戻すためのオフセット
                double halfWidth = (data.Width - 1) * data.PixelSizeX * 0.5; //PixelSizeXは(Max - Min)  / (Num - 1)で考える
                double halfHeight = (data.Height - 1) * data.PixelSizeY * 0.5;

                foreach (var img in images)
                {
                    var thePixels = img.Descendants(ns + "Pixels").FirstOrDefault();
                    if (thePixels == null)
                    {
                        fovOrigins.Add(new GlobalPoint(0, 0, 0));
                        continue;
                    }

                    // 最初のPlaneを取得
                    var firstPlane = thePixels.Descendants(ns + "Plane").FirstOrDefault();

                    //原点座標
                    double originX = 0;
                    double originY = 0;
                    double originZ = 0;

                    if (firstPlane != null)
                    {
                        // PositionX, Y (中心座標) を取得
                        if (double.TryParse(firstPlane.Attribute("PositionX")?.Value, out double px))
                        {
                            // Center -> Origin (Left)
                            originX = px - halfWidth;
                        }

                        if (double.TryParse(firstPlane.Attribute("PositionY")?.Value, out double py))
                        {
                            //もしY軸を反転させたい場合はデータを取得後に行うので、ここでは左上を原点として考える
                            originY = py - halfHeight;
                        }

                        // Z座標 (最初のスライスの位置)
                        if (double.TryParse(firstPlane.Attribute("PositionZ")?.Value, out double pz))
                        {
                            originZ = pz; //StartZに相当する
                        }
                    }

                    fovOrigins.Add(new GlobalPoint(originX, originY, originZ)); //実際には2次元タイルしか考えないのでorizinZは使われない
                }

                // 解析結果をメタデータに格納
                data.GlobalOrigins = fovOrigins.ToArray();
                data.StartZ = fovOrigins[0].Z; //Zは共通なので、最初のフレームのZを入れておく全体のZ原点も設定 
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OME-XML parse error: {ex.Message}");
                // フォールバック処理は行わない（エラーを上位に伝播）
                throw new InvalidDataException($"Unable to parse OME-XML: {ex.Message}", ex);
            }
        }

        private void ReadBasicTiffInfo(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackMetadata data)
        {
            var width = tiff.GetField(TiffTag.IMAGEWIDTH);
            var height = tiff.GetField(TiffTag.IMAGELENGTH);

            data.Width = width?[0].ToInt() ?? 0;
            data.Height = height?[0].ToInt() ?? 0;
            data.Channels = 1;
            data.ZSlices = 1;
            data.TimePoints = tiff.NumberOfDirectories();
        }

        
        private void ReadCustomParameters(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if(doc.Root == null)
                    return;
                // 1. ルート要素を取得
                XElement root = doc.Root;
                if (root == null) 
                    return;

                // 2. 名前空間なしの "CustomParameters" 要素を探す
                // XML内で xmlns="" となっているため、XNamespace.None を指定するか、名前だけで検索します
                XElement? customParams = root.Element(XNamespace.None + "CustomParameters");
                
                // 3. 値を返す（CDATAの中身が自動的に文字列として取得されます）
                var values =  customParams?.Value;
                if (values == null)
                    return;
                data.CustomParameters = values;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CoordinateSystem annotation read error: {ex.Message}");
                // エラーでも継続（オプショナル情報）
            }
        }

        private void ReadCoordinateSystemAnnotation(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                var nsSA = XNamespace.Get("http://www.openmicroscopy.org/Schemas/SA/2016-06");

                var coordAnnotation = doc.Descendants(nsSA + "XMLAnnotation")
                    .FirstOrDefault(a => a.Attribute("Namespace")?.Value?.Contains("coordinate-system") == true);

                if (coordAnnotation != null)
                {
                    var value = coordAnnotation.Element(nsSA + "Value");
                    var coordSystem = value?.Element("CoordinateSystem");
                    var position = coordSystem?.Element("IndexZeroPosition");
                    var unit = coordSystem?.Element("Unit");
                    var tileLayout = coordSystem?.Element("TileLayout");

                    if (position != null)
                    {
                        if (double.TryParse(position.Attribute("X")?.Value, out double ox))
                            data.StartX = ox;
                        if (double.TryParse(position.Attribute("Y")?.Value, out double oy))
                            data.StartY = oy;
                        if (double.TryParse(position.Attribute("Z")?.Value, out double oz))
                            data.StartZ = oz;
                    }
                    if (unit != null)
                    {
                        // 単位情報の読み取り（必要に応じてアンコメント）
                        data.UnitX = unit.Attribute("X")?.Value ?? data.UnitX;
                        data.UnitY = unit.Attribute("Y")?.Value ?? data.UnitY;
                        data.UnitZ = unit.Attribute("Z")?.Value ?? data.UnitZ;
                    }
                    if (tileLayout != null)
                    {
                        if (int.TryParse(tileLayout.Attribute("TilesX")?.Value, out int tilesX) &&
                            int.TryParse(tileLayout.Attribute("TilesY")?.Value, out int tilesY))
                        {
                            data.TileLayout = (tilesX, tilesY);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CoordinateSystem annotation read error: {ex.Message}");
                // エラーでも継続（オプショナル情報）
            }
        }

        private void ReadImageData(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackData<T> data, IProgress<int>? progress = null)
        {
            int totalFrames = data.TotalFrames;
            int actualFrames = Math.Min(totalFrames, tiff.NumberOfDirectories());
            data.ImageStack = new List<T[]>(actualFrames);

            progress?.Report(-actualFrames);
            for (int directory = 0; directory < actualFrames; directory++)
            {
                tiff.SetDirectory((short)directory);
                var frameData = ReadSingleFrame(tiff, data.Width, data.Height);
                data.ImageStack.Add(frameData);

                progress?.Report(directory);
            }
            progress?.Report(actualFrames);

        }

        private T[] ReadSingleFrame(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        {
            var imageData = new T[width * height];

            // T が ushort (16bit) かどうか確認
            bool isUshort = typeof(T) == typeof(ushort);
            int typeSize = isUshort ? 2 : Marshal.SizeOf(typeof(T));

            int stride = width * typeSize;
            byte[] buffer = new byte[stride];

            // ファイルが逆エンディアン（スワップが必要）かチェック
            bool isSwapped = tiff.IsByteSwapped();

            // 出力先の Span
            Span<byte> destBytes = MemoryMarshal.AsBytes(imageData.AsSpan());

            for (int row = 0; row < height; row++)
            {
                // 1行読み込み
                if (!tiff.ReadScanline(buffer, row, 0))
                {
                    throw new InvalidOperationException($"Failed to read the row: {row}");
                }

                // ★スワップ処理 (16bitの場合)
                if (isUshort && isSwapped)
                {
                    // バイト順を入れ替える (BigEndian <-> LittleEndian)
                    for (int i = 0; i < buffer.Length; i += 2)
                    {
                        byte temp = buffer[i];
                        buffer[i] = buffer[i + 1];
                        buffer[i + 1] = temp;
                    }
                }

                // 変換後のバイト列を imageData の所定の位置にコピー
                buffer.AsSpan().CopyTo(destBytes.Slice(row * stride, stride));
            }

            return imageData;
        }

        #endregion


        #region 型依存メソッド

        private int GetBitsPerSample()
        {
            if (typeof(T) == typeof(short) || typeof(T) == typeof(ushort))
                return 16;
            if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
                return 8;
            if (typeof(T) == typeof(int) || typeof(T) == typeof(uint))
                return 32;
            if (typeof(T) == typeof(float))
                return 32;
            if (typeof(T) == typeof(double))
                return 64;

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        private SampleFormat GetSampleFormat()
        {
            if (typeof(T) == typeof(short) || typeof(T) == typeof(sbyte) || typeof(T) == typeof(int))
                return SampleFormat.INT;
            if (typeof(T) == typeof(ushort) || typeof(T) == typeof(byte) || typeof(T) == typeof(uint))
                return SampleFormat.UINT;
            if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
                return SampleFormat.IEEEFP;

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        private int GetBytesPerPixel()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<T>();
        }

        private string GetOmePixelType()
        {
            if (typeof(T) == typeof(short)) return "int16";
            if (typeof(T) == typeof(ushort)) return "uint16";
            if (typeof(T) == typeof(byte)) return "uint8";
            if (typeof(T) == typeof(sbyte)) return "int8";
            if (typeof(T) == typeof(int)) return "int32";
            if (typeof(T) == typeof(uint)) return "uint32";
            if (typeof(T) == typeof(float)) return "float";
            if (typeof(T) == typeof(double)) return "double";

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        #endregion

        #region OME-XML生成

        private string CreateOmeXml(HyperstackMetadata data, string? customParameters = null)
        {
            XNamespace ns = "http://www.openmicroscopy.org/Schemas/OME/2016-06";
            XNamespace sa = "http://www.openmicroscopy.org/Schemas/SA/2016-06";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            // UUIDを生成（Fijiの出力に合わせてファイル固有IDを作る）
            string uuid = "urn:uuid:" + Guid.NewGuid().ToString();

            var ome = new XElement(ns + "OME",
                  new XAttribute("xmlns", ns.NamespaceName),
                  new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                  new XAttribute(xsi + "schemaLocation", $"{ns.NamespaceName} {ns.NamespaceName}/ome.xsd"),
                  new XAttribute("UUID", uuid),
                  new XAttribute("Creator", "OMETiffHandler.cs C#")
              );

            // 1つのFOVに含まれる画像の総数 (Z * C * T)
            int planesPerFov = data.ZSlices * data.Channels * data.TimePoints;

            // ★修正点: FOVの数だけ Image ノードを生成して追加
            for (int fov = 0; fov < data.FovCount; fov++)
            {
                // このFOVが始まるIFD番号 (前のFOVの枚数分だけずらす)
                int startIfd = fov * planesPerFov;
                (int tileX, int tileY) = data.GetTileIndices(fov);

                var imageNode = new XElement(ns + "Image",
                    new XAttribute("ID", $"Image:{fov}"), // Image:0, Image:1 ...
                    new XAttribute("Name", data.FovCount > 1 ? $"FOV:{fov} [{tileX},{tileY}]" : "Single FOV"), // 任意の名前
                    new XElement(ns + "Pixels",
                        new XAttribute("ID", $"Pixels:{fov}"), // Pixels:0, Pixels:1 ...
                        new XAttribute("SizeX", data.Width),
                        new XAttribute("SizeY", data.Height),
                        new XAttribute("SizeZ", data.ZSlices),
                        new XAttribute("SizeC", data.Channels),
                        new XAttribute("SizeT", data.TimePoints),
                        new XAttribute("DimensionOrder", "XYCZT"),
                        new XAttribute("Type", GetOmePixelType()),
                        new XAttribute("PhysicalSizeX", data.PixelSizeX),
                        new XAttribute("PhysicalSizeY", data.PixelSizeY),
                        new XAttribute("PhysicalSizeZ", data.PixelSizeZ),

                        // チャンネル定義 (共通)
                        CreateChannels(data.Channels, ns),

                        // ★重要: IFDの開始位置を渡す必要があります
                        // 既存のメソッドを CreateTiffData(..., startIfd) に修正してください
                        CreateTiffData(data.Channels, data.ZSlices, data.TimePoints, ns, startIfd),

                        // Plane定義 (FOVインデックスを渡して、そのFOVのGlobalPointを参照させる)
                        CreatePlanes(data, ns, fov)
                    )
                );

                // アノテーション参照 (必要であれば各Imageに追加)
                imageNode.Add(new XElement(ns + "AnnotationRef", new XAttribute("ID", "Annotation:CoordinateSystem:0")));

                // OMEルートに追加
                ome.Add(imageNode);
            }

            // StructuredAnnotations (全体で1つ、あるいは必要に応じて増やす)
            ome.Add(new XElement(sa + "StructuredAnnotations",
                 CreateCoordinateSystemAnnotation(data, sa)
            ));

            if (!string.IsNullOrEmpty(customParameters))
            {
                ome.Add(new XElement("CustomParameters", new XCData(customParameters)));
            }
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + ome.ToString();
        }

        private IEnumerable<XElement> CreateChannels(int channelCount, XNamespace ns)
        {
            for (int i = 0; i < channelCount; i++)
            {
                yield return new XElement(ns + "Channel",
                    new XAttribute("ID", $"Channel:0:{i}"),
                    new XAttribute("SamplesPerPixel", 1), //Grayscale
                    new XElement(ns + "LightPath")
                );
            }
        }

        private IEnumerable<XElement> CreateTiffData(int c, int z, int t, XNamespace ns, int ifd)
        {
            // 全フレーム数
            //int totalFrames = c * z * t;

            //int ifd = 0;

            // XYCZT順序で正確にマッピング
            for (int tIndex = 0; tIndex < t; tIndex++)
            {
                for (int zIndex = 0; zIndex < z; zIndex++)
                {
                    for (int cIndex = 0; cIndex < c; cIndex++)
                    {
                        yield return new XElement(ns + "TiffData",
                            new XAttribute("IFD", ifd),
                            new XAttribute("FirstC", cIndex),
                            new XAttribute("FirstZ", zIndex),
                            new XAttribute("FirstT", tIndex),
                            new XAttribute("PlaneCount", 1)
                        );
                        ifd++;
                    }
                }
            }

        }

        private IEnumerable<XElement> CreatePlanes(HyperstackMetadata data, XNamespace ns, int fovIndex = 0)
        {
            int t = data.TimePoints;
            int c = data.Channels;
            int z = data.ZSlices;
            //GlobalOriginは原点（左上/左下）の座標を示すので、中心座標に変換する必要がある
            var gorigin = (data.GlobalOrigins != null && fovIndex < data.GlobalOrigins.Length) ?
                data.GlobalOrigins[fovIndex] : new GlobalPoint(data.StartX, data.StartY, data.StartZ);
            //posX/Yは中心座標
            double posX = gorigin.X + (data.Width - 1) * data.PixelSizeX * 0.5;
            double posY = gorigin.Y + (data.Height - 1) * data.PixelSizeY * 0.5 ;

            for (int tIndex = 0; tIndex < t; tIndex++)
            {
                double time = data.StartTime + tIndex * data.TimeStep;
                for (int zIndex = 0; zIndex < z; zIndex++)
                {
                    //double posZ = data.StartZ + data.PixelSizeZ * zIndex;
                    double posZ = gorigin.Z + data.PixelSizeZ * zIndex; //MatrixData的には現状ではgorizin.Zはdata.StartZと同じになる
                    for (int cIndex = 0; cIndex < c; cIndex++)
                    {
                        yield return new XElement(ns + "Plane",
                            new XAttribute("TheC", cIndex),
                            new XAttribute("TheZ", zIndex),
                            new XAttribute("TheT", tIndex),
                            new XAttribute("PositionX", posX),
                            new XAttribute("PositionY", posY),
                            new XAttribute("PositionZ", posZ), 
                            new XAttribute("DeltaT", time), //OME-TIFFではDeltaTは測定開始（最初のフレーム）からの経過時間
                            new XAttribute("DeltaTUnit", data.UnitTime),
                            new XAttribute("ExposureTime", 1) 
                        );
                    }
                }
            }
        }

        /// <summary>
        /// カスタム座標系情報を追加
        /// </summary>
        private XElement CreateCoordinateSystemAnnotation(HyperstackMetadata data, XNamespace sa)
        {
            return new XElement(sa + "XMLAnnotation",
                new XAttribute("ID", "Annotation:CoordinateSystem:0"),
                new XAttribute("Namespace", "matarix-data-plotter/coordinate-system/v1"),
                new XElement(sa + "Value",
                    new XElement("CoordinateSystem",
                        new XElement("IndexZeroPosition",
                            new XAttribute("X", data.StartX),
                            new XAttribute("Y", data.StartY),
                            new XAttribute("Z", data.StartZ)
                        ),
                        new XElement("Unit",
                            new XAttribute("X", data.UnitX),
                            new XAttribute("Y", data.UnitY),
                            new XAttribute("Z", data.UnitZ)
                        ),
                        new XElement("TileLayout",
                            new XAttribute("TilesX", data.TileLayout.X),
                            new XAttribute("TilesY", data.TileLayout.Y)
                         )

                    )
                )
            );
        }
        #endregion
    }

    #region データクラス
    public class HyperstackMetadata
    {
        /// <summary>
        /// X方向の画素数
        /// </summary>
        public int Width { get; set; }
        /// <summary>
        /// Y方向の画素数
        /// </summary>
        public int Height { get; set; }
        public int Channels { get; set; }
        public int ZSlices { get; set; }
        public int TimePoints { get; set; }
        public double StartTime { get; set; } =0.0;
        /// <summary>
        /// DeltaTは各タイムポイント間の時間間隔（単位はUnitTimeに依存）の平均値
        /// </summary>
        public double TimeStep { get; set; } = 1;

        public double PixelSizeX { get; set; } = 1.0;
        public double PixelSizeY { get; set; } = 1.0;
        public double PixelSizeZ { get; set; } = 1.0;
        public string UnitX { get; set; } = "µm";
        public string UnitY { get; set; } = "µm";
        public string UnitZ { get; set; } = "µm";

        public string UnitTime { get; set; } = "s";
        

        //タイリング設定
        public int FovCount { get; set; } = 1;

        /// <summary>
        /// 2次元タイルレイアウト (X方向タイル数, Y方向タイル数)
        /// </summary>
        public (int X, int Y) TileLayout { get; set; } = (1, 1);
        /// <summary>
        /// ワールド座標（ステージ座標）における各タイル（FOV)の原点座標
        /// </summary>
        public GlobalPoint[]? GlobalOrigins { get; set; }   = null;

        /// <summary>
        /// フレームの原点X座標( = T[0]のX座標（相対値）、MatrixDataだと左下、通常は左下）
        /// </summary>
        public double StartX { get; set; } = 0.0;
        /// <summary>
        /// フレームの原点Y座標( = T[0]のY座標（相対値）、MatrixDataだと左下、通常は左下）
        /// </summary>
        public double StartY { get; set; } = 0.0;
        /// <summary>
        /// zスタックの【開始】Z座標(zindex = 0のときの絶対位置)
        /// </summary>
        public double StartZ { get; set; } = 0.0;

        /// <summary>
        /// Default XYCZT
        /// </summary>
        public string DimensionOrder { get; set; } = "XYCZT";
        public string? PixelType { get; set; }

        public string? CustomParameters { get; set; }

        public string? OMEXml { get; set; }

        /// <summary>
        /// 総フレーム数
        /// </summary>
        public int TotalFrames => Channels * ZSlices * TimePoints * FovCount;

        public (int X, int Y) GetTileIndices(int fovIndex)
        {
            int tilesX = TileLayout.X;
            int xIndex = fovIndex % tilesX;
            int yIndex = fovIndex / tilesX;
            return (xIndex, yIndex);
        }

        //内部データを上下反転させる：派生クラスで型に応じて実装
        public virtual void FlipVertical()
        {
            throw new NotSupportedException("Unable to excute FlipVertical on metadata-only class.");
        }

        public static string FormatXml(string xmlString)
        {
            // 改行があるならそのまま返す
            if (xmlString.Contains("\n") || xmlString.Contains("\r"))
                return xmlString;

            // 改行がない場合は整形
            var doc = new XmlDocument();
            doc.LoadXml(xmlString);

            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace
            };

            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }

            return sb.ToString();
        }

    }

    /// <summary>
    /// Hyperstackデータとメタデータを格納するクラス
    /// </summary>
    public class HyperstackData<T>: HyperstackMetadata where T : unmanaged
    {
        public List<T[]> ImageStack { get; set; } = new List<T[]>();

        // DataTypeプロパティをオーバーライド
        public new string PixelType => GetPixelTypeString();

        private string GetPixelTypeString()
        {
            if (typeof(T) == typeof(short)) return "int16";
            if (typeof(T) == typeof(ushort)) return "uint16";
            if (typeof(T) == typeof(byte)) return "uint8";
            if (typeof(T) == typeof(sbyte)) return "int8";
            if (typeof(T) == typeof(int)) return "int32";
            if (typeof(T) == typeof(uint)) return "uint32";
            if (typeof(T) == typeof(float)) return "float";
            if (typeof(T) == typeof(double)) return "double";
            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        /// <summary>
        /// MatrixData<typeparamref name="T"/>からHyperStackDataを生成する
        /// list==nullの場合は、SeriesDataがそのまま入るが、ソート後のデータを入れるとそれに従った並びで記録される(XYCZTが必要）
        /// </summary>
        /// <param name="md"></param>
        /// <param name="list"></param>
        /// <returns></returns>
        public static HyperstackData<T> CreateFrom(MatrixData<T> md, List<T[]>? list = null)
        {
            var data = new HyperstackData<T>();
            if (list == null)
            {
                int pageNum = md.FrameCount;
                list = new List<T[]>(pageNum);
                for (int i = 0; i < pageNum; i++)
                {
                    list.Add(md.GetArray(i));
                }
            }
            data.ImageStack = list;
            data.Width = md.XCount;
            data.Height = md.YCount;
            var dimensions = md.Dimensions;
            int cnum = dimensions.GetLength("Channel");
            int znum = dimensions.GetLength("Z");
            int tnum = dimensions.GetLength("Time");
            int fovNum = dimensions.GetLength("FOV");

            data.ZSlices = znum;
            data.TimePoints = tnum;
            data.Channels = cnum;
            data.FovCount = fovNum;

            data.PixelSizeX = md.XStep;
            data.PixelSizeY = md.YStep;
            data.PixelSizeZ = dimensions.Contains("Z") ? dimensions["Z"]!.Step : 1;

            //一応セットするが、Bio-formatだと好きな単位を入れられるわけではない
            //µm ⇒文字化けする
            data.UnitX = md.XUnit;
            data.UnitY = md.YUnit;
            data.UnitZ = dimensions.Contains("Z") ? dimensions["Z"]!.Unit : "";
            data.UnitTime = dimensions.Contains("Time") ? dimensions["Time"]!.Unit : "s";
            data.StartX = md.XMin;// md.Width * 0.5 + md.XMin;
            data.StartY = md.YMin;// md.Height * 0.5 + md.YMin;
            data.StartZ = dimensions.Contains("Z") ? dimensions["Z"]!.Min : 0;
            data.TimeStep = dimensions.Contains("Time") ? dimensions["Time"]!.Step : 1;
            data.StartTime = dimensions.Contains("Time") ? dimensions["Time"]!.Min : 0;

            if(data.FovCount > 1 && dimensions["FOV"] is FovAxis fovAxis)
            {
                var tile = fovAxis.TileLayout;
                data.TileLayout = (tile.X, tile.Y);
                data.GlobalOrigins = fovAxis.Origins;
            }

            return data;
        }


        /// <summary>
        /// データのy軸を全て上下反転する
        /// </summary>
        public override void FlipVertical()
        {
            int height = this.Height;
            int width = this.Width;
            int bytesPerPixel = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            int bytesPerRow = width * bytesPerPixel;

            // フレーム並列で処理
            Parallel.ForEach(ImageStack, frame =>
            {
                byte[] tempRow = new byte[bytesPerRow];

                for (int row = 0; row < height / 2; row++)
                {
                    int topOffset = row * width * bytesPerPixel;
                    int bottomOffset = (height - 1 - row) * width * bytesPerPixel;

                    Buffer.BlockCopy(frame, topOffset, tempRow, 0, bytesPerRow);
                    Buffer.BlockCopy(frame, bottomOffset, frame, topOffset, bytesPerRow);
                    Buffer.BlockCopy(tempRow, 0, frame, bottomOffset, bytesPerRow);
                }
            });

            if (this.FovCount > 1)
            {
                for(int i = 0; i < this.GlobalOrigins?.Length; i++)
                {
                    var origin = this.GlobalOrigins[i];
                    this.GlobalOrigins[i] = new GlobalPoint(origin.X, -origin.Y, origin.Z);
                }
            }
        }

    }

    #endregion


    #region 保存オプション
    /// <summary>
    /// 書き込みオプション
    /// </summary>
    public class OmeTiffOptions
    {
        /// <summary>
        /// trueの場合、BigTIFF形式(64bitオフセット)で保存します。
        /// 4GBを超える可能性がある場合は必須です。
        /// </summary>
        public bool UseBigTiff { get; set; } = false;

        /// <summary>
        /// 圧縮方式を指定します。
        /// </summary>
        public Compression Compression { get; set; } = Compression.LZW; // 推奨: LZW

        /// <summary>
        /// 圧縮時の予測子。LZWやDeflateの場合、Horizontal(2)にすると圧縮率が向上します。
        /// </summary>
        public Predictor Predictor { get; set; } = Predictor.HORIZONTAL;
    }
    #endregion

    #region ファクトリークラス

    /// <summary>
    /// 型安全なファクトリーパターン
    /// </summary>
    public static class OmeTiffFactory
    {
        public static OmeTiffHandlerInstance<short> CreateSigned16() => new OmeTiffHandlerInstance<short>();
        public static OmeTiffHandlerInstance<ushort> CreateUnsigned16() => new OmeTiffHandlerInstance<ushort>();
        public static OmeTiffHandlerInstance<byte> CreateUnsigned8() => new OmeTiffHandlerInstance<byte>();
        public static OmeTiffHandlerInstance<sbyte> CreateSigned8() => new OmeTiffHandlerInstance<sbyte>();
        public static OmeTiffHandlerInstance<int> CreateSigned32() => new OmeTiffHandlerInstance<int>();
        public static OmeTiffHandlerInstance<uint> CreateUnsigned32() => new OmeTiffHandlerInstance<uint>();
        public static OmeTiffHandlerInstance<float> CreateFloat32() => new OmeTiffHandlerInstance<float>();
        public static OmeTiffHandlerInstance<double> CreateFloat64() => new OmeTiffHandlerInstance<double>();
    }

    public static class OmeTiffReader
    {
        /// <summary>
        /// メタデータのみを読み込み（型不要）
        /// </summary>
        public static HyperstackMetadata ReadMetadata(string filename)
        {
            var pixelType = DetectPixelType(filename);

            dynamic handler = pixelType switch
            {
                "int16" => OmeTiffFactory.CreateSigned16(),
                "uint16" => OmeTiffFactory.CreateUnsigned16(),
                "uint8" => OmeTiffFactory.CreateUnsigned8(),
                "int8" => OmeTiffFactory.CreateSigned8(),
                "int32" => OmeTiffFactory.CreateSigned32(),
                "uint32" => OmeTiffFactory.CreateUnsigned32(),
                "float" => OmeTiffFactory.CreateFloat32(),
                "double" => OmeTiffFactory.CreateFloat64(),
                _ => throw new NotSupportedException($"Pixel type '{pixelType}' is not supported.")
            };

            var data = handler.ReadMetadata(filename);

            return data;
        }

        /// <summary>
        /// 完全なデータを読み込み（型を自動判定）
        /// </summary>
        public static object ReadHyperstackAuto(string filename, IProgress<int>? progress = null)
        {
            var pixelType = DetectPixelType(filename);

            return pixelType switch
            {
                "int16" => OmeTiffFactory.CreateSigned16().ReadHyperstack(filename, progress),
                "uint16" => OmeTiffFactory.CreateUnsigned16().ReadHyperstack(filename, progress),
                "uint8" => OmeTiffFactory.CreateUnsigned8().ReadHyperstack(filename, progress),
                "int8" => OmeTiffFactory.CreateSigned8().ReadHyperstack(filename, progress),
                "int32" => OmeTiffFactory.CreateSigned32().ReadHyperstack(filename, progress),
                "uint32" => OmeTiffFactory.CreateUnsigned32().ReadHyperstack(filename, progress),
                "float" => OmeTiffFactory.CreateFloat32().ReadHyperstack(filename, progress),
                "double" => OmeTiffFactory.CreateFloat64().ReadHyperstack(filename, progress),
                _ => throw new NotSupportedException($"Pixel type '{pixelType}' is not supported.")
            };
        }

        /// <summary>
        /// TIFFファイルからピクセルタイプを検出
        /// </summary>
        private static string DetectPixelType(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"ファイルが見つかりません: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                if (tiff == null)
                    throw new IOException("TIFFファイルを開けませんでした。");

                // 1. OME-XMLから型を取得
                var imageDescription = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
                if (imageDescription != null && imageDescription.Length > 0)
                {
                    string omeXml = imageDescription[0].ToString();
                    string? pixelTypeFromXml = ExtractPixelTypeFromOmeXml(omeXml);
                    if (!string.IsNullOrEmpty(pixelTypeFromXml))
                        return pixelTypeFromXml;
                }

                // 2. TIFFタグから推測
                var bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 0;
                var sampleFormat = tiff.GetField(TiffTag.SAMPLEFORMAT)?[0].ToInt() ?? 0;

                return InferPixelType(bitsPerSample, (SampleFormat)sampleFormat);
            }
        }

        private static string? ExtractPixelTypeFromOmeXml(string omeXml)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if(doc.Root == null)
                    return null;
                var ns = doc.Root.GetDefaultNamespace();
                var pixels = doc.Descendants(ns + "Pixels").FirstOrDefault();

                // Type属性またはPixelType属性を探す
                return pixels?.Attribute("Type")?.Value
                    ?? pixels?.Attribute("PixelType")?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static string InferPixelType(int bitsPerSample, SampleFormat sampleFormat)
        {
            return (bitsPerSample, sampleFormat) switch
            {
                (8, SampleFormat.UINT) => "uint8",
                (8, SampleFormat.INT) => "int8",
                (16, SampleFormat.UINT) => "uint16",
                (16, SampleFormat.INT) => "int16",
                (32, SampleFormat.UINT) => "uint32",
                (32, SampleFormat.INT) => "int32",
                (32, SampleFormat.IEEEFP) => "float",
                (64, SampleFormat.IEEEFP) => "double",
                _ => throw new NotSupportedException($"Unsupported format: {bitsPerSample}bit, {sampleFormat}")
            };
        }
    }
    #endregion
}

#region 使用例

/*
// 使用例1: signed 16bit
var signedHandler = OmeTiffFactory.CreateSigned16();
List<short[]> signedData = GetSignedImageData();
signedHandler.WriteHyperstack("signed.ome.tiff", signedData, 1024, 1024, 1, 10, 5);

// 使用例2: unsigned 16bit
var unsignedHandler = OmeTiffFactory.CreateUnsigned16();
List<ushort[]> unsignedData = GetUnsignedImageData();
unsignedHandler.WriteHyperstack("unsigned.ome.tiff", unsignedData, 1024, 1024, 3, 10, 20);

// 使用例3: メモリ効率版（大容量データ）
var frames = GenerateFramesLazy(2048, 2048, 1000); // 1000フレーム
unsignedHandler.WriteHyperstack("large.ome.tiff", frames, 2048, 2048, 1, 1, 1000);

// 使用例4: 遅延読み込み
foreach (var frame in unsignedHandler.ReadFramesLazy("large.ome.tiff"))
{
    ProcessSingleFrame(frame);
}

// 使用例5: 特定フレームのみアクセス
var frame50 = unsignedHandler.ReadSingleFrameAt("large.ome.tiff", 49);

// 使用例6: メタデータのみ読み込み
var metadata = unsignedHandler.ReadMetadata("data.ome.tiff");
Console.WriteLine($"サイズ: {metadata.Width}x{metadata.Height}, フレーム数: {metadata.TotalFrames}");
*/

#endregion