using System.Runtime.InteropServices;

namespace SendToMonitor;

/// <summary>What UI Automation reports for one element.</summary>
internal readonly record struct UiaElementInfo(int ControlType, string ClassName, string AutomationId, string Name, RECT Bounds);

/// <summary>
/// Minimal UI Automation client: just enough to ask "what is under this point?".
/// Calls IUIAutomation through raw vtable slots instead of pulling in WPF's managed UIA
/// (which would roughly double the size of the single-file exe). Slot numbers were checked
/// against the UIAutomationClient type library in UIAutomationCore.dll.
/// Create and use it on one MTA thread; it is not thread-safe.
/// </summary>
internal sealed unsafe class Uia : IDisposable
{
    // Control type IDs (UIA_*ControlTypeId)
    public const int Button = 50000, Calendar = 50001, CheckBox = 50002, ComboBox = 50003, Edit = 50004,
        Hyperlink = 50005, Image = 50006, ListItem = 50007, List = 50008, Menu = 50009, MenuBar = 50010,
        MenuItem = 50011, ProgressBar = 50012, RadioButton = 50013, ScrollBar = 50014, Slider = 50015,
        Spinner = 50016, StatusBar = 50017, Tab = 50018, TabItem = 50019, Text = 50020, ToolBar = 50021,
        ToolTip = 50022, Tree = 50023, TreeItem = 50024, Custom = 50025, Group = 50026, Thumb = 50027,
        DataGrid = 50028, DataItem = 50029, Document = 50030, SplitButton = 50031, Window = 50032,
        Pane = 50033, Header = 50034, HeaderItem = 50035, Table = 50036, TitleBar = 50037,
        Separator = 50038, SemanticZoom = 50039, AppBar = 50040;

    private const int UIA_BoundingRectanglePropertyId = 30001, UIA_ControlTypePropertyId = 30003,
        UIA_NamePropertyId = 30005, UIA_AutomationIdPropertyId = 30011, UIA_ClassNamePropertyId = 30012;

    // IUIAutomation / IUIAutomation2
    private const int Slot_ElementFromPointBuildCache = 11, Slot_RawViewWalker = 16, Slot_CreateCacheRequest = 20,
        Slot_PutConnectionTimeout = 61, Slot_PutTransactionTimeout = 63;
    // IUIAutomationCacheRequest
    private const int Slot_AddProperty = 3;
    // IUIAutomationTreeWalker
    private const int Slot_GetParentElementBuildCache = 9;
    // IUIAutomationElement (cached properties)
    private const int Slot_CachedControlType = 53, Slot_CachedName = 55, Slot_CachedAutomationId = 61,
        Slot_CachedClassName = 62, Slot_CachedBoundingRectangle = 75;

    private static readonly Guid CLSID_CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");
    private static readonly Guid IID_IUIAutomation2 = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

    private IntPtr _automation;   // IUIAutomation2*
    private IntPtr _cacheRequest; // IUIAutomationCacheRequest*
    private IntPtr _rawWalker;    // IUIAutomationTreeWalker*

    private Uia() { }

    /// <summary>
    /// Creates the client on the calling thread (which must be MTA). Cross-process calls give up
    /// after <paramref name="timeoutMs"/> instead of UIA's default 20 s. Returns null if UIA is unavailable.
    /// </summary>
    public static Uia? TryCreate(uint timeoutMs)
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        if (hr < 0 && hr != RPC_E_CHANGED_MODE) return null;

        var uia = new Uia();
        try
        {
            Guid clsid = CLSID_CUIAutomation8, iid = IID_IUIAutomation2;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out uia._automation) < 0)
                return null;

            Call(uia._automation, Slot_PutConnectionTimeout, timeoutMs);
            Call(uia._automation, Slot_PutTransactionTimeout, timeoutMs);

            IntPtr p;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(uia._automation, Slot_CreateCacheRequest))(uia._automation, &p) < 0)
                return null;
            uia._cacheRequest = p;
            foreach (int property in new[] { UIA_ControlTypePropertyId, UIA_ClassNamePropertyId, UIA_AutomationIdPropertyId,
                                             UIA_NamePropertyId, UIA_BoundingRectanglePropertyId })
            {
                Call(uia._cacheRequest, Slot_AddProperty, (uint)property);
            }

            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(uia._automation, Slot_RawViewWalker))(uia._automation, &p) < 0)
                return null;
            uia._rawWalker = p;

            Uia result = uia;
            uia = null!;
            return result;
        }
        finally
        {
            uia?.Dispose();
        }
    }

    /// <summary>
    /// The element under <paramref name="pt"/> followed by up to <paramref name="ancestors"/> of its parents
    /// (raw view). Empty if UIA failed or timed out.
    /// </summary>
    public List<UiaElementInfo> ElementsAt(POINT pt, int ancestors = 0)
    {
        var result = new List<UiaElementInfo>(ancestors + 1);
        IntPtr element;
        if (((delegate* unmanaged[Stdcall]<IntPtr, POINT, IntPtr, IntPtr*, int>)Vtbl(_automation, Slot_ElementFromPointBuildCache))
                (_automation, pt, _cacheRequest, &element) < 0 || element == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            for (int level = 0; ; level++)
            {
                result.Add(ReadCached(element));
                if (level >= ancestors) break;

                IntPtr parent;
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Vtbl(_rawWalker, Slot_GetParentElementBuildCache))
                        (_rawWalker, element, _cacheRequest, &parent) < 0 || parent == IntPtr.Zero)
                {
                    break;
                }
                Release(element);
                element = parent;
            }
        }
        finally
        {
            Release(element);
        }
        return result;
    }

    private static UiaElementInfo ReadCached(IntPtr element)
    {
        int controlType = 0;
        RECT bounds = default;
        ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtbl(element, Slot_CachedControlType))(element, &controlType);
        ((delegate* unmanaged[Stdcall]<IntPtr, RECT*, int>)Vtbl(element, Slot_CachedBoundingRectangle))(element, &bounds);
        return new UiaElementInfo(controlType,
            ReadBstr(element, Slot_CachedClassName),
            ReadBstr(element, Slot_CachedAutomationId),
            ReadBstr(element, Slot_CachedName),
            bounds);
    }

    private static string ReadBstr(IntPtr element, int slot)
    {
        IntPtr bstr = IntPtr.Zero;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(element, slot))(element, &bstr) < 0 || bstr == IntPtr.Zero)
            return string.Empty;
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { Marshal.FreeBSTR(bstr); }
    }

    public void Dispose()
    {
        Release(_rawWalker);
        Release(_cacheRequest);
        Release(_automation);
        _rawWalker = _cacheRequest = _automation = IntPtr.Zero;
    }

    // ---------------- raw COM plumbing ----------------

    private static IntPtr Vtbl(IntPtr comObject, int slot) => (*(IntPtr**)comObject)[slot];

    private static void Call(IntPtr comObject, int slot, uint arg) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl(comObject, slot))(comObject, arg);

    private static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(comObject, 2))(comObject);
    }

    private const uint COINIT_MULTITHREADED = 0x0, CLSCTX_INPROC_SERVER = 0x1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr ppv);
}
