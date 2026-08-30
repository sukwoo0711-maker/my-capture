using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyCapture.Core.Primitives;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> implementation.
/// </summary>
/// <remarks>
/// Hand-written rather than taken from a MVVM package. The domain layer has exactly
/// one requirement here — notify on change — and adding a dependency to satisfy it
/// would pull a framework into the layer that is deliberately framework-free.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Assigns <paramref name="value"/> to <paramref name="field"/> and raises a
    /// change notification when the value actually differs.
    /// </summary>
    /// <returns><see langword="true"/> when the value changed.</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }
}
