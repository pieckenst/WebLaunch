using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace handlerlaunch;

/// <summary>Start menu identity registration; called only by explicit install/repair.</summary>
internal static class NotificationRegistration
{
    internal const string AppId = "WebLaunch.Desktop";
    internal static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WebLaunch.lnk");
    public static void Install(string executable)
    {
        var shell = (IShellLink)new ShellLink();
        try
        {
            shell.SetPath(executable);
            shell.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            shell.SetDescription("WebLaunch desktop game launcher");
            shell.SetIconLocation(executable, 0);
            var store = (IPropertyStore)shell;
            SetString(store, 5, AppId);
            // Protocol activation needs no COM server; the shortcut identity keeps
            // the notification in Windows Notification Center when the app is closed.
            var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 26 };
            var memory = Marshal.AllocCoTaskMem(16);
            try
            {
                Marshal.StructureToPtr(new Guid("7D15BA58-40BD-419E-ACF0-779A70F68CC1"), memory, false);
                var value = new PropertyValue { Type = 72, Pointer = memory }; // VT_CLSID
                store.SetValue(ref key, ref value);
            }
            finally { Marshal.FreeCoTaskMem(memory); }
            store.Commit();
            Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
            ((IPersistFile)shell).Save(Shortcut, true);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
    private static void SetString(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var memory = Marshal.StringToCoTaskMemUni(text);
        try { var value = new PropertyValue { Type = 31, Pointer = memory }; store.SetValue(ref key, ref value); }
        finally { Marshal.FreeCoTaskMem(memory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public nint Pointer;
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyValue value); void SetValue(ref PropertyKey key, ref PropertyValue value); void Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, nint findData, uint flags);
        void GetIDList(out nint id); void SetIDList(nint id);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key); void SetHotkey(short key); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
