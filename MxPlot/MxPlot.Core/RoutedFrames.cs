using MxPlot.Core.IO;
using System.Collections;

namespace MxPlot.Core
{

    public class RoutedFrames<T> : IList<T[]>, IFrameKeyProvider<T>, IWritableFrameProvider<T> where T : unmanaged
    {

        private readonly IList<T[]> _frameList;
        private readonly List<int> _mappedIndex;

        public RoutedFrames(IList<T[]> frameList, List<int> order)
        {
            _frameList = frameList ?? throw new ArgumentNullException(nameof(frameList));
            _mappedIndex = order ?? throw new ArgumentNullException(nameof(order));
        }

        /// <summary>
        /// Returns the mapped index in the underlying frame list for a given RoutedFrames index.
        ///
        /// </summary>
        /// <param name="index"></param>
        /// <returns>The mapped index</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public int GetMappedIndex(int index)
        {
            if (index < 0 || index >= _mappedIndex.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _mappedIndex[index];
        }

        public T[] this[int index]
        {
            get
            {
                // index is a zero-based RoutedFrames index; convert it to the frameList index via GetMappedIndex before accessing.
                return _frameList[GetMappedIndex(index)];
            }
            set
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Gets whether the underlying frame list currently does not hold every frame resident in
        /// memory -- forwards to <see cref="ILazyDataSource.IsVirtual"/>, or <see langword="false"/>
        /// for a plain in-memory list.
        /// </summary>
        public bool IsVirtual => _frameList is ILazyDataSource lazy && lazy.IsVirtual;

        public int Count => _mappedIndex.Count;

        public bool IsReadOnly => _frameList.IsReadOnly;

        public T[] GetKey(int frameIndex)
        {
            
            if(_frameList is IFrameKeyProvider<T> provider)
            {
                return provider.GetKey(GetMappedIndex(frameIndex));
            }
            else
            {
                return _frameList[GetMappedIndex(frameIndex)];    
            }
        }

        public T[] GetWritableArray(int index)
        {
            if (_frameList is IWritableFrameProvider<T> provider)
            {
                return provider.GetWritableArray(GetMappedIndex(index));
            }
            // e.g. InMemory frame list
            return _frameList[GetMappedIndex(index)];
        }

        public void WriteDirectly(int frameIndex, T[] data)
        {
            if (_frameList is IWritableFrameProvider<T> provider)
                provider.WriteDirectly(GetMappedIndex(frameIndex), data);
            else
                throw new NotSupportedException("The underlying frame list does not support direct writes.");
        }

        public void Flush()
        {
            if (_frameList is IWritableFrameProvider<T> provider)
                provider.Flush();
        }

        public void SaveAs(string path, Action<string>? beforeRemount = null, IProgress<int>? progress = null)
        {
            if (_frameList is IWritableFrameProvider<T> provider)
                provider.SaveAs(path, beforeRemount, progress);
            else
                throw new NotSupportedException("The underlying frame list does not support saving.");
        }

        public bool Contains(T[] item)
        {
           return IndexOf(item) >= 0;
        }

        public void CopyTo(T[][] array, int arrayIndex)
        {
            // arrayIndex is the index in the destination array,
            // so copy Count elements starting at array[arrayIndex].
            //
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("array.Length is less than arrayIndex + Count");

            // Pack this collection's elements sequentially from the specified index.
            for (int i = 0; i < Count; i++)
            {
                // Calling this[i] keeps index routing and cache reads working as expected.
                array[arrayIndex + i] = this[i];
            }
        }

        public IEnumerator<T[]> GetEnumerator() { for (int i = 0; i < Count; i++) { yield return this[i]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();


        /// <summary>
        /// Returns the first index whose frame key (<see cref="GetKey"/>) is <paramref name="item"/>
        /// by reference, or -1 -- the same key-based contract as the underlying Virtual frame lists.
        /// </summary>
        /// <remarks>
        /// Compares keys, not frame data: for an in-memory list the key is the frame array itself, so
        /// the result is unchanged, but over a Virtual list the key is a dummy array. Comparing it
        /// against <c>this[i]</c> used to read (decode/copy) every frame through the underlying
        /// list and could never match.
        /// </remarks>
        public int IndexOf(T[] item)
        {
            for (int i = 0; i < Count; i++)
            {
                if (ReferenceEquals(GetKey(i), item))
                    return i;
            }
            return -1;
        }


        public void Insert(int index, T[] item) => throw new NotSupportedException();
        public void Add(T[] item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(T[] item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

        /// <summary>
        /// Gets the underlying frame list if it implements <see cref="ICacheableFrameList"/>;
        /// otherwise, returns null. One accessor for every on-demand backend -- see
        /// <see cref="IMatrixData.GetDiagnosticCacheableList"/>'s own remarks.
        /// </summary>
        internal ICacheableFrameList? GetUnderlyingCacheableList() => _frameList as ICacheableFrameList;
    }

}
