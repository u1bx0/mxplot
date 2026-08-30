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
        // The axis whose values are combined/overlaid together in Composite mode. Historically
        // this was hardcoded to an axis literally named "Channel", but Composite mode itself was
        // generalized to work with any axis -- so this must be settable to whatever axis is
        // actually configured, not just "Channel" by name.
        private int _compositeAxisIndex = -1;
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

      

        /// <summary>
        /// The axis used for Composite-mode overlay (all its values are preloaded/protected
        /// together, alongside <see cref="TargetAxis"/>, instead of just the current one).
        /// </summary>
        public Axis? CompositeAxis
        {
            get => _compositeAxisIndex >= 0 ? _dim[_compositeAxisIndex] : null;
            set
            {
                int index = (value != null) ? _dim.GetAxisOrder(value) : -1;
                if (_compositeAxisIndex != index)
                {
                    _compositeAxisIndex = index;
                    OnStrategyChanged();
                }
            }
        }

        private int _lastCurrentIndex = 0;

        // Memoizes GetPreloadIndices' Volume-mode target set (see the comment inside
        // GetPreloadIndices for why this is valid and why it matters). Invalidated in
        // OnStrategyChanged, since that already fires whenever TargetAxis, CompositeAxis, Mode,
        // or the target-channel filter change -- every input this memo depends on besides the
        // per-call axis position itself.
        private int[]? _cachedVolumeContext;
        private List<int>? _cachedVolumeTargets;

        public event EventHandler? StrategyChanged;

        /// <summary>
        /// Initializes the strategy with a <see cref="DimensionStructure"/>, the axis to use as
        /// the primary scroll / volume target, and (optionally) the axis used for Composite-mode
        /// overlay.
        /// </summary>
        /// <param name="compositeAxis">
        /// The Composite-mode axis. When <see langword="null"/> (default), falls back to an axis
        /// literally named "Channel" if one exists, for backward compatibility with data that
        /// still uses that convention -- pass this explicitly whenever Composite mode is actually
        /// configured on a different axis.
        /// </param>
        public DimensionStrategy(DimensionStructure dimStruct, Axis target, Axis? compositeAxis = null)
        {
            _dim = dimStruct;

            _targetAxisIndex = dimStruct.GetAxisOrder(target);
            TargetAxis = target;

            CompositeAxis = compositeAxis ?? (dimStruct.Contains("Channel") ? dimStruct["Channel"] : null);
        }

        private void OnStrategyChanged()
        {
            _cachedVolumeContext = null;
            _cachedVolumeTargets = null;
            StrategyChanged?.Invoke(this, EventArgs.Empty);
        }


        /// <summary>
        /// Enumerates the channel indices that are actually in scope for this strategy.
        /// </summary>
        private IEnumerable<int> GetEffectiveChannels()
        {
            if (_compositeAxisIndex < 0)
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
                int count = _dim.Axes[_compositeAxisIndex].Count;
                for (int c = 0; c < count; c++) yield return c;
            }
        }

        /// <summary>
        /// Enumerates every frame index in the current Volume-mode working set: every value of
        /// <see cref="TargetAxis"/>, times every value of <see cref="CompositeAxis"/> (if any),
        /// with every other axis fixed at <paramref name="currentIndex"/>'s position -- INCLUDING
        /// <paramref name="currentIndex"/> itself, unlike <see cref="GetPreloadIndices"/> (which
        /// omits it deliberately, since that method answers "what else needs prefetching" rather
        /// than "what is the whole set"). Intended for diagnostics (e.g. reporting how much of the
        /// working set a cache currently holds), not for driving eviction/prefetch decisions.
        /// Empty when <see cref="Mode"/> isn't <see cref="CacheMode.Volume"/>, or <see cref="TargetAxis"/>
        /// is unset.
        /// </summary>
        public IEnumerable<int> GetVolumeWorkingSet(int currentIndex)
        {
            if (Mode != CacheMode.Volume || _targetAxisIndex < 0) yield break;
            if (_dim.Axes.Count == 0) yield break;

            int[] currentPos = _dim.GetAxisIndices(currentIndex);
            int targetLength = _dim.Axes[_targetAxisIndex].Count;

            for (int t = 0; t < targetLength; t++)
                foreach (int c in GetEffectiveChannels())
                    yield return GetFrameIndex(currentPos, _targetAxisIndex, t, c);
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

                // The target set here never depends on TargetAxis's own current value -- every
                // value of TargetAxis is always included regardless of which one "currentIndex"
                // happens to sit on -- only on the OTHER axes' positions (e.g. which Time point).
                // So every call within one full sweep along TargetAxis (e.g. one orthogonal
                // slice-build pass, which touches every frame along it in turn) shares the exact
                // same target set. Recomputing the full TargetAxis x CompositeAxis enumeration
                // (up to targetLength * channelCount GetFrameIndex calls) on every single one of
                // those calls was, measured empirically, the dominant remaining per-access cost
                // in VirtualFrames after its own lock/LRU overhead was fixed -- for a fully
                // "warmed" cache this meant every touch still redid the same expensive work for
                // no benefit. Memoize it, keyed by everything except TargetAxis's own value.
                if (_cachedVolumeContext == null || !ContextMatchesExceptTargetAxis(currentPos, _cachedVolumeContext))
                {
                    int targetLength = _dim.Axes[_targetAxisIndex].Count;
                    var targets = new List<int>(targetLength * Math.Max(1, _targetChannels.Count > 0 ? _targetChannels.Count : 1));
                    for (int t = 0; t < targetLength; t++)
                        foreach (int c in GetEffectiveChannels())
                            targets.Add(GetFrameIndex(currentPos, _targetAxisIndex, t, c));

                    _cachedVolumeContext = currentPos; // currentPos is a fresh array from GetAxisIndices above; safe to retain
                    _cachedVolumeTargets = targets;
                }

                foreach (int targetIndex in _cachedVolumeTargets!)
                    if (targetIndex != currentIndex) yield return targetIndex;
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
            Span<int> pos = stackalloc int[_dim.Axes.Count];
            basePos.CopyTo(pos);

            if (targetAxisIdx >= 0) pos[targetAxisIdx] = targetVal;
            if (_compositeAxisIndex >= 0) pos[_compositeAxisIndex] = channelVal;

            return _dim.GetFrameIndexAt(pos);
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> agree on every axis except
        /// <see cref="_targetAxisIndex"/> -- the equality that makes Volume mode's memoized
        /// preload target set (see <see cref="GetPreloadIndices"/>) still valid to reuse.
        /// </summary>
        private bool ContextMatchesExceptTargetAxis(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (i == _targetAxisIndex) continue;
                if (a[i] != b[i]) return false;
            }
            return true;
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
            if (_compositeAxisIndex >= 0)
            {
                int evalC = evalPos[_compositeAxisIndex];
                if (_targetChannels.Count > 0 && !_targetChannels.Contains(evalC))
                    return true;
            }

            // --- Verify that all axes other than TargetAxis and ChannelAxis (the "context" axes) match. ---
            for (int i = 0; i < _dim.Axes.Count; i++)
            {
                if (i == _compositeAxisIndex) continue; // channel differences are acceptable

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
