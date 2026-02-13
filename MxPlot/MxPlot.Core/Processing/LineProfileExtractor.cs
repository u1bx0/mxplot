using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.Processing
{

    public enum LineProfileOption
    {
        NearestNeighbor, // 最近傍法（整数座標に丸める・高速）
        Bilinear         // 双線形補間（サブピクセル精度・滑らか）
    }

    public static class LineProfileExtractor
    {
        public static (double[] Pos, double[] Values) GetLineProfile<T>(
            this MatrixData<T> src,
            (double X, double Y) start,
            (double X, double Y) end)
            where T : unmanaged
        {
            throw new NotImplementedException();
        }

    }
}
