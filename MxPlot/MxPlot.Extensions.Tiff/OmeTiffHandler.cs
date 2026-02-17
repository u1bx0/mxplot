using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MxPlot.Extensions.Tiff
{
    public static class OmeTiffHandler
    {
        /// <summary>
        /// Save IMatrixData to OME-TIFF file
        /// </summary>
        /// <param name="filename"></param>
        /// <param name="data"></param>
        /// <param name="progress"></param>
        /// <exception cref="Exception"></exception>
        /// <exception cref="InvalidDataException"></exception>
        /// <exception cref="NotSupportedException"></exception>
        public static void Save(string filename, IMatrixData data,  IProgress<int>? progress = null)
        {
            //filenameはname.ome.tiffの形式であることを想定
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
            //これ以外の軸には対応していない。。。
            if (pageNum != cnum * znum * tnum * fovnum)
            {
                //非対応な軸がある。
                var bad = dims.Axes.Select(a => a.Name)
                                .Where(n => !new[] { "Channel", "Z", "Time", "FOV" }.Contains(n, StringComparer.OrdinalIgnoreCase))
                                .ToArray();
                string msg = bad switch
                {
                    [var x] => $"{x} is unsupported axis name.",                      // 1個のとき
                    _ => $"{string.Join(", ", bad)} are unsupported axis names." // 複数（または0）のとき
                };

                throw new InvalidDataException(
                    $"Frame count expected: {cnum * znum * tnum * fovnum} " +
                    $"(C:{cnum} × Z:{znum} × T:{tnum} × FOV:{fovnum}), but got {pageNum} frames. {msg}");
            }

            #region cztの順に並べ替えるためのindex計算
            int cAxisOrder = dims.Contains("Channel") ? dims.GetAxisOrder(dims["Channel"]!) : -1;
            int zAxisOrder = dims.Contains("Z") ? dims.GetAxisOrder(dims["Z"]!) : -1;
            int tAxisOrder = dims.Contains("Time") ? dims.GetAxisOrder(dims["Time"]!) : -1;
            int fovAxisOrder = dims.Contains("FOV") ? dims.GetAxisOrder(dims["FOV"]!) : -1;
            //これ以外の軸があっても対応できない
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
                            sortedIndex[ip++] = dims.GetFrameIndexAt(axisIndexer); //もしc=1,z=1,t=1ならaxisIndexerは空配列なので、0が返る    
                        }
                    }
                }
            }
            #endregion

            double xpitch = data.XStep;
            double ypitch = data.YStep;
            double zpitch = dims["Z"]?.Step ?? 1; //series.Contains("Z") ? data.Series["Z"].Pitch : 1;

            string customParameters = JsonSerializer.Serialize(data.Metadata);

            void WriteData<T>(MatrixData<T> md, OmeTiffHandlerInstance<T> handler) where T : unmanaged
            {
                List<T[]> list = new List<T[]>(pageNum);
                for (int i = 0; i < pageNum; i++)
                {
                    int pageIndex = sortedIndex[i];
                    list.Add(md.GetArray(pageIndex));
                }
                var hd = HyperstackData<T>.CreateFrom(md, list);
                //y軸方向に反転させる MatrixDataPlotterは左下が原点
                hd.FlipVertical();

                handler.WriteHyperstack(filename,
                    list.AsEnumerable(), hd,
                    null, customParameters, progress);
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

        public static IMatrixData Load(string filename, IProgress<int>? progress = null)
        {
            //時間計測をする
            Stopwatch sw = new Stopwatch();
            Debug.WriteLine("[OMETiffUtility.LoadFrom] filename = " + filename);
            sw.Start();

            //MatrixData<ushort> md = null;
            IMatrixData? md = null;

            var result = OmeTiffReader.ReadHyperstackAuto(filename, progress);
            var meta = result as HyperstackMetadata;
            if (meta == null)
                throw new InvalidDataException("Failed to read metadata from OME-TIFF file.");

            meta.FlipVertical(); //Y軸反転
            switch (result)
            {
                case HyperstackData<ushort> ushortData:
                    md = new MatrixData<ushort>(xnum: ushortData.Width, ynum: ushortData.Height, ushortData.ImageStack); 
                    break;
                case HyperstackData<short> shortData:
                    md = new MatrixData<short>(shortData.Width, shortData.Height, shortData.ImageStack);
                    break;
                case HyperstackData<float> floatData:
                    md = new MatrixData<float>(floatData.Width, floatData.Height, floatData.ImageStack);
                    break;
                case HyperstackData<double> doubleData:
                    md = new MatrixData<double>(doubleData.Width, doubleData.Height, doubleData.ImageStack);
                    break;
                case HyperstackData<byte> byteData:
                    md = new MatrixData<byte>(byteData.Width, byteData.Height, byteData.ImageStack);
                    break;
                case HyperstackData<sbyte> sbyteData:
                    md = new MatrixData<sbyte>(sbyteData.Width, sbyteData.Height, sbyteData.ImageStack);
                    break;
                case HyperstackData<int> intData:
                    md = new MatrixData<int>(intData.Width, intData.Height, intData.ImageStack);
                    break;
                default:
                    throw new NotSupportedException($"{result} is not supported for OME-TIFF format.");
            }

            int xnum = meta.Width;
            int ynum = meta.Height;
            int channels = meta.Channels;
            int zSlices = meta.ZSlices;
            int timePoints = meta.TimePoints;
            int fovNum = meta.FovCount;
            double pixelSizeX = meta.PixelSizeX;
            double pixelSizeY = meta.PixelSizeY;
            double pixelSizeZ = meta.PixelSizeZ;

            double originX = meta.StartX;
            double originY = meta.StartY;
            double originZ = meta.StartZ;

            double xmin = originX;
            double xmax = originX + pixelSizeX * (xnum - 1);
            double ymin = originY;
            double ymax = originY + pixelSizeY * (ynum - 1);

            md.SetXYScale(xmin, xmax, ymin, ymax);
            md.XUnit = meta.UnitX;
            md.YUnit = meta.UnitY;

            if (channels <= 0 || zSlices <= 0 || timePoints <= 0)
            {
                throw new InvalidDataException("Invalid dimension information in OME-TIFF metadata.");
            }
            string order = meta.DimensionOrder;
            List<Axis> axes = new List<Axis>();
            if (channels * zSlices * timePoints * fovNum > 1) //series
            {
                if (order.EndsWith("CZT"))
                {
                    if (channels > 1)
                    {
                        axes.Add(Axis.Channel(channels));
                    }
                    if (zSlices > 1)
                    {
                        axes.Add(Axis.Z(zSlices, originZ, originZ + (zSlices - 1) * pixelSizeZ, meta.UnitZ));
                    }
                }
                else if (order.EndsWith("ZCT"))
                {
                    if (zSlices > 1)
                    {
                        axes.Add(Axis.Z(zSlices, originZ, originZ + (zSlices - 1) * pixelSizeZ, meta.UnitZ));
                    }
                    if (channels > 1)
                    {
                        axes.Add(Axis.Channel(channels));
                    }
                }
                if (timePoints > 1)
                {
                    //現時点ではTimeのUnitは"s"固定する。書き込み時に何かを設定してた場合には失われる
                    axes.Add(Axis.Time(timePoints, meta.StartTime, meta.StartTime + (timePoints - 1) * meta.TimeStep, "s"));
                }
                if (fovNum > 1)
                {
                    var layout = meta.TileLayout;
                    var origins = meta.GlobalOrigins?.ToList();
                    if (origins != null && origins.Count == fovNum)
                    {
                        axes.Add(new FovAxis(origins, layout.X, layout.Y));
                    }
                    else
                    {
                        axes.Add(new FovAxis(layout.X, layout.Y));
                    }
                }
                md.DefineDimensions(axes.ToArray());
            }

            md.Metadata.Clear();

            if (meta.CustomParameters is not null)
            {
                var ret = JsonSerializer.Deserialize<Dictionary<string, string>>(meta.CustomParameters);
                if (ret is not null)
                {
                    foreach (var kvp in ret)
                    {
                        md.Metadata[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (meta.OMEXml is not null)//OME_XMLメタデータをそのまま保存 (CustomParametersも含まれる）
            {
                //暫定の措置
                var xmlString = HyperstackMetadata.FormatXml(meta.OMEXml);
                md.Metadata["OME_XML"] = xmlString;
            }
            return md;
        }
    }
}
