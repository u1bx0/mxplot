using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// Opt-in capability: a managed window that can render its current view to a PNG file.
    /// </summary>
    public interface IExportableAsImage
    {
        /// <summary>
        /// Exports the current view to <paramref name="filePath"/> as a PNG.
        /// Returns <c>true</c> if the file was written; <c>false</c> if content was unavailable.
        /// Must be called on the UI thread.
        /// </summary>
        Task<bool> ExportAsImageAsync(string filePath);
    }
}
