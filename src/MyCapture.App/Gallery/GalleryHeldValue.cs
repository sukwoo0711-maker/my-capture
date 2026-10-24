using System.Windows;
using System.Windows.Data;

namespace MyCapture.App.Gallery;

/// <summary>
/// A temporary local value on a property that is later handed back exactly as it was: the
/// property's own local value, its local binding, or, with neither, its template or style value.
/// </summary>
internal sealed class GalleryHeldValue
{
    private readonly DependencyObject _owner;
    private readonly DependencyProperty _property;
    private readonly object _local;
    private readonly BindingBase? _binding;
    private bool _restored;

    private GalleryHeldValue(DependencyObject owner, DependencyProperty property, object local, BindingBase? binding)
    {
        _owner = owner;
        _property = property;
        _local = local;
        _binding = binding;
    }

    /// <summary>Sets <paramref name="value"/> locally, remembering what it covers.</summary>
    internal static GalleryHeldValue Replace(DependencyObject owner, DependencyProperty property, object? value) =>
        TryReplace(owner, property, value)
        ?? throw new InvalidOperationException($"{property.Name} holds an expression that cannot be restored.");

    /// <summary>
    /// Like <see cref="Replace"/>, or <see langword="null"/> without touching the property when its
    /// local value is an expression other than a binding (a dynamic resource) that could not return.
    /// </summary>
    internal static GalleryHeldValue? TryReplace(DependencyObject owner, DependencyProperty property, object? value)
    {
        object local = owner.ReadLocalValue(property);
        BindingBase? binding = local is BindingExpressionBase ? BindingOperations.GetBindingBase(owner, property) : null;
        if (local is Expression && binding is null) return null;
        var held = new GalleryHeldValue(owner, property, local, binding);
        owner.SetValue(property, value);
        return held;
    }

    internal void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (_binding is not null) BindingOperations.SetBinding(_owner, _property, _binding);
        else if (_local == DependencyProperty.UnsetValue) _owner.ClearValue(_property);
        else _owner.SetValue(_property, _local);
    }
}
