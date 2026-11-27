using MxPlot.Core;
using MxPlot.Core.IO.Formats;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Extensions.Images
{
    /// <summary>
    /// Data structure to hold decomposed image data (either RGB channels or grayscale).
    /// </summary>
    /// <param name="Channels">
    /// Byte arrays, R, G, B or gray-scale. For an animated image the frames follow one another with
    /// the channels varying fastest: the channels of frame 0, then those of frame 1, and so on.
    /// </param>
    /// <param name="ChannelCount">1 (Gray) or 3 (RGB) channels per frame</param>
    /// <param name="Width"></param>
    /// <param name="Height">さ</param>
    /// <param name="FrameCount">Number of animation frames; 1 for a still image.</param>
    /// <param name="FrameIntervalSeconds">Duration of the first frame in seconds; 0 for a still image.</param>
    /// <param name="OmittedFrameCount">
    /// Frames of an animated file that were left out because only the first one was read; 0 otherwise.
    /// </param>
    public record DecomposedData(
        List<byte[]> Channels,
        int ChannelCount,
        int Width,
        int Height,
        int FrameCount = 1,
        double FrameIntervalSeconds = 0,
        int OmittedFrameCount = 0);

    /// <summary>
    /// What an animated image (GIF, animated WebP) holds, known from its header alone.
    /// </summary>
    /// <param name="Width"></param>
    /// <param name="Height"></param>
    /// <param name="FrameCount"></param>
    /// <param name="EstimatedBytes">
    /// Size of the pixel data when every frame is read, counted as three bytes per pixel (the size of
    /// an RGB read; a gray read needs a third of it).
    /// </param>
    public sealed record AnimationInfo(int Width, int Height, int FrameCount, long EstimatedBytes);

    public static class ImageLoader
    {
        /// <summary>
        /// Loads an image from the specified path and decomposes it into separate channels using SkiaSharp.
        /// </summary>
        public static DecomposedData LoadAsDecomposedData(string path, bool toGrayscale = false, bool flipY = true)
            => Decompose(path, toGrayscale ? BitmapReadMode.GrayScale : BitmapReadMode.RGBDecomposed, flipY);

        /// <summary>
        /// The size an animated file expands to when every frame is read. Reads only the header, so it
        /// is cheap even for a huge file.
        /// </summary>
        /// <returns>
        /// The description, or <c>null</c> when the file is missing, unreadable, or not an animation
        /// (a still image, or an animated format that holds a single frame).
        /// </returns>
        public static AnimationInfo? InspectAnimation(string path)
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec == null || codec.FrameCount <= 1)
                return null;

            int w = codec.Info.Width;
            int h = codec.Info.Height;
            return new AnimationInfo(w, h, codec.FrameCount, 3L * w * h * codec.FrameCount);
        }

        /// <summary>Default for the size above which <see cref="AnimationReadMode.Auto"/> reads only the first frame: 1 GB.</summary>
        public const long DefaultMaxAnimationBytes = 1L << 30;

        /// <summary>
        /// Returns <c>true</c> when every pixel satisfies R == G == B, i.e. the image carries no
        /// colour information regardless of the format it was encoded in.
        /// </summary>
        /// <remarks>
        /// SkiaSharp 3.x exposes no encoded-colour-type information, so the decoded pixels are the
        /// only reliable source. A colour image bails out on its first coloured pixel, which is
        /// almost always immediate; only a genuinely gray image pays for a full pass, and that is a
        /// single cheap scan over memory that has just been written.
        /// </remarks>
        private static bool IsMonochrome(ReadOnlySpan<SKColor> pixels)
        {
            foreach (var c in pixels)
                if (c.Red != c.Green || c.Green != c.Blue)
                    return false;
            return true;
        }

        /// <summary>Rec.709 luma of one pixel, truncated to a byte.</summary>
        private static byte ToGray(byte r, byte g, byte b)
            => (byte)(0.2126f * r + 0.7152f * g + 0.0722f * b);

        /// <summary>Frame duration used when an animated image stores none (a duration of 0).</summary>
        private const double DefaultFrameIntervalSeconds = 0.1;

        private static DecomposedData Decompose(string path, BitmapReadMode mode, bool flipY = true,
            AnimationReadMode animation = AnimationReadMode.Auto, long maxAnimationBytes = DefaultMaxAnimationBytes,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"No such file: {path}");

            cancellationToken.ThrowIfCancellationRequested();

            // An animated image (GIF, animated WebP) is read frame by frame when the mode asks for every
            // frame; otherwise, like every other file and an animated format holding a single frame, it
            // goes through the single-bitmap path below, which yields the first frame.
            int omitted = 0;
            using (var probe = File.OpenRead(path))
            using (var codec = SKCodec.Create(probe))
            {
                if (codec != null && codec.FrameCount > 1)
                {
                    long estimated = 3L * codec.Info.Width * codec.Info.Height * codec.FrameCount;
                    bool allFrames = animation == AnimationReadMode.AllFrames
                        || (animation == AnimationReadMode.Auto && estimated <= maxAnimationBytes);
                    if (allFrames)
                        return DecomposeFrames(codec, mode, flipY, cancellationToken);
                    omitted = codec.FrameCount - 1;
                }
            }

            // SKBitmap.Decode は自動的に PNG/JPG/BMP 等を判別してメモリに展開する
            using var stream = File.OpenRead(path);
            using var bitmap = SKBitmap.Decode(stream);

            if (bitmap == null)
                throw new InvalidDataException("Failed to load bitmap.");

            int w = bitmap.Width;
            int h = bitmap.Height;
            int pixelCount = w * h;

            // SkiaSharpのピクセルデータに直接アクセス (ReadOnlySpan)
            // デフォルトでは SKColorType.Rgba8888 または Bgra8888
            ReadOnlySpan<SKColor> pixels = bitmap.Pixels;

            bool toGrayscale = mode switch
            {
                BitmapReadMode.GrayScale => true,
                BitmapReadMode.RGBDecomposed => false,
                _ => IsMonochrome(pixels),
            };

            if (toGrayscale)
            {
                byte[] gray = new byte[pixelCount];
                for (int y = 0; y < h; y++)
                {
                    // flipY が true なら y を反転させて読み取り先を計算
                    int sourceY = flipY ? (h - 1 - y) : y;
                    for (int x = 0; x < w; x++)
                    {
                        var c = pixels[sourceY * w + x];
                        gray[y * w + x] = ToGray(c.Red, c.Green, c.Blue);
                    }
                }
                return new DecomposedData(new List<byte[]> { gray }, 1, w, h, OmittedFrameCount: omitted);
            }
            else
            {
                return new DecomposedData(SplitRgb(pixels, w, h, flipY), 3, w, h, OmittedFrameCount: omitted);
            }
        }

        /// <summary>Splits the pixels of one frame into R, G and B arrays, flipped vertically when asked.</summary>
        private static List<byte[]> SplitRgb(ReadOnlySpan<SKColor> pixels, int w, int h, bool flipY)
        {
            int pixelCount = w * h;
            byte[] r = new byte[pixelCount];
            byte[] g = new byte[pixelCount];
            byte[] b = new byte[pixelCount];

            for (int y = 0; y < h; y++)
            {
                int sourceY = flipY ? (h - 1 - y) : y;
                for (int x = 0; x < w; x++)
                {
                    var c = pixels[sourceY * w + x];
                    int targetIdx = y * w + x;
                    r[targetIdx] = c.Red;
                    g[targetIdx] = c.Green;
                    b[targetIdx] = c.Blue;
                }
            }
            return new List<byte[]> { r, g, b };
        }

        /// <summary>
        /// Reads every frame of an animated image. The frames come back one after another with the
        /// channels varying fastest, and the first frame's duration stands for the whole animation.
        /// </summary>
        private static DecomposedData DecomposeFrames(SKCodec codec, BitmapReadMode mode, bool flipY,
            CancellationToken cancellationToken)
        {
            int w = codec.Info.Width;
            int h = codec.Info.Height;
            int pixelCount = w * h;
            var frameInfos = codec.FrameInfo;
            int frameCount = frameInfos.Length;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);

            // A frame may be stored as a difference to an earlier one (RequiredFrame). That frame has
            // to stay in memory until the last frame that builds on it has been decoded.
            var lastNeeded = new int[frameCount];
            Array.Fill(lastNeeded, -1);
            for (int i = 0; i < frameCount; i++)
            {
                if (frameInfos[i].RequiredFrame >= 0)
                    lastNeeded[frameInfos[i].RequiredFrame] = i;
            }

            var kept = new Dictionary<int, SKBitmap>();
            var channels = new List<byte[]>(frameCount * 3);
            bool allMonochrome = true;
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bitmap = new SKBitmap(info);
                    int required = frameInfos[i].RequiredFrame;
                    SKCodecOptions options;
                    if (required >= 0)
                    {
                        kept[required].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                        options = new SKCodecOptions(i, required);
                    }
                    else
                    {
                        options = new SKCodecOptions(i);
                    }

                    var result = codec.GetPixels(info, bitmap.GetPixels(), options);
                    if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    {
                        bitmap.Dispose();
                        throw new InvalidDataException($"Failed to decode frame {i}: {result}.");
                    }

                    ReadOnlySpan<SKColor> pixels = bitmap.Pixels;
                    if (allMonochrome)
                        allMonochrome = IsMonochrome(pixels);
                    channels.AddRange(SplitRgb(pixels, w, h, flipY));

                    if (lastNeeded[i] > i) kept[i] = bitmap;
                    else bitmap.Dispose();

                    foreach (int key in kept.Keys.Where(k => lastNeeded[k] <= i).ToList())
                    {
                        kept[key].Dispose();
                        kept.Remove(key);
                    }
                }
            }
            finally
            {
                foreach (var bitmap in kept.Values)
                    bitmap.Dispose();
            }

            // Auto collapses to gray only when no frame carries colour.
            bool toGrayscale = mode switch
            {
                BitmapReadMode.GrayScale => true,
                BitmapReadMode.RGBDecomposed => false,
                _ => allMonochrome,
            };

            int channelCount = 3;
            if (toGrayscale)
            {
                var gray = new List<byte[]>(frameCount);
                for (int f = 0; f < frameCount; f++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] r = channels[3 * f],g = channels[3 * f + 1], b = channels[3 * f + 2];
                    byte[] y = new byte[pixelCount];
                    for (int j = 0; j < pixelCount; j++)
                        y[j] = ToGray(r[j], g[j], b[j]);
                    gray.Add(y);
                }
                channels = gray;
                channelCount = 1;
            }

            double interval = frameInfos[0].Duration > 0
                ? frameInfos[0].Duration / 1000.0
                : DefaultFrameIntervalSeconds;
            return new DecomposedData(channels, channelCount, w, h, frameCount, interval);
        }

        public static MatrixData<T> LoadImage<T>(string path, bool toGrayscale = false, double normalizationDivisor = 1.0) where T : unmanaged
            => LoadImage<T>(path,
                            toGrayscale ? BitmapReadMode.GrayScale : BitmapReadMode.RGBDecomposed,
                            normalizationDivisor);

        /// <summary>
        /// Loads an image using an explicit <see cref="BitmapReadMode"/>.
        /// </summary>
        /// <remarks>
        /// A three-channel result gets a proper <see cref="ColorAxis"/> axis rather than the
        /// generic "Frame" axis <see cref="DimensionStructure"/> would otherwise invent, which is
        /// what lets the viewer recognise it as a colour image.
        /// <para>
        /// An animated image (GIF, animated WebP) additionally gets a "Time" axis in seconds, after the
        /// colour axis if there is one, whose step is the duration of the first frame.
        /// </para>
        /// </remarks>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was canceled. It is checked between frames.
        /// </exception>
        public static MatrixData<T> LoadImage<T>(string path, BitmapReadMode mode, double normalizationDivisor = 1.0,
            AnimationReadMode animation = AnimationReadMode.Auto, long maxAnimationBytes = DefaultMaxAnimationBytes,
            CancellationToken cancellationToken = default) where T: unmanaged
        {
            if (!(typeof(T) == typeof(byte) || typeof(T) == typeof(float) ||
                    typeof(T) == typeof(double) || typeof(T) == typeof(int) ||
                    typeof(T) == typeof(System.Numerics.Complex)))
            {
                throw new NotSupportedException($"{typeof(T).Name} is not supported.");
            }

            var ret = Decompose(path, mode, animation: animation, maxAnimationBytes: maxAnimationBytes,
                cancellationToken: cancellationToken);
            MatrixData<T> md;
            if (typeof(T) == typeof(byte) && normalizationDivisor == 1.0) //No conversion needed, just wrap the byte arrays in MatrixData<byte>
            {
                md = (MatrixData<T>)(object)new MatrixData<byte>(ret.Width, ret.Height, ret.Channels);
            }
            else
            {
                List<T[]> dst = new List<T[]>();
                if (typeof(T) == typeof(Complex)) //特殊条件：TがComplex型であれば、byteを正規化して実数部に格納し、虚数部は0とする
                {
                    for(int i = 0; i < ret.Channels.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] src = ret.Channels[i];
                        T[] destination = new T[src.Length]; // byteの数と同じだけTを確保
                        for (int j = 0; j < src.Length; j++)
                        {
                            double real = src[j] / normalizationDivisor; //byteを正規化してdoubleに変換
                            Complex z = new Complex(real, 0); // Complex型の値を作成（虚部は0）
                            destination[j] = (T)(object)z; // Complex型をobjectにキャストしてからTにキャスト
                        }
                        dst.Add(destination);
                    }
                }
                else //通常の条件：byteを正規化してTに変換する
                {
                    for (int i = 0; i < ret.Channels.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte[] src = ret.Channels[i];
                        T[] destination = new T[src.Length]; // byteの数と同じだけTを確保
                        for (int j = 0; j < src.Length; j++) //byteを1つずつTに変換してコピー
                        {
                            double val = src[j] / normalizationDivisor; // 先に double で計算してから T にキャストする(ゼロ回避)
                            destination[j] = (T)Convert.ChangeType(val, typeof(T));
                        }
                        dst.Add(destination);
                    }
                }
                md = new MatrixData<T>(ret.Width, ret.Height, dst);
            }

            if (ret.FrameCount > 1)
            {
                // The frames of an animation are points in time; MatrixPlotter plays a "Time" axis at
                // the rate its Step (in seconds) gives. Channels vary fastest, so they come first.
                var time = Axis.Time(ret.FrameCount, 0, (ret.FrameCount - 1) * ret.FrameIntervalSeconds, "s");
                if (ret.ChannelCount == 3)
                    md.DefineDimensions(ColorAxis.CreateRgb(), time);
                else
                    md.DefineDimensions(time);
            }
            else if (ret.ChannelCount == 3)
            {
                md.DefineDimensions(ColorAxis.CreateRgb());
            }

            if (ret.OmittedFrameCount > 0)
                md.Metadata["Animation"] = $"The file holds {ret.OmittedFrameCount + 1} frames; only the first one was loaded.";

            return md;
        }

        public enum BitmapReadMode
        {
            /// <summary>
            /// Decides per image: sources whose pixels are all R == G == B load as a single
            /// grayscale channel, everything else is decomposed into R/G/B. This is the default —
            /// it keeps grayscale sources cheap while letting colour images arrive as real channels.
            /// </summary>
            Auto,

            /// <summary>Always collapse to one channel using Rec.709 luma.</summary>
            GrayScale,

            /// <summary>Always decompose into three R/G/B channels, even for a gray source.</summary>
            RGBDecomposed
        }

        /// <summary>
        /// How an animated image (GIF, animated WebP) is read. A still image is not affected.
        /// </summary>
        public enum AnimationReadMode
        {
            /// <summary>
            /// Every frame while the expanded pixel data stays within the size limit, the first frame
            /// only above it. A note about the omitted frames is added to the metadata.
            /// </summary>
            Auto,

            /// <summary>Every frame, whatever the size. A large animation may take a long time and more memory than is available.</summary>
            AllFrames,

            /// <summary>The first frame only.</summary>
            FirstFrameOnly
        }

        /// <summary>
        /// Provides configuration options and methods for reading bitmap image files and extracting their pixel data as MatrixData&lt;T&gt;.
        /// </summary>
        /// <remarks>The BitmapImageFormat class allows customization of how bitmap images are read,
        /// including normalization of pixel values and selection of grayscale or RGB decomposition modes. It implements
        /// the IMatrixDataReader interface, enabling integration with matrix-based data processing workflows.</remarks>
        public class BitmapImageFormat : IMatrixDataReader
        {
            public string FormatName => "Bitmap Image";

            public IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

            /// <summary>
            /// Gets or sets the divisor used to normalize values in calculations. If 255, the values will be normalized to the range [0, 1]. Default is 1.0 (no normalization).
            /// </summary>
            public double NormalizationDivisor { get; set; } = 1.0;

            /// <summary>
            /// Gets or sets the mode used when reading bitmap images. GrayScale mode will convert the image to grayscale, while RGBDecomposed will keep the RGB channels separate. Default is Auto, which picks between the two by inspecting the decoded pixels.
            /// </summary>
            public BitmapReadMode Mode { get; set; } = BitmapReadMode.Auto;

            /// <summary>
            /// How an animated image is read. Default is Auto: every frame unless the expanded pixel
            /// data would exceed <see cref="MaxAnimationBytes"/>.
            /// </summary>
            public AnimationReadMode AnimationMode { get; set; } = AnimationReadMode.Auto;

            /// <summary>
            /// The size of the expanded pixel data above which <see cref="AnimationReadMode.Auto"/> reads
            /// only the first frame. Default is <see cref="DefaultMaxAnimationBytes"/> (1 GB).
            /// </summary>
            public long MaxAnimationBytes { get; set; } = DefaultMaxAnimationBytes;

            public CancellationToken CancellationToken { get; set; }

            bool IMatrixDataReader.IsCancellable => true;

            /// <summary>
            /// Provides configuration options
            /// </summary>
            /// <remarks>
            /// When this class is called as <br/><br/>
            /// <c>var md = MatrixData&lt;double&gt;.Load(path, new BitmapImageFormat() { Mode = BitmapReadMode.RGBDecomposed, NormalizationDivisor=255});</c>
            /// <br/><br/>
            /// then, it reads an image file to generate a MatrixData of type double, with RGB channels decomposed. 
            /// The NormalizationDivisor with 255 normalizes pixel values to the range [0, 1]. The deafult is 1 which means no normalization. <br/> 
            /// If Complex type is used, the real part will be the (normalized) pixel value and the imaginary part will be set to 0. <br/>
            /// </remarks>
            public BitmapImageFormat() { }

            /// <summary>
            /// Reads an image file from the specified path and returns its pixel data as a matrix of the specified
            /// unmanaged type.
            /// </summary>
            /// <remarks>The pixel data is read in either grayscale or color mode, depending on the
            /// current BitmapReadMode. Pixel values may be normalized according to the NormalizationDivisor property.
            /// The caller is responsible for ensuring that the file exists and is accessible.</remarks>
            /// <typeparam name="T">The unmanaged value type to use for the matrix elements representing pixel data.</typeparam>
            /// <param name="filePath">The path to the image file to read. Cannot be null or empty.</param>
            /// <returns>A MatrixData<T> containing the pixel data of the image. The matrix will be empty if the file does not
            /// contain any pixel data.</returns>
            public MatrixData<T> Read<T>(string filePath) where T : unmanaged
            {
                return LoadImage<T>(filePath, Mode, NormalizationDivisor, AnimationMode, MaxAnimationBytes, CancellationToken);
            }

            public IMatrixData Read(string path)
            {
                return LoadImage<byte>(path, Mode, NormalizationDivisor, AnimationMode, MaxAnimationBytes, CancellationToken);
            }
        }
    }
}
