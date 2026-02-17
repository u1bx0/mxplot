using MxPlot.Core;
using MxPlot.Core.IO;
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
    /// <param name="Channels">List of byte arrays, R, G, B or gray-scale</param>
    /// <param name="ChannelCount">1 (Gray) or 3 (RGB)</param>
    /// <param name="Width"></param>
    /// <param name="Height">さ</param>
    public record DecomposedData(
        List<byte[]> Channels,
        int ChannelCount,
        int Width,
        int Height);

    public static class ImageLoader
    {
        /// <summary>
        /// Loads an image from the specified path and decomposes it into separate channels using SkiaSharp.
        /// </summary>
        public static DecomposedData LoadAsDecomposedData(string path, bool toGrayscale = false, bool flipY = true)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"画像ファイルが見つかりません: {path}");

            // SKBitmap.Decode は自動的に PNG/JPG/BMP 等を判別してメモリに展開する
            using var stream = File.OpenRead(path);
            using var bitmap = SKBitmap.Decode(stream);

            if (bitmap == null)
                throw new InvalidDataException("画像のデコードに失敗しました。形式が未対応か壊れています。");

            int w = bitmap.Width;
            int h = bitmap.Height;
            int pixelCount = w * h;

            // SkiaSharpのピクセルデータに直接アクセス (ReadOnlySpan)
            // デフォルトでは SKColorType.Rgba8888 または Bgra8888
            ReadOnlySpan<SKColor> pixels = bitmap.Pixels;

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
                        gray[y * w + x] = (byte)(0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue);
                    }
                }
                return new DecomposedData(new List<byte[]> { gray }, 1, w, h);
            }
            else
            {
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
                return new DecomposedData(new List<byte[]> { r, g, b }, 3, w, h);
            }
        }

        public static MatrixData<T> LoadImage<T>(string path, bool toGrayscale = false, double normalizationDivisor = 1.0) where T: unmanaged
        {
            if (!(typeof(T) == typeof(byte) || typeof(T) == typeof(float) ||
                    typeof(T) == typeof(double) || typeof(T) == typeof(int) ||
                    typeof(T) == typeof(System.Numerics.Complex)))
            {
                throw new NotSupportedException($"{typeof(T).Name} is not supported.");
            }

            var ret = LoadAsDecomposedData(path, toGrayscale);
            if (typeof(T) == typeof(byte) && normalizationDivisor == 1.0) //No conversion needed, just wrap the byte arrays in MatrixData<byte>
            {
                return (MatrixData<T>)(object)new MatrixData<byte>(ret.Width, ret.Height, ret.Channels);
            }
            else
            {
                List<T[]> dst = new List<T[]>();
                if (typeof(T) == typeof(Complex)) //特殊条件：TがComplex型であれば、byteを正規化して実数部に格納し、虚数部は0とする
                {
                    for(int i = 0; i < ret.Channels.Count; i++)
                    {
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
                        byte[] src = ret.Channels[i];
                        T[] destination = new T[src.Length]; // byteの数と同じだけTを確保
                        for (int j = 0; j < src.Length; j++) // byteを1つずつTに変換してコピー
                        {
                            double val = src[j] / normalizationDivisor; // 先に double で計算してから T にキャストする(ゼロ回避)
                            destination[j] = (T)Convert.ChangeType(val, typeof(T));
                        }
                        dst.Add(destination);
                    }
                }
                return new MatrixData<T>(ret.Width, ret.Height, dst);
            }
        }

        public enum BitmapReadMode
        {
            GrayScale,
            RGBDecomposed
        }

        /// <summary>
        /// Provides configuration options and methods for reading bitmap image files and extracting their pixel data as MatrixData&lt;T&gt;. 
        /// </summary>
        /// <remarks>The BitmapImageFormat class allows customization of how bitmap images are read,
        /// including normalization of pixel values and selection of grayscale or RGB decomposition modes. It implements
        /// the IMatrixDataReader interface, enabling integration with matrix-based data processing workflows.</remarks>
        public class BitmapImageFormat : IMatrixDataReader
        {
            /// <summary>
            /// Gets or sets the divisor used to normalize values in calculations. If 255, the values will be normalized to the range [0, 1]. Default is 1.0 (no normalization).
            /// </summary>
            public double NormalizationDivisor { get; set; } = 1.0;

            /// <summary>
            /// Gets or sets the mode used when reading bitmap images. GrayScale mode will convert the image to grayscale, while RGBDecomposed will keep the RGB channels separate. Default is GrayScale.
            /// </summary>
            public BitmapReadMode Mode { get; set; } = BitmapReadMode.GrayScale;

            /// <summary>
            /// Provides configuration options and methods for reading bitmap image files and extracting their pixel data as MatrixData&lt;T&gt;. This class is used for the static method of MatrixData.Load and MatrixData.Load&lt;T&gt;. 
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
                return LoadImage<T>(filePath, Mode == BitmapReadMode.GrayScale, NormalizationDivisor);
            }

            public IMatrixData Read(string path)
            {
                return LoadImage<byte>(path, Mode == BitmapReadMode.GrayScale, NormalizationDivisor);
            }
        }
    }
}
