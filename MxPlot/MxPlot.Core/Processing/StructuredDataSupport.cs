using System;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Operations that keep the element type (Normalize, spatial filters, Grayscale) are defined for the primitive
    /// numeric types. A structured element type is not supported by them yet: it has to be read in a value mode
    /// first with <see cref="MatrixDataValueConverter.ToScalars{T}"/>.
    /// </summary>
    internal static class StructuredDataSupport
    {
        internal static void ThrowIfNotSupported<T>(string operation) where T : unmanaged
        {
            if (!MatrixData.SupportedPrimitiveTypes.Contains(typeof(T)))
                throw new NotSupportedException(
                    $"{operation} does not support {typeof(T).Name} data yet. Read it in a value mode first, " +
                    $"e.g. data.ToScalars(valueMode).{operation}(...).");
        }
    }
}
