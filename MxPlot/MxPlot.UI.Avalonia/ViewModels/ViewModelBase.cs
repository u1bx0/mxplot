using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MxPlot.UI.Avalonia.ViewModels
{
    /// <summary>
    /// Base class for the library's view models: plain <see cref="INotifyPropertyChanged"/>,
    /// implemented here so that the library carries no MVVM-toolkit dependency into its hosts.
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>
        /// Assigns <paramref name="value"/> and raises <see cref="PropertyChanged"/>, unless the
        /// field already holds an equal value. Returns whether the value changed.
        /// </summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
