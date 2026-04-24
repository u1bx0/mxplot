using MxPlot.Core.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection.Metadata.Ecma335;
using System.Text;

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
        /// RoutedFramesのindex -> _frameListのindex
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        private int GetMappedIndex(int index)
        {
            if (index < 0 || index >= _mappedIndex.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _mappedIndex[index];
        }

        public T[] this[int index]
        {
            get
            {
                //indexはRoutedFramesのzero-based index、GetMappedIndexでframeListのindexに変換してからアクセスする
                return _frameList[GetMappedIndex(index)];
            }
            set
            {
                throw new NotSupportedException();
            }
        }

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
            // 親が特殊なプロバイダでなければ、そのまま配列の参照を返す
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
            //arrayIndexはコピー先の配列のインデックス、
            //つまり、array[arrayIndex]からCount個分の要素をコピーする
            //
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("array.Length is less than arrayIndex + Count");

            // 自分の要素を、指定されたインデックスから順番に詰め込むだけ
            for (int i = 0; i < Count; i++)
            {
                // ここで this[i] を呼ぶので、インデックスのルーティングや
                // キャッシュからの読み出しは今まで通り完璧に機能します！
                array[arrayIndex + i] = this[i];
            }
        }

        public IEnumerator<T[]> GetEnumerator() { for (int i = 0; i < Count; i++) { yield return this[i]; } }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();


        public int IndexOf(T[] item)
        {
            for (int i = 0; i < Count; i++)
            {
                // 参照の比較（配列のポインタが同じか）
                if (EqualityComparer<T[]>.Default.Equals(this[i], item))
                {
                    return i;
                }
            }
            return -1;
        }


        public void Insert(int index, T[] item) => throw new NotSupportedException();
        public void Add(T[] item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Remove(T[] item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

    }

}
