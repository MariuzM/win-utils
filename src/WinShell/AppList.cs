using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal sealed class AppEntry
{
    public string Name = string.Empty;
    public string ParsingName = string.Empty;
    public IntPtr Pidl;
    public IntPtr Icon;
    public bool IconLoaded;
}

internal static unsafe class AppList
{
    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");

    private static readonly List<AppEntry> All = new();

    public static IReadOnlyList<AppEntry> Items => All;

    public static bool Loaded { get; private set; }

    public static void EnsureLoaded()
    {
        if (Loaded)
            return;

        Loaded = true;

        try
        {
            Enumerate();
        }
        catch
        {
        }

        All.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Enumerate()
    {
        IntPtr folder = IntPtr.Zero;
        IntPtr enumerator = IntPtr.Zero;

        try
        {
            Guid folderId = FolderIdAppsFolder;
            Guid iidItem = IidShellItem;

            IntPtr psi;
            if (Win32.SHGetKnownFolderItem(&folderId, 0, IntPtr.Zero, &iidItem, &psi) != 0 || psi == IntPtr.Zero)
                return;

            folder = psi;

            Guid bhid = BhidEnumItems;
            Guid iidEnum = IidEnumShellItems;

            IntPtr penum;
            if (BindToHandler(folder, &bhid, &iidEnum, &penum) != 0 || penum == IntPtr.Zero)
                return;

            enumerator = penum;

            IntPtr item;
            uint fetched;

            while (Next(enumerator, 1, &item, &fetched) == 0 && fetched == 1 && item != IntPtr.Zero)
            {
                try
                {
                    AppEntry? entry = Build(item);
                    if (entry != null)
                        All.Add(entry);
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            if (enumerator != IntPtr.Zero)
                Release(enumerator);

            if (folder != IntPtr.Zero)
                Release(folder);
        }
    }

    private static AppEntry? Build(IntPtr item)
    {
        string name = DisplayName(item, SIGDN_NORMALDISPLAY);
        if (name.Length == 0)
            return null;

        string parsing = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
        if (parsing.Length == 0)
            return null;

        IntPtr pidl;
        if (Win32.SHGetIDListFromObject(item, &pidl) != 0)
            pidl = IntPtr.Zero;

        return new AppEntry { Name = name, ParsingName = parsing, Pidl = pidl };
    }

    private static string DisplayName(IntPtr item, uint kind)
    {
        IntPtr buffer;
        if (GetDisplayName(item, kind, &buffer) != 0 || buffer == IntPtr.Zero)
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Win32.CoTaskMemFree(buffer);
        }
    }

    public static IntPtr Icon(AppEntry entry)
    {
        if (entry.IconLoaded)
            return entry.Icon;

        entry.IconLoaded = true;

        if (entry.Pidl == IntPtr.Zero)
            return IntPtr.Zero;

        var info = default(SHFILEINFOW);
        IntPtr result = Win32.SHGetFileInfoPidl(
            entry.Pidl, 0, ref info, (uint)sizeof(SHFILEINFOW),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_PIDL);

        if (result != IntPtr.Zero)
            entry.Icon = info.hIcon;

        return entry.Icon;
    }

    public static void Launch(AppEntry entry)
    {
        // ShellLaunch, not ShellExecute: for packaged apps the AppsFolder path fails outright
        // while WinShell is the shell. See the header comment there.
        ShellLaunch.Open("shell:AppsFolder\\" + entry.ParsingName);
    }

    public static void Release()
    {
        foreach (AppEntry entry in All)
        {
            if (entry.Icon != IntPtr.Zero)
                Win32.DestroyIcon(entry.Icon);

            if (entry.Pidl != IntPtr.Zero)
                Win32.CoTaskMemFree(entry.Pidl);
        }

        All.Clear();
        Loaded = false;
    }

    private static uint Release(IntPtr obj)
    {
        var fn = (delegate* unmanaged<IntPtr, uint>)(*(void***)obj)[2];
        return fn(obj);
    }

    private static int BindToHandler(IntPtr item, Guid* bhid, Guid* riid, IntPtr* ppv)
    {
        var fn = (delegate* unmanaged<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)(*(void***)item)[3];
        return fn(item, IntPtr.Zero, bhid, riid, ppv);
    }

    private static int GetDisplayName(IntPtr item, uint sigdn, IntPtr* name)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)(*(void***)item)[5];
        return fn(item, sigdn, name);
    }

    private static int Next(IntPtr enumerator, uint count, IntPtr* items, uint* fetched)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, int>)(*(void***)enumerator)[3];
        return fn(enumerator, count, items, fetched);
    }
}
