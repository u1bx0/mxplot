using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MxPlot.Extensions.Tiff
{
    public static class OmeTiffHandler
    {
        /// <summary>
        /// Key for storing the original OME-XML metadata string in the IMatrixData.Metadata dictionary.
        /// </summary>
        public const string OmeXmlKey = "OME_XML";

        /// <summary>
        /// This is not required but it might be useful to pre-load the library.
        /// </summary>
        /// <remarks>This method is typically called at the start of an application to set up the
        /// environment for the library's functionality. It does not return any value or indicate success or
        /// failure.</remarks>
        public static void Activate()
        {
            //dummy call to access the assembly
            _ = typeof(BitMiracle.LibTiff.Classic.Tiff);
        }

        /// <summary>
        /// Save IMatrixData to OME-TIFF file
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="data"></param>
        /// <param name="option">   </param>
        /// <param name="progress"></param>
        /// <exception cref="Exception"></exception>
        /// <exception cref="InvalidDataException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        public static void Save(string filename, IMatrixData data, OmeTiffOptions? option = null, IProgress<int>? progress = null)
        {
            // filename is expected to be in the form name.ome.tiff or name.ome.tif
            if (filename.EndsWith(".ome.tiff", StringComparison.OrdinalIgnoreCase) == false &&
               filename.EndsWith(".ome.tif", StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new Exception("File extension must be .ome.tiff or .ome.tif");
            }

            int pageNum = data.FrameCount;
            int xnum = data.XCount;
            int ynum = data.YCount;
            var dims = data.Dimensions;
            Axis[] axes = dims.Axes.ToArray();

            int cnum = dims.GetLength("Channel");
            int znum = dims.GetLength("Z");
            int tnum = dims.GetLength("Time");
            int fovnum = dims.GetLength("FOV"); //series.Contains("FOV") ? data.Series["FOV"].Count : 1;
            // Axes other than Channel, Z, Time, and FOV are not supported.
            if (pageNum != cnum * znum * tnum * fovnum)
            {
                // At least one unsupported axis name is present.
                var bad = dims.Axes.Select(a => a.Name)
                                .Where(n => !new[] { "Channel", "Z", "Time", "FOV" }.Contains(n, StringComparer.OrdinalIgnoreCase))
                                .ToArray();
                string msg = bad switch
                {
                    [var x] => $"{x} is unsupported axis name.", // single unsupported axis
                    _ => $"{string.Join(", ", bad)} are unsupported axis names." // multiple (or zero) unsupported axes
                };

                throw new InvalidDataException(
                    $"Frame count expected: {cnum * znum * tnum * fovnum} " +
                    $"(C:{cnum} × Z:{znum} × T:{tnum} × FOV:{fovnum}), but got {pageNum} frames. {msg}");
            }

            #region Index calculation for CZT sort order
            int cAxisOrder = dims.Contains("Channel") ? dims.GetAxisOrder(dims["Channel"]!) : -1;
            int zAxisOrder = dims.Contains("Z") ? dims.GetAxisOrder(dims["Z"]!) : -1;
            int tAxisOrder = dims.Contains("Time") ? dims.GetAxisOrder(dims["Time"]!) : -1;
            int fovAxisOrder = dims.Contains("FOV") ? dims.GetAxisOrder(dims["FOV"]!) : -1;
            // Axes beyond C/Z/T/FOV are not handled here
            int[] axisIndexer = dims.GetAxisIndices();
            //int[] axisIndexer = new int[(cAxisOrder >= 0 ? 1 : 0) + (zAxisOrder >= 0 ? 1 : 0) + (tAxisOrder >= 0 ? 1 : 0)];
            int[] sortedIndex = new int[pageNum];
            int ip = 0;
            for (int ifov = 0; ifov < fovnum; ifov++)
            {
                if (fovAxisOrder >= 0) axisIndexer[fovAxisOrder] = ifov;
                for (int it = 0; it < tnum; it++)
                {
                    if (tAxisOrder >= 0) axisIndexer[tAxisOrder] = it;
                    for (int iz = 0; iz < znum; iz++)
                    {
                        if (zAxisOrder >= 0) axisIndexer[zAxisOrder] = iz;
                        for (int ic = 0; ic < cnum; ic++)
                        {
                            if (cAxisOrder >= 0) axisIndexer[cAxisOrder] = ic;
                            sortedIndex[ip++] = dims.GetFrameIndexAt(axisIndexer); // If c=z=t=1 the axisIndexer is empty and GetFrameIndexAt returns 0
                        }
                    }
                }
            }
            #endregion

            double xpitch = data.XStep;
            double ypitch = data.YStep;
            double zpitch = dims["Z"]?.Step ?? 1; //series.Contains("Z") ? data.Series["Z"].Pitch : 1;

            void WriteData<T>(MatrixData<T> md, OmeTiffHandlerInstance<T> handler) where T : unmanaged
            {
                // Build metadata with an empty ImageStack — frames are yielded lazily below.
                // Passing an empty list (not null) avoids CreateFrom's fallback that
                // materializes all frames, which would OOM for virtual data.
                var hd = HyperstackData<T>.CreateFrom(md, []);

                // Lazy frame iterator: yields one frame at a time in CZT sort order.
                // Each frame is Y-flipped so that TIFF row 0 = image top (standard TIFF convention).
                // Load() calls FlipY() after reading to restore MxPlot's Y-up convention (row 0 = bottom).
                // For virtual data, each GetArray() pages in a single frame via MMF
                // and the previous frame can be evicted by the cache strategy.
                IEnumerable<T[]> LazyFrames()
                {
                    for (int i = 0; i < pageNum; i++)
                    {
                        int pageIndex = sortedIndex[i];
                        T[] src = md.GetArray(pageIndex);
                        T[] flipped = new T[xnum * ynum];
                        for (int row = 0; row < ynum; row++)
                            Array.Copy(src, row * xnum, flipped, (ynum - 1 - row) * xnum, xnum);
                        yield return flipped;
                    }
                }

                handler.WriteHyperstack(filename,
                    LazyFrames(), hd,
                    option, progress);
            }

            if (data is MatrixData<short> mdShort)
            {
                WriteData<short>(mdShort, OmeTiffFactory.CreateSigned16());
            }
            else if (data is MatrixData<ushort> mdUShort)
            {
                WriteData<ushort>(mdUShort, OmeTiffFactory.CreateUnsigned16());
            }
            else if (data is MatrixData<float> mdflt)
            {
                WriteData<float>(mdflt, OmeTiffFactory.CreateFloat32());
            }
            else if (data is MatrixData<double> mdDbl)
            {
                WriteData<double>(mdDbl, OmeTiffFactory.CreateFloat64());
            }
            else if (data is MatrixData<byte> mdByte)
            {
                WriteData<byte>(mdByte, OmeTiffFactory.CreateUnsigned8());
            }
            else if (data is MatrixData<sbyte> mdSByte)
            {
                WriteData<sbyte>(mdSByte, OmeTiffFactory.CreateSigned8());
            }
            else if (data is MatrixData<int> mdInt)
            {
                WriteData<int>(mdInt, OmeTiffFactory.CreateSigned32());
            }
            else
            {
                throw new NotSupportedException($"{data.ValueType} is not supported for OMETiffHanlder.");
            }

        }

        public static IMatrixData Load(string filename, LoadingMode mode = LoadingMode.Auto, IProgress<int>? progress = null, int maxParallelDegree = 0, CancellationToken ct = default)
        {
            // Measure load time
            Stopwatch sw = new Stopwatch();
            Debug.WriteLine("[OMETiffUtility.LoadFrom] filename = " + filename);
            sw.Start();

            //MatrixData<ushort> md = null;
            IMatrixData? md = null;

            var result = OmeTiffReader.ReadHyperstackAuto(filename, mode, progress, maxParallelDegree, ct);
            var meta = result as HyperstackMetadata;
            if (meta == null)
                throw new InvalidDataException("Failed to read metadata from OME-TIFF file.");

            meta.FlipY(); // flip Y axis to restore MxPlot's Y-up convention (row 0 = bottom)

            switch (result)
            {
                case HyperstackData<ushort> ushortData:
                    md = ushortData.ImageStack switch
                    {
                        VirtualFrames<ushort> vList => MatrixData<ushort>.CreateAsVirtualFrames(ushortData.Width, ushortData.Height, vList),
                        List<ushort[]> mList => new MatrixData<ushort>(ushortData.Width, ushortData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for ushort.")
                    };
                    break;

                case HyperstackData<short> shortData:
                    md = shortData.ImageStack switch
                    {
                        VirtualFrames<short> vList => MatrixData<short>.CreateAsVirtualFrames(shortData.Width, shortData.Height, vList),
                        List<short[]> mList => new MatrixData<short>(shortData.Width, shortData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for short.")
                    };
                    break;

                case HyperstackData<float> floatData:
                    md = floatData.ImageStack switch
                    {
                        VirtualFrames<float> vList => MatrixData<float>.CreateAsVirtualFrames(floatData.Width, floatData.Height, vList),
                        List<float[]> mList => new MatrixData<float>(floatData.Width, floatData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for float.")
                    };
                    break;

                case HyperstackData<double> doubleData:
                    md = doubleData.ImageStack switch
                    {
                        VirtualFrames<double> vList => MatrixData<double>.CreateAsVirtualFrames(doubleData.Width, doubleData.Height, vList),
                        List<double[]> mList => new MatrixData<double>(doubleData.Width, doubleData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for double.")
                    };
                    break;

                case HyperstackData<byte> byteData:
                    md = byteData.ImageStack switch
                    {
                        VirtualFrames<byte> vList => MatrixData<byte>.CreateAsVirtualFrames(byteData.Width, byteData.Height, vList),
                        List<byte[]> mList => new MatrixData<byte>(byteData.Width, byteData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for byte.")
                    };
                    break;

                case HyperstackData<sbyte> sbyteData:
                    md = sbyteData.ImageStack switch
                    {
                        VirtualFrames<sbyte> vList => MatrixData<sbyte>.CreateAsVirtualFrames(sbyteData.Width, sbyteData.Height, vList),
                        List<sbyte[]> mList => new MatrixData<sbyte>(sbyteData.Width, sbyteData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for sbyte.")
                    };
                    break;

                case HyperstackData<int> intData:
                    md = intData.ImageStack switch
                    {
                        VirtualFrames<int> vList => MatrixData<int>.CreateAsVirtualFrames(intData.Width, intData.Height, vList),
                        List<int[]> mList => new MatrixData<int>(intData.Width, intData.Height, mList),
                        _ => throw new InvalidDataException("Unknown ImageStack type for int.")
                    };
                    break;

                default:
                    throw new NotSupportedException($"{result.GetType()} is not supported for OME-TIFF format.");
            }


            // Apply scale, axis, unit from OME-TIFF metadata
            ApplyMetadataToMatrixData(md, meta);
          
            return md;
        }

        /// <summary>
        /// Creates a new OME-TIFF file and returns a <see cref="MatrixData{T}"/> instance backed by virtual frames (Memory-Mapped File).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Compatibility Note:</b> This method is maintained for backward compatibility. 
        /// It acts as a convenience wrapper around the new builder-based API.
        /// </para>
        /// <para>
        /// <b>Recommended Usage:</b> For better architectural decoupling and access to format-specific extensions, 
        /// it is recommended to use the following pattern:
        /// <code>
        /// var spec = new HyperstackMetadata(...);
        /// var builder = OmeTiffFormat.AsVirtualBuilder(spec);
        /// var matrix = MatrixData&lt;float&gt;.CreateVirtual(filePath, builder);
        /// </code>
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The unmanaged numeric type for the pixel data.</typeparam>
        /// <param name="filePath">
        /// The path to the OME-TIFF file to be created. 
        /// If <see langword="null"/>, a temporary file will be automatically generated.
        /// </param>
        /// <param name="spec">The structural metadata (dimensions, axes, and scales) for the new dataset.</param>
        /// <returns>A writable <see cref="MatrixData{T}"/> instance bound to the virtual physical storage.</returns>
        public static MatrixData<T> CreateWritable<T>(
            string? filePath,
            HyperstackMetadata spec)
            where T : unmanaged
        {
            var builder = OmeTiffFormat.AsVirtualBuilder(spec);
            return builder.CreateWritable<T>(filePath);
        }

        // =====================================================================
        // Shared helper: apply HyperstackMetadata → IMatrixData (scale / axis / unit)
        // Used by both Load() and CreateWritable<T>().
        // =====================================================================
        public static void ApplyMetadataToMatrixData(IMatrixData md, HyperstackMetadata meta)
        {
            double pixelSizeX = meta.PixelSizeX;
            double pixelSizeY = meta.PixelSizeY;
            double pixelSizeZ = meta.PixelSizeZ;
            double originX = meta.StartX;
            double originY = meta.StartY;
            double originZ = meta.StartZ;

            md.SetXYScale(
                originX, originX + pixelSizeX * (meta.Width - 1),
                originY, originY + pixelSizeY * (meta.Height - 1));
            md.XUnit = meta.UnitX;
            md.YUnit = meta.UnitY;

            int channels = meta.Channels;
            int zSlices = meta.ZSlices;
            int timePoints = meta.TimePoints;
            int fovNum = meta.FovCount;

            if (channels * zSlices * timePoints * fovNum > 1)
            {
                // Set dimension structure
                string order = meta.DimensionOrder; // e.g. "XYCZT"
                var axes = new List<Axis>();

                // Last 3 characters of DimensionOrder define the C/Z/T ordering
                var str = order.Substring(order.Length - 3, 3).ToArray();
                foreach (var a in str)
                {
                    if (a == 'C' && channels > 1) axes.Add(Axis.Channel(channels));
                    else if (a == 'Z' && zSlices > 1) axes.Add(Axis.Z(zSlices, originZ, originZ + (zSlices - 1) * pixelSizeZ, meta.UnitZ));
                    else if (a == 'T' && timePoints > 1) axes.Add(Axis.Time(timePoints, meta.StartTime, meta.StartTime + (timePoints - 1) * meta.TimeStep, meta.UnitTime));
                }

                if (fovNum > 1)
                {
                    var layout = meta.TileLayout;
                    var origins = meta.GlobalOrigins?.ToList();
                    axes.Add(origins != null && origins.Count == fovNum
                        ? new FovAxis(origins, layout.X, layout.Y)
                        : new FovAxis(layout.X, layout.Y));
                }

                md.DefineDimensions(axes.ToArray());
            }

           //Set Metadata
            md.Metadata.Clear();
            if (meta.MatrixDataMetadata is not null)
            {
                foreach (var kvp in meta.MatrixDataMetadata)
                {
                    md.Metadata[kvp.Key] = kvp.Value;
                }
            }

            if (meta.OMEXml is not null)
            {
                var xmlString = HyperstackMetadata.FormatXml(meta.OMEXml);
                md.Metadata[OmeXmlKey] = xmlString;
                md.MarkAsFormatHeader(OmeXmlKey);
            }
        }

    }
}
