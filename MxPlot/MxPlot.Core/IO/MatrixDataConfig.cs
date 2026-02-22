using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Configuration for MatrixData, encompassing dimensionality, physical scales, 
    /// UI states, and statistical caches for persistence and data exchange.
    /// </summary>
    public record class MatrixDataConfig
    {
        public const int CurrentVersion = 1;

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixDataConfig"/> class.
        /// Primarily for serialization purposes.
        /// </summary>
        public MatrixDataConfig() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixDataConfig"/> class from an <see cref="IMatrixData"/> source.
        /// Automatically populates statistical caches and UI states.
        /// </summary>
        /// <param name="data">The source matrix data.</param>
        public MatrixDataConfig(IMatrixData data)
        {
            Version = CurrentVersion;
            ValueTypeName = data.ValueType?.FullName ?? string.Empty;
            XCount = data.XCount;
            YCount = data.YCount;
            FrameCount = data.FrameCount;
            XMin = data.XMin;
            XMax = data.XMax;
            YMin = data.YMin;
            YMax = data.YMax;
            XUnit = data.XUnit;
            YUnit = data.YUnit;
            ActiveIndex = data.ActiveIndex;

            Axes = data.Axes?.ToArray();
            Metadata = data.Metadata != null ? new Dictionary<string, string>(data.Metadata) : new();

            // Extract value ranges for each frame.
            // FAIL-SAFE: If the data type (T) is a custom struct without a registered MinMaxFinder,
            // GetValueRangeList will return [double.NaN] via the internal RefreshValueRange logic.
            // This ensures the config remains valid and serializable even without statistical data.
            for (int i = 0; i < data.FrameCount; i++)
            {
                var (mins, maxs) = data.GetValueRangeList(i);
                MinValueList.Add(new List<double>(mins));
                MaxValueList.Add(new List<double>(maxs));
            }
        }

        #region Identification and Type Information

        /// <summary>
        /// Gets the configuration format version.
        /// </summary>
        public int Version { get; init; } = CurrentVersion;

        /// <summary>
        /// Gets whether the binary data payload is compressed using GZip.
        /// </summary>
        public bool IsCompressed { get; init; } = false;

        /// <summary>
        /// Gets the full .NET Type name of the stored values. 
        /// Used for dynamic type resolution during data restoration.
        /// </summary>
        public string ValueTypeName { get; init; } = string.Empty;

        /// <summary>
        /// Gets a friendly C# alias for the value type. 
        /// Supports standard numeric types (signed/unsigned) and falls back to the short type name for custom structs.
        /// </summary>
        public string ValueTypeAlias => ValueTypeName switch
        {
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Numerics.Complex" => "Complex",
            // Fallback for custom unmanaged structs: Extract the class name from the namespace
            string s when s.Contains('.') => s.Split('.').Last(),
            _ => ValueTypeName
        };

        #endregion

        #region Dimensionality and Physical Scale

        public int XCount { get; init; }
        public int YCount { get; init; }
        public int FrameCount { get; init; }
        public double XMin { get; init; }
        public double XMax { get; init; }
        public double YMin { get; init; }
        public double YMax { get; init; }
        public string? XUnit { get; init; }
        public string? YUnit { get; init; }

        #endregion

        #region UI States and Extended Metadata

        /// <summary>
        /// Gets the index of the frame that was last active or selected in the UI.
        /// </summary>
        public int ActiveIndex { get; init; } = 0;

        /// <summary>
        /// Gets additional axis definitions.
        /// </summary>
        public Axis[]? Axes { get; init; }

        /// <summary>
        /// Gets a dictionary of custom metadata key-value pairs.
        /// </summary>
        public Dictionary<string, string> Metadata { get; init; } = new();

        #endregion

        #region Statistical Cache (Persistence)

        /// <summary>
        /// Gets the cached minimum values. 
        /// Outer list: Frame index, Inner list: Channel index.
        /// </summary>
        public List<List<double>> MinValueList { get; init; } = new();

        /// <summary>
        /// Gets the cached maximum values. 
        /// Outer list: Frame index, Inner list: Channel index.
        /// </summary>
        public List<List<double>> MaxValueList { get; init; } = new();

        #endregion

        #region Utility Methods for Serialization and Desirialization

        private static readonly JsonSerializerOptions _options = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // FAIL-SAFE: Allow NaN/Infinity for custom types that cannot provide valid numeric statistics
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        /// <summary>
        /// Converts the config to a JSON string. 
        /// Gracefully handles NaN values if the data type does not support min/max calculation.
        /// </summary>
        public string ToHeaderString() => JsonSerializer.Serialize(this, _options);

        /// <summary>
        /// Deserializes a JSON header string back into a <see cref="MatrixDataConfig"/> instance.
        /// </summary>
        /// <param name="header">The JSON string from the file header.</param>
        public static MatrixDataConfig? FromHeaderString(string header)
            => JsonSerializer.Deserialize<MatrixDataConfig>(header, _options);

        #endregion
    }

    public static class MatrixDataConfigExtensions
    {
        public static MatrixData<T> CreateNewInstance<T>(this MatrixDataConfig config, List<T[]> arrays)
            where T : unmanaged
        {
            if (arrays.Count != config.FrameCount)
                throw new ArgumentException($"The number of provided arrays ({arrays.Count}) does not match the expected frame count ({config.FrameCount}).");

            MatrixData<T>? md = null;

            var min = config.MinValueList; // = new() at initialization, but should be populated from the config
            var max = config.MaxValueList; // = new() 
            if (min.Count == max.Count)
            {
                if (arrays.Count != min.Count)
                    throw new ArgumentException($"The number of arrays ({arrays.Count}) does not match the value range cashes ({min.Count})");
                md = new MatrixData<T>(config.XCount, config.YCount, arrays, min, max);
                md.SetXYScale(config.XMin, config.XMax, config.YMin, config.YMax);
                md.XUnit = config.XUnit ?? "";
                md.YUnit = config.YUnit ?? "";
                md.DefineDimensions(config.Axes ?? []);
                foreach (var key in config.Metadata.Keys)
                {
                    md.Metadata[key] = config.Metadata[key];
                }
                md.ActiveIndex = config.ActiveIndex;
            }
            if (md == null)
                throw new InvalidOperationException("Failed to create MatrixData instance from config. Value range caches are missing or invalid.");
            return md;
        }
    }
}