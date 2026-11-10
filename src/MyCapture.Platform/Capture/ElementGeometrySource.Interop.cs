using System.Runtime.InteropServices;

namespace MyCapture.Platform.Capture;

public sealed partial class ElementGeometrySource
{
    // IUIAutomation2 extends all 55 IUIAutomation slots. Verified against the installed
    // UIAutomationCore type library (IUnknown slots 0..2, timeout setters 61/63).
    // Unused prefix slots must remain present and are never called.
    [ComImport, Guid("34723AFF-0C9D-49D0-9896-7AB52DF8CD8A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomation2
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement(); void ElementFromHandle();
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        IUiElement ElementFromHandleBuildCache(IntPtr hwnd, IUiCache cache);
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        IUiWalker GetControlViewWalker(); void GetContentViewWalker(); IUiWalker GetRawViewWalker();
        IUiCondition GetRawViewCondition(); IUiCondition GetControlViewCondition(); void GetContentViewCondition();
        IUiCache CreateCacheRequest();
        void CreateTrueCondition(); void CreateFalseCondition(); void CreatePropertyCondition(); void CreatePropertyConditionEx();
        void CreateAndCondition(); void CreateAndConditionFromArray(); void CreateAndConditionFromNativeArray();
        void CreateOrCondition(); void CreateOrConditionFromArray(); void CreateOrConditionFromNativeArray(); void CreateNotCondition();
        void AddAutomationEventHandler(); void RemoveAutomationEventHandler(); void AddPropertyChangedEventHandlerNativeArray();
        void AddPropertyChangedEventHandler(); void RemovePropertyChangedEventHandler(); void AddStructureChangedEventHandler();
        void RemoveStructureChangedEventHandler(); void AddFocusChangedEventHandler(); void RemoveFocusChangedEventHandler();
        void RemoveAllEventHandlers(); void IntNativeArrayToSafeArray(); void IntSafeArrayToNativeArray(); void RectToVariant();
        void VariantToRect(); void SafeArrayToRectNativeArray(); void CreateProxyFactoryEntry(); void GetProxyFactoryMapping();
        void GetPropertyProgrammaticName(); void GetPatternProgrammaticName(); void PollForPotentialSupportedPatterns();
        void PollForPotentialSupportedProperties(); void CheckNotSupported(); void GetReservedNotSupportedValue();
        void GetReservedMixedAttributeValue(); void ElementFromIAccessible(); void ElementFromIAccessibleBuildCache();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAutoSetFocus();
        void SetAutoSetFocus([MarshalAs(UnmanagedType.Bool)] bool value);
        uint GetConnectionTimeout(); void SetConnectionTimeout(uint milliseconds);
        uint GetTransactionTimeout(); void SetTransactionTimeout(uint milliseconds);
    }
    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache();
        void FindAllBuildCache(); void BuildUpdatedCache(); void GetCurrentPropertyValue(); void GetCurrentPropertyValueEx();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCachedPropertyValue(int propertyId);
    }
    [ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiWalker
    {
        void GetParentElement(); void GetFirstChildElement(); void GetLastChildElement(); void GetNextSiblingElement();
        void GetPreviousSiblingElement(); void NormalizeElement(); void GetParentElementBuildCache();
        IUiElement? GetFirstChildElementBuildCache(IUiElement element, IUiCache cache);
        void GetLastChildElementBuildCache(); IUiElement? GetNextSiblingElementBuildCache(IUiElement element, IUiCache cache);
    }
    [ComImport, Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiCache
    {
        void AddProperty(int id); void AddPattern(); void Clone(); void GetTreeScope(); void SetTreeScope(int scope);
        void GetTreeFilter(); void SetTreeFilter(IUiCondition condition); void GetAutomationElementMode(); void SetAutomationElementMode(int mode);
    }
    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiCondition { }
}
