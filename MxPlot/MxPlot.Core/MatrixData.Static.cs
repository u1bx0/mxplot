using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace MxPlot.Core
{
    public static class MatrixData
    {
        /// <summary>
        /// Contains the set of primitive numeric types supported for MatrixData&lt;T&gt;. 
        /// This collection is used to determine if certain operations, such as GetValueAsDouble, are supported for a given type parameter T.
        /// </summary>
        /// <remarks>This static, read-only collection can be used to validate or restrict types during
        /// processing. The set includes common integral and floating-point types recognized by the system.</remarks>
        public static readonly HashSet<Type> SupportedPrimitiveTypes = new()
        {
            typeof(byte), typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int), typeof(uint),
            typeof(long), typeof(ulong),
            typeof(float), typeof(double)
         };

        /// <summary>
        /// Internally calls Clone() method
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="target"></param>
        /// <returns></returns>
        public static T Duplicate<T>(this T target)
            where T : IMatrixData
        {
            return (T)target.Clone();
        }

        /// <summary>
        /// Loads matrix data from a file using the specified reader and returns it as an IMatrixData.
        /// </summary>
        /// <remarks>
        /// Use this method when the underlying data type is unknown at compile time. 
        /// The reader will determine the appropriate numeric type (e.g., double, int).
        /// </remarks>
        /// <param name="filePath">The source file path.</param>
        /// <param name="reader">The format reader implementation.</param>
        public static IMatrixData Load(string filePath, IMatrixDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return reader.Read(filePath);
        }

        /// <summary>
        /// Extension method for MatrixData<Complex> to get the value range (min and max) based on the specified ComplexValueMode.
        /// </summary>
        /// <param name="src"></param>
        /// <param name="frameIndex"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static (double Min, double Max) GetValueRange(this MatrixData<Complex> src, int frameIndex, ComplexValueMode mode)
        {
            return src.GetValueRange(frameIndex, (int)mode);
        }

        /*
        /// <summary>
        /// Create a new MatrixData instance with the same size, scale settings, and Metadata as the source
        /// </summary>
        /// <param name="src"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstanceFrom(IMatrixData src)
        {
            var dst = CreateNewInstance(src.ValueType, src.XCount, src.YCount, src.FrameCount);
            dst.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            dst.DefineDimensions(Axis.CreateFrom(src.Dimensions.Axes.ToArray()));
            
            return dst;
        }

        /// <summary>
        /// MatrixData<typeparamref name="T"/>型を動的に生成する
        /// </summary>
        /// <param name="type"></param>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstance(Type type, int xnum, int ynum) => CreateNewInstance(type, xnum, ynum, 1);

        /// <summary>
        /// MatrixData<typeparamref name="T"/>型のSeriesを動的に生成する
        /// </summary>
        /// <param name="type"></param>
        /// <param name="xNum"></param>
        /// <param name="yNum"></param>
        /// <param name="seriesNum"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstance(Type type, int xNum, int yNum, int seriesNum)
        {
            var genericType = typeof(MatrixData<>);
            var constructedType = genericType.MakeGenericType(type);
            var ret = Activator.CreateInstance(constructedType, xNum, yNum, seriesNum) as IMatrixData;
            if(ret is null)
                throw new InvalidOperationException("Failed to create MatrixData instance of type: " + type);

            return ret!;
        }
        */
    }
}
