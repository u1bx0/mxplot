using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// A one-shot command that runs in a plotter window: ask the user, process the data, and put the
    /// result somewhere. It keeps no state in the window once it finishes.
    /// </summary>
    /// <remarks>
    /// A command that runs one <c>IMatrixDataOperation</c> derives from <see cref="ProcessingCommand"/>,
    /// which supplies the steps such commands share. Any other one-shot command implements this directly.
    /// </remarks>
    internal interface IProcessingCommand
    {
        Task RunAsync(ICommandHost host);
    }
}
