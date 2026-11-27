using BitMiracle.LibTiff.Classic;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// The pixel types a TIFF frame can hold, and how each maps to TIFF's BitsPerSample / SampleFormat
/// tags, to its size in bytes and to its OME pixel-type name. Every TIFF dialect uses this one table.
/// </summary>
internal static class TiffPixelType
{
    public static bool IsSupported(Type type)
        => type == typeof(byte) || type == typeof(sbyte)
        || type == typeof(ushort) || type == typeof(short)
        || type == typeof(uint) || type == typeof(int)
        || type == typeof(float) || type == typeof(double);

    public static void EnsureSupported<T>() where T : unmanaged
    {
        if (!IsSupported(typeof(T)))
            throw new NotSupportedException(
                $"Pixel type {typeof(T).Name} is not supported. " +
                "Supported types: byte, sbyte, ushort, short, uint, int, float, double.");
    }

    public static int BitsPerSample(Type type)
    {
        if (type == typeof(byte) || type == typeof(sbyte)) return 8;
        if (type == typeof(ushort) || type == typeof(short)) return 16;
        if (type == typeof(uint) || type == typeof(int) || type == typeof(float)) return 32;
        if (type == typeof(double)) return 64;
        throw new NotSupportedException($"Pixel type {type.Name} is not supported.");
    }

    public static int BytesPerSample(Type type) => BitsPerSample(type) / 8;

    public static SampleFormat SampleFormatOf(Type type)
    {
        if (type == typeof(byte) || type == typeof(ushort) || type == typeof(uint)) return SampleFormat.UINT;
        if (type == typeof(sbyte) || type == typeof(short) || type == typeof(int)) return SampleFormat.INT;
        if (type == typeof(float) || type == typeof(double)) return SampleFormat.IEEEFP;
        throw new NotSupportedException($"Pixel type {type.Name} is not supported.");
    }

    public static string OmeName(Type type)
    {
        if (type == typeof(byte)) return "uint8";
        if (type == typeof(sbyte)) return "int8";
        if (type == typeof(ushort)) return "uint16";
        if (type == typeof(short)) return "int16";
        if (type == typeof(uint)) return "uint32";
        if (type == typeof(int)) return "int32";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        throw new NotSupportedException($"Pixel type {type.Name} is not supported.");
    }

    /// <summary>
    /// The OME-style name ("uint8" ... "double") for a BitsPerSample / SampleFormat pair.
    /// </summary>
    public static string NameOf(int bitsPerSample, SampleFormat sampleFormat)
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

    /// <summary>
    /// Reads the pixel-type name of the current directory of <paramref name="tiff"/> from its
    /// BitsPerSample / SampleFormat tags. A missing SampleFormat means unsigned integer.
    /// </summary>
    public static string NameOf(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        int bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 0;
        int sampleFormat = tiff.GetField(TiffTag.SAMPLEFORMAT)?[0].ToInt() ?? (int)SampleFormat.UINT;
        return NameOf(bitsPerSample, (SampleFormat)sampleFormat);
    }

    /// <summary>
    /// Throws when the current directory of <paramref name="tiff"/> does not store samples of type
    /// <typeparamref name="T"/>.
    /// </summary>
    public static void EnsureFileMatches<T>(BitMiracle.LibTiff.Classic.Tiff tiff) where T : unmanaged
    {
        string requested = OmeName(typeof(T));
        string actual = NameOf(tiff);
        if (actual != requested)
            throw new InvalidDataException($"File holds {actual} samples, but {requested} was requested.");
    }

    /// <summary>
    /// True when samples stored with the given byte order must be reversed to match the host.
    /// </summary>
    public static bool NeedsByteSwap(bool fileIsBigEndian) => fileIsBigEndian == BitConverter.IsLittleEndian;

    /// <summary>Reverses the byte order of every element of <paramref name="data"/> in place.</summary>
    public static void SwapEndiannessInPlace<T>(Span<T> data) where T : unmanaged
    {
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (size == 2)
        {
            var s = MemoryMarshal.Cast<T, ushort>(data);
            BinaryPrimitives.ReverseEndianness(s, s);
        }
        else if (size == 4)
        {
            var s = MemoryMarshal.Cast<T, uint>(data);
            BinaryPrimitives.ReverseEndianness(s, s);
        }
        else if (size == 8)
        {
            var s = MemoryMarshal.Cast<T, ulong>(data);
            BinaryPrimitives.ReverseEndianness(s, s);
        }
    }
}
