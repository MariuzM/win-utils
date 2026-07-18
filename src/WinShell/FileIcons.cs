using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal static unsafe class FileIcons
{
    private const string DirectoryKey = "<dir>";

    private static readonly Dictionary<string, IntPtr> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IntPtr For(string path, bool isDirectory)
    {
        string key = isDirectory ? DirectoryKey : Path.GetExtension(path);

        if (key.Length == 0)
            key = "<none>";

        if (Cache.TryGetValue(key, out IntPtr cached))
            return cached;

        var info = default(SHFILEINFOW);
        uint attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

        IntPtr result = Win32.SHGetFileInfoPath(
            isDirectory ? "folder" : "file" + key,
            attributes, ref info, (uint)sizeof(SHFILEINFOW),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        IntPtr icon = result != IntPtr.Zero ? info.hIcon : IntPtr.Zero;
        Cache[key] = icon;
        return icon;
    }

    public static void Release()
    {
        foreach (IntPtr icon in Cache.Values)
        {
            if (icon != IntPtr.Zero)
                Win32.DestroyIcon(icon);
        }

        Cache.Clear();
    }
}
