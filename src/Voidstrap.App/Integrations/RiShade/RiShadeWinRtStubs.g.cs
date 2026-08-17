namespace Windows.UI.Composition
{
    public sealed class Visual
    {
        private Visual()
        {
        }
    }
}

namespace ABI.Windows.UI.Composition
{
    public static class Visual
    {
        public static global::WinRT.ObjectReferenceValue CreateMarshaler2(global::Windows.UI.Composition.Visual _) => default;
    }
}

namespace Windows.System
{
    public sealed class DispatcherQueue
    {
        private DispatcherQueue()
        {
        }
    }
}

namespace ABI.Windows.System
{
    public static class DispatcherQueue
    {
        public static global::Windows.System.DispatcherQueue FromAbi(nint thisPtr) => null;

        public static void DisposeAbi(nint abi) => global::WinRT.MarshalInspectable<object>.DisposeAbi(abi);
    }
}
