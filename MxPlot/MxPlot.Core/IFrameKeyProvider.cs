using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    public interface IFrameKeyProvider<T> where T: unmanaged
    {
        T[] GetKey(int frameIndex);
    }
}
