using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.IO.CacheStrategies
{
    public class NeighborStrategy : ICacheStrategy
    {
        private readonly int _lookAhead;  // number of frames to prefetch in the forward direction
        private readonly int _lookBehind; // number of frames to keep in the backward direction

        public NeighborStrategy(int lookAhead = 4, int lookBehind = 1)
        {
            _lookAhead = lookAhead;
            _lookBehind = lookBehind;
            StrategyChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? StrategyChanged;

        /// <summary>
        /// Returns the preload target indices around <paramref name="currentIndex"/>.
        /// Forward frames are listed first (highest priority), followed by backward frames,
        /// so the caller receives them in nearest-first order.
        /// </summary>
        public IEnumerable<int> GetPreloadIndices(int currentIndex, int totalCount)
        {
            var targets = new List<int>();

            // 1. Forward frames first — most likely to be needed next.
            for (int i = 1; i <= _lookAhead; i++)
            {
                int target = currentIndex + i;
                if (target < totalCount) targets.Add(target);
            }

            // 2. Backward frames second — useful when the user scrolls in reverse.
            for (int i = 1; i <= _lookBehind; i++)
            {
                int target = currentIndex - i;
                if (target >= 0) targets.Add(target);
            }

            return targets;
        }

        /// <summary>
        /// This simple strategy does not designate any frame as high-priority;
        /// all frames are treated equally for eviction purposes.
        /// </summary>
        public bool IsHighPriority(int index) => false;
        
    }
}
