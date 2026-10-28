using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    // Placeholders keep the property order and collapsed-state contract without constructing
    // the controls, bindings, glyphs and templates of tools the user has not opened.
    private sealed class DeferredInspectorSection : FrameworkElement { }

    internal int InspectorCreatedGroupCount { get; private set; }

    private FrameworkElement DeferredSection(string name)
    {
        var section = new DeferredInspectorSection { Name = name, Visibility = Visibility.Collapsed };
        _propertyGroups.Children.Add(section);
        return section;
    }

    private void EnsureSection(ref FrameworkElement section, Func<FrameworkElement> create)
    {
        if (section is not DeferredInspectorSection) return;
        FrameworkElement previous = section;
        section = create();
        InstallSection(previous, section);
    }

    private void InstallSection(FrameworkElement previous, FrameworkElement section)
    {
        section.Visibility = previous.Visibility;
        int index = _propertyGroups.Children.IndexOf(previous);
        _propertyGroups.Children.RemoveAt(index);
        _propertyGroups.Children.Insert(index, section);
        DisableImeOutsideTextInputs(section);
        InspectorCreatedGroupCount++;
    }

    private void EnsureShapeSection()
    {
        if (_shapeStyleSection is not DeferredInspectorSection) return;
        FrameworkElement previousFill = _fillSection;
        EnsureSection(ref _shapeStyleSection, BuildShapeStyleSection);
        InstallSection(previousFill, _fillSection);
    }

    private void EnsureAllInspectorSections()
    {
        EnsureSection(ref _colorSection, BuildColorSection);
        EnsureSection(ref _thicknessSection, BuildThicknessSection);
        EnsureShapeSection();
        EnsureSection(ref _arrowHeadSection, BuildArrowHeadSection);
        EnsureSection(ref _stepSection, BuildStepNumberSection);
        EnsureSection(ref _fontFamilySection, BuildFontFamilySection);
        EnsureSection(ref _fontSizeSection, BuildFontSizeSection);
        EnsureSection(ref _textStyleSection, BuildTextStyleSection);
        EnsureSection(ref _blurSection, BuildBlurSection);
        EnsureSection(ref _mosaicSection, BuildMosaicSection);
        EnsureSection(ref _cropSection, BuildCropSection);
    }

    private static TextBox CropDimensionInput()
    {
        var box = new TextBox { MinHeight = 32, Width = 64, FontSize = 12 };
        InputMethod.SetIsInputMethodEnabled(box, false);
        return box;
    }
}
