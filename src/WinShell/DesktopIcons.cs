using Microsoft.Win32;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

internal sealed class DesktopItem
{
    public string Path = string.Empty;
    public string Name = string.Empty;
    public bool IsDirectory;

    public IntPtr Icon;
    public bool IconLoaded;

    // Grid cell, not pixels. Keeping the authoritative position as a cell is what lets the
    // layout survive a resolution change without icons ending up off-screen.
    public int Column;
    public int Row;
    public bool Placed;

    public bool Selected;
    public RECT Bounds;
    public RECT LabelBounds;
}

/// <summary>
/// The model behind the desktop: what is on it, what it looks like, and where it sits.
///
/// Two folders make up "the desktop" - the user's own and the machine-wide one - and Explorer
/// presents them as a single merged view, so this does too.
///
/// Icons here are *owned*, unlike the taskbar's borrowed window icons: IImageList::GetIcon
/// hands back a fresh HICON each time and leaking one per file per refresh would be a steady
/// drip. They are destroyed on refresh and on shutdown. This is the same ownership rule as
/// AppList, and the opposite of WindowList - see the note in TODO.md §6.
/// </summary>
internal static unsafe class DesktopIcons
{
    private static readonly Guid IidImageList = new("46eb5926-582e-4017-9fdf-e8998daa0950");

    private static readonly List<DesktopItem> Items = new();

    // The 48px system image list. Fetched once and kept: it is a shared system object, so it
    // is never released.
    private static IntPtr _largeIcons;
    private static bool _largeIconsResolved;

    private static bool _hideExtensions = true;
    private static bool _showHidden;

    public static IReadOnlyList<DesktopItem> All => Items;

    public static string UserDesktop =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string PublicDesktop =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    private static string LayoutFile =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinShell", "desktop-layout.tsv");

    // ---- Enumeration --------------------------------------------------------------------

    public static void Refresh()
    {
        ReleaseIcons();

        Dictionary<string, (int Column, int Row)> saved = LoadLayout();
        ReadExplorerPreferences();

        Items.Clear();

        Collect(UserDesktop);
        Collect(PublicDesktop);

        // Alphabetical, folders first - Explorer's default sort, and the order new items are
        // assigned cells in when they have no saved position.
        Items.Sort(static (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (DesktopItem item in Items)
        {
            if (!saved.TryGetValue(item.Path, out (int Column, int Row) cell))
                continue;

            item.Column = cell.Column;
            item.Row = cell.Row;
            item.Placed = true;
        }
    }

    private static void Collect(string directory)
    {
        if (directory.Length == 0 || !Directory.Exists(directory))
            return;

        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                DesktopItem? item = Build(path);

                if (item != null)
                    Items.Add(item);
            }
        }
        catch
        {
            // An unreadable desktop folder should cost us that folder, not the whole desktop.
        }
    }

    private static DesktopItem? Build(string path)
    {
        try
        {
            var info = new FileInfo(path);
            FileAttributes attributes = info.Attributes;

            // desktop.ini carries the folder's own view settings and is never shown.
            if (string.Equals(info.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                return null;

            if ((attributes & FileAttributes.System) != 0)
                return null;

            if (!_showHidden && (attributes & FileAttributes.Hidden) != 0)
                return null;

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;

            return new DesktopItem
            {
                Path = path,
                Name = DisplayName(info.Name, isDirectory),
                IsDirectory = isDirectory,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Shortcut extensions are always hidden - Explorer never shows ".lnk", regardless of the
    /// "hide extensions for known file types" setting, which governs everything else.
    /// </summary>
    private static string DisplayName(string fileName, bool isDirectory)
    {
        if (isDirectory)
            return fileName;

        string extension = Path.GetExtension(fileName);

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(fileName);
        }

        return _hideExtensions ? Path.GetFileNameWithoutExtension(fileName) : fileName;
    }

    private static void ReadExplorerPreferences()
    {
        try
        {
            using RegistryKey? advanced = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");

            if (advanced == null)
                return;

            _hideExtensions = advanced.GetValue("HideFileExt") is not int hide || hide != 0;
            _showHidden = advanced.GetValue("Hidden") is int hidden && hidden == 1;
        }
        catch
        {
            // Defaults already match Explorer's out-of-the-box behaviour.
        }
    }

    // ---- Icons ----------------------------------------------------------------------------

    /// <summary>
    /// Resolves a 48px icon, lazily. SHGFI_ICON tops out at 32px, so the index it yields is
    /// used to pull the real thing out of the extra-large system image list instead.
    /// </summary>
    public static IntPtr Icon(DesktopItem item)
    {
        if (item.IconLoaded)
            return item.Icon;

        item.IconLoaded = true;

        var info = default(SHFILEINFOW);

        IntPtr result = Win32.SHGetFileInfoPath(
            item.Path, 0, ref info, (uint)sizeof(SHFILEINFOW), SHGFI_SYSICONINDEX);

        if (result == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr list = LargeIconList();

        if (list == IntPtr.Zero)
        {
            // No image list: fall back to the 32px icon rather than showing nothing. It is
            // drawn scaled, which is worse than native but still recognisable.
            var small = default(SHFILEINFOW);

            if (Win32.SHGetFileInfoPath(item.Path, 0, ref small, (uint)sizeof(SHFILEINFOW), SHGFI_ICON) != IntPtr.Zero)
                item.Icon = small.hIcon;

            return item.Icon;
        }

        IntPtr icon;
        if (GetIcon(list, info.iIcon, ILD_TRANSPARENT, &icon) == 0)
            item.Icon = icon;

        return item.Icon;
    }

    private static IntPtr LargeIconList()
    {
        if (_largeIconsResolved)
            return _largeIcons;

        _largeIconsResolved = true;

        Guid iid = IidImageList;

        IntPtr list;
        if (Win32.SHGetImageList(SHIL_EXTRALARGE, &iid, &list) == 0)
            _largeIcons = list;

        return _largeIcons;
    }

    private static void ReleaseIcons()
    {
        foreach (DesktopItem item in Items)
        {
            if (item.Icon != IntPtr.Zero)
                Win32.DestroyIcon(item.Icon);

            item.Icon = IntPtr.Zero;
            item.IconLoaded = false;
        }
    }

    public static void Release()
    {
        ReleaseIcons();
        Items.Clear();
    }

    // ---- Layout ------------------------------------------------------------------------------

    /// <summary>
    /// Assigns a cell to everything that does not already have one, then converts cells to
    /// pixel rectangles. Column-major, matching Explorer: icons fill downwards first.
    /// </summary>
    public static void Layout(int width, int height, int cellWidth, int cellHeight, int marginX, int marginY)
    {
        int rows = Math.Max(1, (height - marginY) / cellHeight);
        int columns = Math.Max(1, (width - marginX) / cellWidth);

        var taken = new HashSet<long>();

        // Saved positions are honoured first so that a newly created file cannot displace an
        // icon the user deliberately put somewhere.
        foreach (DesktopItem item in Items)
        {
            if (!item.Placed)
                continue;

            // A saved cell that no longer fits (smaller screen since last run) is released
            // back to the pool rather than leaving the icon stranded off-screen.
            if (item.Column >= columns || item.Row >= rows)
            {
                item.Placed = false;
                continue;
            }

            taken.Add(Key(item.Column, item.Row));
        }

        int next = 0;

        foreach (DesktopItem item in Items)
        {
            if (!item.Placed)
            {
                while (next < columns * rows && taken.Contains(Key(next / rows, next % rows)))
                    next++;

                // More icons than cells: stack the overflow in the last column rather than
                // dropping it. Rare, and better than an invisible file.
                item.Column = next < columns * rows ? next / rows : columns - 1;
                item.Row = next < columns * rows ? next % rows : rows - 1;

                taken.Add(Key(item.Column, item.Row));
                item.Placed = true;
                next++;
            }

            int x = marginX + item.Column * cellWidth;
            int y = marginY + item.Row * cellHeight;

            item.Bounds = new RECT(x, y, x + cellWidth, y + cellHeight);
        }
    }

    private static long Key(int column, int row) => ((long)column << 32) | (uint)row;

    // IImageList::GetIcon is the eighth method after IUnknown, so slot 10:
    // Add, ReplaceIcon, SetOverlayImage, Replace, AddMasked, Draw, Remove, GetIcon.
    private static int GetIcon(IntPtr list, int index, uint flags, IntPtr* icon)
    {
        var fn = (delegate* unmanaged<IntPtr, int, uint, IntPtr*, int>)(*(void***)list)[10];
        return fn(list, index, flags, icon);
    }

    /// <summary>Finds the cell a point falls in, for drop and drag-to-reposition.</summary>
    public static (int Column, int Row) CellAt(int x, int y, int cellWidth, int cellHeight, int marginX, int marginY)
    {
        int column = Math.Max(0, (x - marginX) / cellWidth);
        int row = Math.Max(0, (y - marginY) / cellHeight);

        return (column, row);
    }

    public static DesktopItem? HitTest(int x, int y)
    {
        // Reverse order so the topmost icon wins if two ever overlap.
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Bounds.Contains(x, y))
                return Items[i];
        }

        return null;
    }

    // ---- Position persistence -----------------------------------------------------------------

    /// <summary>
    /// A tab-separated file rather than JSON: the AOT trimming analyzers make reflection-based
    /// serialisation a fight for no benefit, and this is three fields.
    /// </summary>
    private static Dictionary<string, (int Column, int Row)> LoadLayout()
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(LayoutFile))
                return result;

            foreach (string line in File.ReadAllLines(LayoutFile))
            {
                string[] parts = line.Split('\t');

                if (parts.Length != 3)
                    continue;

                if (int.TryParse(parts[1], out int column) && int.TryParse(parts[2], out int row))
                    result[parts[0]] = (column, row);
            }
        }
        catch
        {
            // A corrupt layout file costs icon positions, not the desktop.
        }

        return result;
    }

    public static void SaveLayout()
    {
        try
        {
            string? directory = Path.GetDirectoryName(LayoutFile);

            if (directory == null)
                return;

            Directory.CreateDirectory(directory);

            var lines = new List<string>(Items.Count);

            foreach (DesktopItem item in Items)
            {
                if (item.Placed)
                    lines.Add($"{item.Path}\t{item.Column}\t{item.Row}");
            }

            File.WriteAllLines(LayoutFile, lines);
        }
        catch
        {
            // Losing positions is survivable; failing to shut down is not.
        }
    }
}
