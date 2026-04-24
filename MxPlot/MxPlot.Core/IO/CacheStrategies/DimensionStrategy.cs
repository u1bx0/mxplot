using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;

namespace MxPlot.Core.IO.CacheStrategies
{

    public class DimensionStrategy : ICacheStrategy
    {
        private readonly DimensionStructure _dim;

        private int _targetAxisIndex = -1;
        private readonly int _channelAxisIndex = -1;
        private HashSet<int> _targetChannels = new();

        public enum CacheMode { SinglePlane, Volume }

        private CacheMode _mode = CacheMode.SinglePlane;
        public CacheMode Mode
        {
            get => _mode;
            set
            {
                if (_mode != value)
                {
                    _mode = value;
                    OnStrategyChanged();
                }
            }
        }

        public Axis? TargetAxis
        {
            get=> _targetAxisIndex >= 0 ? _dim[_targetAxisIndex] : null;
            set
            {
                int index = (value != null) ? _dim.GetAxisOrder(value) : -1;
                if (_targetAxisIndex != index)
                {
                    _targetAxisIndex = index;
                    OnStrategyChanged();
                }
            }
        }

      

        private int _lastCurrentIndex = 0;

        public event EventHandler? StrategyChanged;

        /// <summary>
        /// Initializes the strategy with a <see cref="DimensionStructure"/> and the axis
        /// to use as the primary scroll / volume target.
        /// </summary>
        public DimensionStrategy(DimensionStructure dimStruct, Axis target)
        {
            _dim = dimStruct;

            _targetAxisIndex = dimStruct.GetAxisOrder(target);
            _channelAxisIndex = dimStruct.Contains("Channel") ? dimStruct.GetAxisOrder("Channel") : -1;
            TargetAxis = target;
        }

        private void OnStrategyChanged()
        {
            StrategyChanged?.Invoke(this, EventArgs.Empty);
        }


        /// <summary>
        /// Enumerates the channel indices that are actually in scope for this strategy.
        /// </summary>
        private IEnumerable<int> GetEffectiveChannels()
        {
            if (_channelAxisIndex < 0)
            {
                yield return 0; // no channel axis present; treat as a single implicit channel
                yield break;
            }

            if (_targetChannels.Count > 0)
            {
                foreach (var c in _targetChannels) yield return c;
            }
            else
            {
                // No filter specified — include all channels.
                int count = _dim.Axes[_channelAxisIndex].Count;
                for (int c = 0; c < count; c++) yield return c;
            }
        }

        /// <summary>
        /// Sets the collection of target channel identifiers for the strategy. <br/>
        /// <c>e.g. SetTargetChannels([0, 1, 2, 3])</c>
        /// </summary>
        /// <remarks>Calling this method updates the internal set of target channels and triggers a change
        /// notification via the OnStrategyChanged method.</remarks>
        /// <param name="channels">The collection of channel identifiers to assign as the target channels. Cannot be null.</param>
        public void SetTargetChannels(IEnumerable<int> channels)
        {
            _targetChannels = new HashSet<int>(channels);
            OnStrategyChanged();
        }

        public IEnumerable<int> GetPreloadIndices(int currentIndex, int totalCount)
        {
            _lastCurrentIndex = currentIndex;
            if (_dim.Axes.Count == 0) yield break;

            // Resolve the current N-dimensional position (one allocation per call).
            int[] currentPos = _dim.GetAxisIndices(currentIndex);

            if (Mode == CacheMode.Volume)
            {
                // --- Volume mode: preload the entire target axis for all effective channels ---
                if (_targetAxisIndex < 0) yield break;
                int targetLength = _dim.Axes[_targetAxisIndex].Count;

                for (int t = 0; t < targetLength; t++)
                {
                    foreach (int c in GetEffectiveChannels())
                    {
                        // Delegate to the non-yield helper so stackalloc can be used inside.
                        int targetIndex = GetFrameIndex(currentPos, _targetAxisIndex, t, c);
                        if (targetIndex != currentIndex) yield return targetIndex;
                    }
                }
            }
            else
            {
                // --- SinglePlane mode ---
                // 1. Same position, different channels (for simultaneous multi-channel display).
                foreach (int c in GetEffectiveChannels())
                {
                    int targetIndex = GetFrameIndex(currentPos, -1, 0, c);
                    if (targetIndex != currentIndex && targetIndex < totalCount && targetIndex >= 0)
                        yield return targetIndex;
                }

                // 2. Adjacent frames along the target axis (scroll look-ahead/look-behind).
                if (_targetAxisIndex >= 0)
                {
                    int currentTargetVal = currentPos[_targetAxisIndex];
                    int targetLength = _dim.Axes[_targetAxisIndex].Count;

                    // Use a plain array rather than stackalloc because this is a yield iterator.
                    int[] offsets = { 1, -1 };

                    foreach (int offset in offsets)
                    {
                        int nextTarget = currentTargetVal + offset;
                        if (nextTarget >= 0 && nextTarget < targetLength)
                        {
                            foreach (int c in GetEffectiveChannels())
                            {
                                // GetFrameIndex is a non-iterator method, so stackalloc is safe there.
                                int targetIndex = GetFrameIndex(currentPos, _targetAxisIndex, nextTarget, c);
                                yield return targetIndex;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Non-iterator helper that resolves a frame index from a base position with
        /// axis overrides. Extracted from the yield iterator so that <c>stackalloc</c> is allowed.
        /// </summary>
        private int GetFrameIndex(int[] basePos, int targetAxisIdx, int targetVal, int channelVal)
        {
            // yield の外なので stackalloc が使える！
            Span<int> pos = stackalloc int[_dim.Axes.Count];
            basePos.CopyTo(pos);

            if (targetAxisIdx >= 0) pos[targetAxisIdx] = targetVal;
            if (_channelAxisIndex >= 0) pos[_channelAxisIndex] = channelVal;

            return _dim.GetFrameIndexAt(pos);
        }

        private int CalculateIndex(int[] basePos, int axis1, int val1, int axis2, int val2)
        {
            Span<int> pos = stackalloc int[_dim.Axes.Count];
            basePos.CopyTo(pos);

            if (axis1 >= 0) pos[axis1] = val1;
            if (axis2 >= 0) pos[axis2] = val2;

            return _dim.GetFrameIndexAt(pos);
        }

        public bool IsHighPriority(int index)
        {
            if (_dim.Axes.Count == 0) return false;

            // stackalloc is safe here because IsHighPriority is a non-iterator method.
            Span<int> currentPos = stackalloc int[_dim.Axes.Count];
            _dim.CopyAxisIndicesTo(currentPos, _lastCurrentIndex);

            Span<int> evalPos = stackalloc int[_dim.Axes.Count];
            _dim.CopyAxisIndicesTo(evalPos, index);

            // Frames outside the target channel set are not high-priority.
            if (_channelAxisIndex >= 0)
            {
                int evalC = evalPos[_channelAxisIndex];
                if (_targetChannels.Count > 0 && !_targetChannels.Contains(evalC))
                    return true;
            }

            // --- Verify that all axes other than TargetAxis and ChannelAxis (the "context" axes) match. ---
            for (int i = 0; i < _dim.Axes.Count; i++)
            {
                if (i == _channelAxisIndex) continue; // channel differences are acceptable

                if (Mode == CacheMode.Volume && i == _targetAxisIndex)
                    continue; // in Volume mode, variation along the target axis (Z, T, etc.) is expected

                // Any other axis (e.g., T when Target=Z, Channel=C) that differs from the
                // current view state indicates data belonging to a different context — safe to evict.
                if (currentPos[i] != evalPos[i])
                {
                    return false;
                }
            }

            // All context axes match — this frame belongs to the current view and should be protected.
            return true;
        }
    }

}
