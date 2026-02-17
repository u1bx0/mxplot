using System;
using System.Collections.Generic;
using System.Text;


namespace MxPlot.Core.IO
{
    /*
    public record DecomposedData(
    List<byte[]> Channels, // [R, G, B] または [Gray]
    int ChannelCount,      // 1 or 3
    int Width,
    int Height
    );

    public class BitmapLoader
    {
        public DecomposedData LoadImage(string path, bool toGrayscale = false)
        {
            try
            {
                // Windows 環境: System.Drawing (GDI+) を使用
                return LoadViaSystemDrawing(path, toGrayscale);
            }
            catch (PlatformNotSupportedException)
            {
                // 非 Windows 環境: 自前バイナリ解析 (現在は BMP のみ対応)
                string ext = Path.GetExtension(path).ToLower();
                if (ext == ".bmp")
                {
                    return LoadBmpManually(path, toGrayscale);
                }
                throw new NotSupportedException($"Platform not supported for {ext}. Please use BMP on non-Windows.");
            }
        }

    private unsafe DecomposedData LoadViaSystemDrawing(string path, bool toGrayscale)
    {
            using var bitmap = new Bitmap(path);
        int w = bitmap.Width;
        int h = bitmap.Height;

        // どんなフォーマットも 32bit ARGB に強制変換してロック
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            return DecomposeHardwareSafe((byte*)data.Scan0, w, h, toGrayscale);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private unsafe DecomposedData LoadBmpManually(string path, bool toGrayscale)
    {
        // 最小限の BMP デコーダ (24bit / 32bit RGB 対応)
        byte[] fileBytes = File.ReadAllBytes(path);
        fixed (byte* pFile = fileBytes)
        {
            if (*(ushort*)pFile != 0x4D42) throw new Exception("Not a BMP file");

            int dataOffset = *(int*)(pFile + 10);
            int width = *(int*)(pFile + 18);
            int height = *(int*)(pFile + 22);
            short bpp = *(short*)(pFile + 28);

            // BMP はボトムアップ（逆さま）なので、本来は反転処理が必要
            // ここでは簡易的にピクセル分解へ
            byte[] r = new byte[width * height];
            byte[] g = new byte[width * height];
            byte[] b = new byte[width * height];

            int bytesPerPixel = bpp / 8;
            byte* pPixelData = pFile + dataOffset;

            for (int i = 0; i < width * height; i++)
            {
                // BMP は BGR 順
                b[i] = pPixelData[i * bytesPerPixel];
                g[i] = pPixelData[i * bytesPerPixel + 1];
                r[i] = pPixelData[i * bytesPerPixel + 2];
            }

            // グレースケールが必要ならここで合成
            if (toGrayscale)
            {
                byte[] gray = new byte[width * height];
                for (int i = 0; i < gray.Length; i++)
                    gray[i] = (byte)(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]);
                return new DecomposedData(new List<byte[]> { gray }, 1, width, height);
            }

            return new DecomposedData(new List<byte[]> { r, g, b }, 3, width, height);
        }
    }

        private unsafe DecomposedData DecomposeHardwareSafe(byte* pScan0, int w, int h, bool toGray)
        {
            int size = w * h;
            if (toGray)
            {
                byte[] gray = new byte[size];
                for (int i = 0; i < size; i++)
                {
                    // System.Drawing (Format32bppArgb) は BGRA 順
                    gray[i] = (byte)(0.299 * pScan0[i * 4 + 2] + 0.587 * pScan0[i * 4 + 1] + 0.114 * pScan0[i * 4]);
                }
                return new DecomposedData(new List<byte[]> { gray }, 1, w, h);
            }
            else
            {
                byte[] r = new byte[size];
                byte[] g = new byte[size];
                byte[] b = new byte[size];
                for (int i = 0; i < size; i++)
                {
                    b[i] = pScan0[i * 4];
                    g[i] = pScan0[i * 4 + 1];
                    r[i] = pScan0[i * 4 + 2];
                }
                return new DecomposedData(new List<byte[]> { r, g, b }, 3, w, h);
            }
        }
    }
    */

}
