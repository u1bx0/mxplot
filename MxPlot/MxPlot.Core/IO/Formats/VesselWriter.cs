using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MxPlot.Core.IO.Formats
{
    /// <summary>
    /// Writes frames to a growing .mxd file one at a time, with the total frame count unknown
    /// until <see cref="Complete"/> is called — unlike
    /// <see cref="MatrixDataSerializer.CreateVessel{T}"/>, which pre-allocates the whole file for
    /// a frame count fixed at construction time. Created via
    /// <see cref="MatrixDataSerializer.CreateStreamingVessel{T}"/>.
    /// </summary>
    /// <remarks>
    /// Backed by a plain sequential <see cref="FileStream"/>, not a memory-mapped
    /// <c>VirtualFrames&lt;T&gt;</c> — that hierarchy's frame count is fixed at construction
    /// throughout (<c>VirtualFrames&lt;T&gt;</c>, <c>MmfFrames&lt;T&gt;</c>), which is
    /// fundamentally incompatible with a frame count that grows for as long as the caller keeps
    /// calling <see cref="WriteFrame"/>. Once <see cref="Complete"/> finalizes the file, it is a
    /// completely ordinary, independently openable .mxd — read it back through the usual
    /// <see cref="MatrixDataSerializer.LoadVirtual{T}"/>/<see cref="MatrixDataSerializer.Load{T}"/>
    /// path, not through this class.
    /// </remarks>
    public sealed class VesselWriter<T> : IDisposable where T : unmanaged
    {
        private readonly FileStream _fs;
        private readonly BinaryWriter _writer;
        private readonly int _xcount, _ycount;
        private readonly int _frameLength;
        private int _frameCount;
        private bool _completed;
        private bool _disposed;

        internal VesselWriter(FileStream fs, BinaryWriter writer, int xcount, int ycount)
        {
            _fs = fs;
            _writer = writer;
            _xcount = xcount;
            _ycount = ycount;
            _frameLength = xcount * ycount;
        }

        /// <summary>
        /// Writes one frame (<paramref name="frame"/>.Length must equal <c>xcount * ycount</c>)
        /// and returns its 0-based index.
        /// </summary>
        /// <remarks>
        /// Fully synchronous: the bytes are written to the underlying file before this returns,
        /// and no reference to <paramref name="frame"/> is retained afterward — the caller may
        /// immediately overwrite/reuse the same array for the next call. No per-frame allocation
        /// or pooling is required on either side of this call. (If a future version buffers or
        /// writes asynchronously, this non-retaining contract must be preserved or reconsidered
        /// together with every caller that currently relies on reusing one scratch array.)
        /// </remarks>
        public int WriteFrame(T[] frame)
        {
            // _completed checked first: Complete() itself leaves _disposed true too (it closes the
            // stream), so checking _disposed first would report the wrong exception for the most
            // common misuse (calling WriteFrame after Complete) - ObjectDisposedException is only
            // the right signal for the Dispose()-without-Complete() abandonment case.
            if (_completed)
                throw new InvalidOperationException($"Cannot call {nameof(WriteFrame)} after {nameof(Complete)}.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.Length != _frameLength)
                throw new ArgumentException(
                    $"frame.Length ({frame.Length}) does not match xcount*ycount ({_frameLength}).", nameof(frame));

            _fs.Write(MemoryMarshal.AsBytes(frame.AsSpan()));
            return _frameCount++;
        }

        /// <summary>
        /// Finalizes the file: writes the JSON trailer and back-patches the fixed header, turning
        /// this from a temp vessel (<c>ConfigOffset == 0</c>) into an ordinary, independently
        /// openable .mxd. Closes the underlying stream — no further <see cref="WriteFrame"/> calls
        /// are possible afterward, and calling <see cref="Complete"/> again throws.
        /// </summary>
        /// <param name="axes">
        /// Extra axes beyond X/Y — the same shape <see cref="IMatrixData.DefineDimensions"/> takes
        /// elsewhere (e.g. <c>ColorAxis.CreateRgb()</c> plus an
        /// <see cref="Axis.IndexBased(string, int)"/> for the remaining frame count, for an RGB
        /// recording where every 3 <see cref="WriteFrame"/> calls are one R/G/B moment). Omit for
        /// a plain single-axis stack — a <c>"Frame"</c> axis sized to however many frames were
        /// actually written is used automatically (matching <c>StackController</c>'s own
        /// <c>Axis.IndexBased("Frame", captureCount)</c> convention for the same shape).
        /// <para>
        /// If the product of the given axes' <see cref="Axis.Count"/> values does not equal the
        /// number of frames actually written, this logs via <see cref="Debug.WriteLine(string)"/>
        /// and falls back to the plain single-axis form instead of producing a file whose declared
        /// shape does not match its own data.
        /// </para>
        /// </param>
        public void Complete(params Axis[] axes)
        {
            // Same ordering reasoning as WriteFrame's own guard.
            if (_completed)
                throw new InvalidOperationException($"{nameof(Complete)} was already called.");
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (axes.Length > 0)
            {
                long product = 1;
                foreach (var axis in axes) product *= axis.Count;
                if (product != _frameCount)
                {
                    Debug.WriteLine(
                        $"[VesselWriter.Complete] axes product ({product}) does not match frames " +
                        $"written ({_frameCount}); falling back to a single \"Frame\" axis.");
                    axes = [];
                }
            }
            Axis[] finalAxes = axes.Length > 0 ? axes : [Axis.IndexBased("Frame", _frameCount)];

            long dataLength = (long)_frameCount * _frameLength * Unsafe.SizeOf<T>();
            var config = new MatrixDataConfig
            {
                Version = MatrixDataConfig.CurrentVersion,
                ValueTypeName = typeof(T).FullName ?? "",
                XCount = _xcount,
                YCount = _ycount,
                FrameCount = _frameCount,
                XMax = _xcount - 1,
                YMax = _ycount - 1,
                Axes = finalAxes,
                IsCompressed = false,
            };
            MatrixDataSerializer.WriteTrailerAndPatchHeader(_fs, _writer, dataLength, config);

            _completed = true;
            _writer.Dispose();
            _fs.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Safety net for a recording that never called <see cref="Complete"/> (e.g. the app
        /// crashed, or the camera disconnected mid-session): releases the file handle only,
        /// leaving <c>ConfigOffset == 0</c> — the same "temp/unfinalized" marker
        /// <see cref="MatrixDataSerializer.CreateTempVessel{T}"/> already uses, so nothing new for
        /// the rest of the format to know about. Writes no trailer. A no-op if <see cref="Complete"/>
        /// already ran (which disposes the stream itself); safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _writer.Dispose();
            _fs.Dispose();
            _disposed = true;
        }
    }
}
