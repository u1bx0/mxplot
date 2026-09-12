namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Whether a bitmap writer's pixel loop runs across cores.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IBitmapWriter.ParallelOptions"/> so that "how many threads, with
    /// what scheduler" stays independent of "should this go parallel at all". A caller that only
    /// wants to cap the degree of parallelism sets the options; a caller that wants the cores left
    /// alone sets <see cref="Never"/> without having to know what options would mean.
    /// </remarks>
    public enum ParallelRenderPolicy
    {
        /// <summary>
        /// Serial for small frames, parallel once the frame is large enough to pay for the split.
        /// The default, and the right answer unless the caller knows something extra.
        /// </summary>
        Auto,

        /// <summary>
        /// Always serial, whatever the frame size. For a host that would rather spend its cores on
        /// something else - an acquisition or processing thread that matters more than redraw
        /// latency - or that is rendering on a machine where a full-width parallel burst disturbs
        /// other work.
        /// </summary>
        Never,

        /// <summary>
        /// Always parallel, whatever the frame size. Mostly useful for measurement, and for a
        /// caller that has supplied its own <see cref="IBitmapWriter.ParallelOptions"/> and means
        /// them to apply to every frame.
        /// </summary>
        Always,
    }
}
