using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Converts every frame to another numeric type, optionally rescaling the values. The whole data is
    /// converted, so there is no "This frame only", Sync or Composite handling.
    /// </summary>
    internal sealed class ConvertValueTypeCommand : IProcessingCommand
    {
        public async Task RunAsync(ICommandHost host)
        {
            var data = host.Data;
            if (data == null) return;

            var (lutMin, lutMax) = host.DisplayedRange;
            var dialog = new ConvertValueTypeDialog(
                data.ValueTypeName, lutMin, lutMax, srcData: data, isLinkWindow: host.IsReplaceBlocked);
            var choice = await dialog.ShowCenteredOnAsync(host.Owner, host.Owner);
            if (choice == null) return;
            var (targetType, doScale, replaceData, srcMin, srcMax, tgtMin, tgtMax) = choice.Value;

            IMatrixData converted;
            try
            {
                using var session = host.BeginProgress("Converting…", cancellable: false);
                converted = await Task.Run(() =>
                    data.ConvertToType(targetType, doScale, srcMin, srcMax, tgtMin, tgtMax));
            }
            catch (Exception ex)
            {
                await host.ShowMessageAsync("Convert Failed", ex.Message);
                return;
            }

            converted.CopyPropertiesFrom(data);
            string typeDesc = $"{data.ValueTypeName} → {converted.ValueTypeName}";
            string detail = doScale
                ? $"{typeDesc}; scale [{srcMin:G6}, {srcMax:G6}] → [{tgtMin:G6}, {tgtMax:G6}]"
                : $"{typeDesc}; direct cast";
            MatrixPlotter.AppendHistory(converted, "Convert Type", host.Title, detail);

            if (replaceData)
                host.ReplaceData(converted, null);
            else
                host.ShowResult(converted, $"Convert of {host.Title}", null, copyDisplayState: false);
        }
    }
}
