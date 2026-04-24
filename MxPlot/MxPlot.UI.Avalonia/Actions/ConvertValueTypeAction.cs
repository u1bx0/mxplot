using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Actions
{
    /// <summary>
    /// Result payload for a completed <see cref="ConvertValueTypeAction"/>.
    /// </summary>
    internal sealed record ConvertValueTypeResult(
        IMatrixData Data,
        bool ReplaceData,
        bool DoScale,
        double SrcMin, double SrcMax,
        double TgtMin, double TgtMax);

    /// <summary>
    /// Interactive action that shows the <see cref="ConvertValueTypeDialog"/>,
    /// then converts all frames of the current data to the selected numeric type.
    /// Fires <see cref="Completed"/> with the converted data on success,
    /// or <see cref="Cancelled"/> if the user dismisses the dialog.
    /// </summary>
    internal sealed class ConvertValueTypeAction : IPlotterAction
    {
        private readonly double _lutMin;
        private readonly double _lutMax;

        public event EventHandler<IMatrixData?>? Completed;
        public event EventHandler? Cancelled;
        /// <summary>Fired on the UI thread just before the background conversion begins (dialog accepted).</summary>
        public event EventHandler? ConvertingStarted;
        public event EventHandler<ConvertValueTypeResult>? ConvertCompleted;

        internal ConvertValueTypeAction(double lutMin, double lutMax)
        {
            _lutMin = lutMin;
            _lutMax = lutMax;
        }

        public void Invoke(PlotterActionContext ctx) => RunAsync(ctx);

        public void Dispose() { }

        private async void RunAsync(PlotterActionContext ctx)
        {
            var data = ctx.Data;
            if (data == null) { Cancelled?.Invoke(this, EventArgs.Empty); return; }

            var owner = TopLevel.GetTopLevel(ctx.HostVisual) as Window;
            if (owner == null) { Cancelled?.Invoke(this, EventArgs.Empty); return; }

            var dlg = new ConvertValueTypeDialog(data.ValueTypeName, _lutMin, _lutMax, srcData: data);
            var result = await dlg.ShowCenteredOnAsync(owner, ctx.HostVisual);

            if (result == null) { Cancelled?.Invoke(this, EventArgs.Empty); return; }

            var (targetType, doScale, replaceData, srcMin, srcMax, tgtMin, tgtMax) = result.Value;

            ConvertingStarted?.Invoke(this, EventArgs.Empty);

            IMatrixData converted;
            try
            {
                converted = await Task.Run(() =>
                    data.ConvertToType(targetType, doScale, srcMin, srcMax, tgtMin, tgtMax));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConvertValueTypeAction] {ex}");
                Cancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            converted.CopyPropertiesFrom(data);
            ConvertCompleted?.Invoke(this, new ConvertValueTypeResult(converted, replaceData, doScale, srcMin, srcMax, tgtMin, tgtMax));
            Completed?.Invoke(this, converted);
        }
    }
}
