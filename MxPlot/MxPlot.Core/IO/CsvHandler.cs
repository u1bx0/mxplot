using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Provides simple CSV export and import for single-frame MatrixData.
    /// CSV format is pure numeric data without headers: comma-separated values, one row per line.
    /// </summary>
    public static class CsvHandler
    {
        /// <summary>
        /// Saves a specific frame of MatrixData to a CSV file.
        /// </summary>
        /// <typeparam name="T">The data type of matrix elements (must be unmanaged).</typeparam>
        /// <param name="path">The file path where CSV will be saved.</param>
        /// <param name="data">The MatrixData object to save.</param>
        /// <param name="separator">The separator string between values (default: ",").</param>
        /// <param name="frameIndex">The frame index to save (-1 for ActiveIndex, default: -1).</param>
        /// <param name="flipY">If true, flips Y-axis (top-left origin, default: true).</param>
        /// <exception cref="ArgumentNullException">Thrown if data or path is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if frameIndex is invalid.</exception>
        /// <exception cref="IOException">Thrown if file write operation fails.</exception>
        public static void Save<T>(string path, MatrixData<T> data, string separator=",", int frameIndex = -1, bool flipY = true) 
            where T : unmanaged
        {
            if (string.IsNullOrEmpty(path)) 
                throw new ArgumentNullException(nameof(path));
            if (data == null) 
                throw new ArgumentNullException(nameof(data));
            
            // Use ActiveIndex if frameIndex is -1
            int targetFrame = frameIndex < 0 ? data.ActiveIndex : frameIndex;
            
            if (targetFrame < 0 || targetFrame >= data.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), 
                    $"Frame index {targetFrame} is out of range [0, {data.FrameCount - 1}]");

            try
            {
                using var writer = new StreamWriter(path, false, Encoding.UTF8);
                var array = data.GetArray(targetFrame);
                int xCount = data.XCount;
                int yCount = data.YCount;

                // Write data row by row
                for (int iy = 0; iy < yCount; iy++)
                {
                    // Determine actual Y index based on flipY
                    int actualY = flipY ? (yCount - 1 - iy) : iy;
                    
                    var line = new StringBuilder();
                    for (int ix = 0; ix < xCount; ix++)
                    {
                        int index = actualY * xCount + ix;
                        double value = Convert.ToDouble(array[index]);
                        
                        if (ix > 0)
                            line.Append(separator);
                        
                        // Use invariant culture for consistent number format
                        line.Append(value.ToString("G17", CultureInfo.InvariantCulture));
                    }
                    
                    writer.WriteLine(line.ToString());
                }
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to save CSV to '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads a CSV file into a single-frame MatrixData&lt;double&gt;.
        /// </summary>
        /// <param name="path">The file path to load CSV from.</param>
        /// <param name="separator">The separator string between values (default: ",").</param>
        /// <param name="flipY">If true, flips Y-axis (assumes top-left origin, default: true).</param>
        /// <returns>A new MatrixData&lt;double&gt; instance loaded from CSV.</returns>
        /// <exception cref="ArgumentNullException">Thrown if path is null.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown if CSV format is invalid.</exception>
        public static MatrixData<double> Load(string path, string separator = ",", bool flipY = true)
        {
            return Load<double>(path, separator, flipY);
        }

        /// <summary>
        /// Loads a CSV file into a single-frame MatrixData&lt;T&gt;.
        /// </summary>
        /// <typeparam name="T">The target data type (must be unmanaged).</typeparam>
        /// <param name="path">The file path to load CSV from.</param>
        /// <param name="separator"></param>
        /// <param name="flipY">If true, flips Y-axis (assumes top-left origin, default: true).</param>
        /// <returns>A new MatrixData&lt;T&gt; instance loaded from CSV.</returns>
        /// <exception cref="ArgumentNullException">Thrown if path is null.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown if CSV format is invalid or conversion fails.</exception>
        public static MatrixData<T> Load<T>(string path, string separator = ",", bool flipY = true)
            where T : unmanaged
        {
            if (string.IsNullOrEmpty(path)) 
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) 
                throw new FileNotFoundException($"File not found: {path}");

            try
            {
                // First pass: determine dimensions
                var lines = File.ReadAllLines(path);
                return CreateFrom<T>(lines, separator, flipY);
            }
            catch (InvalidDataException)
            {
                throw; // Re-throw data format errors
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to load CSV from '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Creates a new MatrixData<T> instance by parsing a set of CSV-formatted lines.
        /// </summary>
        /// <remarks>Each value in the CSV lines is parsed using invariant culture and converted to type
        /// T. All rows must have the same number of columns. The flipY parameter can be used to control the vertical
        /// orientation of the resulting matrix.</remarks>
        /// <typeparam name="T">The type of the matrix elements. Must be an unmanaged type.</typeparam>
        /// <param name="csvLines">An array of strings, each representing a row of comma-separated values to be parsed into the matrix.</param>
        /// <param name="separator">The string used to separate values in each CSV line. Defaults to ",".</param>
        /// <param name="flipY">true to reverse the order of rows along the Y-axis (so the first line becomes the bottom row); otherwise,
        /// false. Defaults to true.</param>
        /// <returns>A MatrixData<T> containing the parsed values from the provided CSV lines.</returns>
        /// <exception cref="ArgumentException">Thrown if csvLines is null or empty.</exception>
        /// <exception cref="InvalidDataException">Thrown if any row in csvLines does not contain the same number of columns as the first row.</exception>
        public static MatrixData<T> CreateFrom<T>(string[] csvLines, string separator = ",", bool flipY = true)
            where T : unmanaged
        {
            if (csvLines == null || csvLines.Length == 0)
                throw new ArgumentException("Input lines are empty.");

            int yCount = csvLines.Length;
            // 区切り文字は動的に指定（Separatorプロパティから来る）
            var firstRowValues = csvLines[0].Split(new[] { separator }, StringSplitOptions.None);
            int xCount = firstRowValues.Length;

            var array = new T[xCount * yCount];

            for (int iy = 0; iy < yCount; iy++)
            {
                var values = csvLines[iy].Split(new[] { separator }, StringSplitOptions.None);
                if (values.Length != xCount)
                    throw new InvalidDataException($"Row {iy} has {values.Length} columns, expected {xCount}");

                int actualY = flipY ? (yCount - 1 - iy) : iy;

                for (int ix = 0; ix < xCount; ix++)
                {
                    // ここは既存の Load<T> のパースロジックを流用
                    double value = double.Parse(values[ix], CultureInfo.InvariantCulture);
                    array[actualY * xCount + ix] = (T)Convert.ChangeType(value, typeof(T));
                }
            }

            return new MatrixData<T>(xCount, yCount, array);
        }
    }

    /// <summary>
    /// Provides functionality for reading and writing matrix data in CSV (Comma-Separated Values) format.
    /// </summary>
    /// <remarks>The CsvFormat class allows customization of CSV parsing and writing behavior through its
    /// properties, such as the separator character, Y-axis flipping, and frame selection. It implements both
    /// IMatrixDataReader and IMatrixDataWriter, enabling use in scenarios that require reading from or writing to CSV
    /// files or strings. The class supports generic matrix data types and can be configured for different CSV dialects
    /// by adjusting its properties.</remarks>
    public class CsvFormat : IMatrixDataReader, IMatrixDataWriter
    {
        // 設定をプロパティとして持てる！
        public string Separator { get; set; } = ",";

        public bool FlipY { get; set; } = true;

        public int FrameIndex { get; set; } = -1;

        // インターフェイス実装：静的メソッドへ委譲
        public void Write<T>(string path, MatrixData<T> data) where T : unmanaged
        {
            CsvHandler.Save(path, data, Separator, FrameIndex, FlipY);
        }

        public MatrixData<T> Read<T>(string path) where T : unmanaged
        {
            return CsvHandler.Load<T>(path, Separator, FlipY);
        }

        /// <summary>
        /// Reads a CSV file into a single-frame MatrixData&lt;double&gt;.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public IMatrixData Read(string path)
        {
            return CsvHandler.Load(path, Separator, FlipY);
        }

        public MatrixData<T> ReadFromString<T>(string content) where T : unmanaged
        {
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return CsvHandler.CreateFrom<T>(lines, Separator, FlipY);
        }
        
        public IMatrixData ReadFromString(string content)
        {
            // CsvHandler.Load (非ジェネリック) が内部で Load<double> を呼んでいるのと同様に
            // ここでも double をデフォルトとして扱う
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            return CsvHandler.CreateFrom<double>(lines, Separator, FlipY);
        }
    }
}
