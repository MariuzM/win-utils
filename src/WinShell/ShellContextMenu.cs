using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// The real Windows context menu, not an imitation of one.
///
/// Building our own menu would have been far less code, but it would also have been a lie:
/// "Open with", "Send to", "Extract all", the Git or antivirus entries a user has installed -
/// all of those come from shell extensions, and the only way to get them is to ask the shell
/// for the menu and let it own the contents. So the menu is obtained from IContextMenu and
/// invoked through it; WinShell only appends its own items to the background menu.
///
/// IContextMenu2 is queried and kept for the lifetime of the popup because submenus are
/// populated lazily: without forwarding WM_INITMENUPOPUP back to the shell, "Open with" and
/// "New" appear as empty arrows that never fill in.
/// </summary>
internal static unsafe class ShellContextMenu
{
    private static readonly Guid IidShellFolder = new("000214e6-0000-0000-c000-000000000046");
    private static readonly Guid IidContextMenu = new("000214e4-0000-0000-c000-000000000046");
    private static readonly Guid IidContextMenu2 = new("000214f4-0000-0000-c000-000000000046");

    // The shell's commands are numbered from IdFirst; ours start well above the range it is
    // allowed to use, so a shell extension can never collide with them.
    private const uint IdFirst = 1;
    private const uint IdLast = 0x6FFF;

    private const int IdRefresh = 0x7001;
    private const int IdDisplaySettings = 0x7002;
    private const int IdPersonalise = 0x7003;
    private const int IdWinShellSettings = 0x7004;

    // Non-zero only while a popup is up, which is also the only time menu messages need
    // forwarding. The message loop runs inside TrackPopupMenuEx, so this is never re-entered.
    private static IntPtr _active;

    // ---- Entry points --------------------------------------------------------------------

    /// <summary>Menu for one or more selected icons.</summary>
    public static void ShowForItems(IntPtr owner, int x, int y, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        string? directory = Path.GetDirectoryName(paths[0]);

        if (directory == null)
            return;

        // GetUIObjectOf takes child PIDLs relative to one parent folder, so a selection that
        // spans both desktop folders is narrowed to the folder the click started in. Rare,
        // and better than refusing to show a menu at all.
        var group = new List<string>(paths.Count);

        foreach (string path in paths)
        {
            if (string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
                group.Add(path);
        }

        if (group.Count == 0)
            return;

        IntPtr folder = BindFolder(directory);

        if (folder == IntPtr.Zero)
            return;

        var pidls = new IntPtr[group.Count];
        var children = new IntPtr[group.Count];
        int count = 0;

        try
        {
            for (int i = 0; i < group.Count; i++)
            {
                IntPtr pidl;
                uint attributes;

                if (Win32.SHParseDisplayName(group[i], IntPtr.Zero, &pidl, 0, &attributes) != 0 || pidl == IntPtr.Zero)
                    continue;

                pidls[count] = pidl;
                children[count] = Win32.ILFindLastID(pidl);
                count++;
            }

            if (count == 0)
                return;

            IntPtr contextMenu;

            fixed (IntPtr* array = children)
            {
                Guid iid = IidContextMenu;

                if (GetUIObjectOf(folder, owner, (uint)count, array, &iid, null, &contextMenu) != 0 ||
                    contextMenu == IntPtr.Zero)
                {
                    return;
                }
            }

            try
            {
                Track(owner, contextMenu, x, y, background: false);
            }
            finally
            {
                Release(contextMenu);
            }
        }
        finally
        {
            for (int i = 0; i < count; i++)
                Win32.ILFree(pidls[i]);

            Release(folder);
        }
    }

    /// <summary>Menu for empty desktop: the folder's own background menu, plus our items.</summary>
    public static void ShowForBackground(IntPtr owner, int x, int y, string directory)
    {
        IntPtr folder = BindFolder(directory);

        if (folder == IntPtr.Zero)
        {
            // Even with no shell menu the user should still get somewhere useful, so our own
            // items are shown on their own rather than nothing happening on right-click.
            ShowFallback(owner, x, y);
            return;
        }

        try
        {
            Guid iid = IidContextMenu;

            IntPtr contextMenu;
            if (CreateViewObject(folder, owner, &iid, &contextMenu) != 0 || contextMenu == IntPtr.Zero)
            {
                ShowFallback(owner, x, y);
                return;
            }

            try
            {
                Track(owner, contextMenu, x, y, background: true);
            }
            finally
            {
                Release(contextMenu);
            }
        }
        finally
        {
            Release(folder);
        }
    }

    /// <summary>
    /// Forwards the messages a shell context menu needs while it is up. Returns false when no
    /// menu is active, so the caller can fall through to DefWindowProc.
    /// </summary>
    public static bool HandleMenuMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;

        if (_active == IntPtr.Zero)
            return false;

        if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
            return false;

        HandleMenuMsg(_active, msg, wParam, lParam);
        return true;
    }

    /// <summary>
    /// Builds both menus and reports how many entries each produced, without showing either.
    /// Every vtable slot this file depends on is exercised except InvokeCommand, so a wrong
    /// slot number shows up as a zero or a failure here rather than as an empty menu at
    /// right-click time. TrackPopupMenuEx runs its own message loop and cannot be tested
    /// unattended, which is exactly why the rest is checked this way.
    /// </summary>
    public static string Probe(IntPtr owner, IReadOnlyList<string> paths, string directory)
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine($"background folder : {directory}");

        IntPtr folder = BindFolder(directory);
        report.AppendLine($"  BindFolder      : {(folder != IntPtr.Zero ? "ok" : "FAILED")}");

        if (folder != IntPtr.Zero)
        {
            try
            {
                Guid iid = IidContextMenu;

                IntPtr contextMenu;
                int hr = CreateViewObject(folder, owner, &iid, &contextMenu);

                report.AppendLine($"  CreateViewObject: 0x{hr:x8}");

                if (hr == 0 && contextMenu != IntPtr.Zero)
                {
                    report.AppendLine($"  background items: {CountItems(contextMenu, background: true)}");
                    Release(contextMenu);
                }
            }
            finally
            {
                Release(folder);
            }
        }

        if (paths.Count == 0)
            return report.ToString();

        report.AppendLine($"item menu for     : {paths[0]}");

        IntPtr parent = BindFolder(Path.GetDirectoryName(paths[0]) ?? string.Empty);
        report.AppendLine($"  BindFolder      : {(parent != IntPtr.Zero ? "ok" : "FAILED")}");

        if (parent == IntPtr.Zero)
            return report.ToString();

        try
        {
            IntPtr pidl;
            uint attributes;

            if (Win32.SHParseDisplayName(paths[0], IntPtr.Zero, &pidl, 0, &attributes) != 0 || pidl == IntPtr.Zero)
            {
                report.AppendLine("  SHParseDisplayName: FAILED");
                return report.ToString();
            }

            try
            {
                IntPtr child = Win32.ILFindLastID(pidl);
                Guid iid = IidContextMenu;

                IntPtr contextMenu;
                int hr = GetUIObjectOf(parent, owner, 1, &child, &iid, null, &contextMenu);

                report.AppendLine($"  GetUIObjectOf   : 0x{hr:x8}");

                if (hr == 0 && contextMenu != IntPtr.Zero)
                {
                    report.AppendLine($"  item items      : {CountItems(contextMenu, background: false)}");
                    Release(contextMenu);
                }
            }
            finally
            {
                Win32.ILFree(pidl);
            }
        }
        finally
        {
            Release(parent);
        }

        return report.ToString();
    }

    private static int CountItems(IntPtr contextMenu, bool background)
    {
        IntPtr menu = Win32.CreatePopupMenu();

        if (menu == IntPtr.Zero)
            return -1;

        try
        {
            uint flags = CMF_NORMAL | CMF_EXPLORE;

            if (!background)
                flags |= CMF_CANRENAME;

            if (QueryContextMenu(contextMenu, menu, 0, IdFirst, IdLast, flags) < 0)
                return -1;

            return Win32.GetMenuItemCount(menu);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    // ---- Tracking -------------------------------------------------------------------------

    private static void Track(IntPtr owner, IntPtr contextMenu, int x, int y, bool background)
    {
        IntPtr menu = Win32.CreatePopupMenu();

        if (menu == IntPtr.Zero)
            return;

        IntPtr menu2 = IntPtr.Zero;

        try
        {
            uint flags = CMF_NORMAL | CMF_EXPLORE;

            if (!background)
                flags |= CMF_CANRENAME;

            if (QueryContextMenu(contextMenu, menu, 0, IdFirst, IdLast, flags) < 0)
                return;

            if (background)
                AppendOwnItems(menu);

            // Kept for the duration of the popup so submenus can populate. Queried after
            // QueryContextMenu because that is when the handler has decided what it needs.
            menu2 = QueryInterface(contextMenu, IidContextMenu2);
            _active = menu2;

            int command = Win32.TrackPopupMenuEx(
                menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD, x, y, owner, IntPtr.Zero);

            _active = IntPtr.Zero;

            if (command <= 0)
                return;

            if (command >= IdRefresh)
                InvokeOwnCommand(command);
            else
                Invoke(owner, contextMenu, command);
        }
        finally
        {
            _active = IntPtr.Zero;

            if (menu2 != IntPtr.Zero)
                Release(menu2);

            Win32.DestroyMenu(menu);
        }
    }

    private static void Invoke(IntPtr owner, IntPtr contextMenu, int command)
    {
        // The verb is the command's offset from idCmdFirst, passed in the pointer field as a
        // small integer - the MAKEINTRESOURCE convention IContextMenu expects.
        var invoke = new CMINVOKECOMMANDINFO
        {
            cbSize = (uint)sizeof(CMINVOKECOMMANDINFO),
            hwnd = owner,
            lpVerb = new IntPtr(command - (int)IdFirst),
            nShow = SW_SHOW,
        };

        InvokeCommand(contextMenu, &invoke);
    }

    private static void AppendOwnItems(IntPtr menu)
    {
        Win32.AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
        Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdRefresh), "Refresh");
        Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdDisplaySettings), "Display settings");
        Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdPersonalise), "Personalise");
        Win32.AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
        Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdWinShellSettings), "WinShell Settings");
    }

    private static void ShowFallback(IntPtr owner, int x, int y)
    {
        IntPtr menu = Win32.CreatePopupMenu();

        if (menu == IntPtr.Zero)
            return;

        try
        {
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdRefresh), "Refresh");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdDisplaySettings), "Display settings");
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdPersonalise), "Personalise");
            Win32.AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
            Win32.AppendMenu(menu, MF_STRING, new IntPtr(IdWinShellSettings), "WinShell Settings");

            int command = Win32.TrackPopupMenuEx(
                menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD, x, y, owner, IntPtr.Zero);

            if (command > 0)
                InvokeOwnCommand(command);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private static void InvokeOwnCommand(int command)
    {
        switch (command)
        {
            case IdRefresh:
                DesktopWindow.Refresh();
                break;

            case IdDisplaySettings:
                ShellLaunch.Open("ms-settings:display");
                break;

            case IdPersonalise:
                ShellLaunch.Open("ms-settings:personalization-background");
                break;

            case IdWinShellSettings:
                SettingsWindow.Show();
                break;
        }
    }

    // ---- Binding ----------------------------------------------------------------------------

    private static IntPtr BindFolder(string directory)
    {
        IntPtr pidl;
        uint attributes;

        if (Win32.SHParseDisplayName(directory, IntPtr.Zero, &pidl, 0, &attributes) != 0 || pidl == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            Guid iid = IidShellFolder;

            // A null IShellFolder means "resolve against the desktop root", which is what
            // makes an absolute PIDL usable here without binding the root by hand first.
            IntPtr folder;
            if (Win32.SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, &iid, &folder) != 0)
                return IntPtr.Zero;

            return folder;
        }
        finally
        {
            Win32.ILFree(pidl);
        }
    }

    // ---- COM, by vtable slot -------------------------------------------------------------------

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        var fn = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)(*(void***)obj)[0];

        IntPtr result;
        return fn(obj, &iid, &result) == 0 ? result : IntPtr.Zero;
    }

    private static uint Release(IntPtr obj)
    {
        var fn = (delegate* unmanaged<IntPtr, uint>)(*(void***)obj)[2];
        return fn(obj);
    }

    // IShellFolder: CreateViewObject is slot 8, GetUIObjectOf slot 10.
    private static int CreateViewObject(IntPtr folder, IntPtr owner, Guid* riid, IntPtr* ppv)
    {
        var fn = (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)folder)[8];
        return fn(folder, owner, riid, ppv);
    }

    private static int GetUIObjectOf(
        IntPtr folder, IntPtr owner, uint count, IntPtr* pidls, Guid* riid, uint* reserved, IntPtr* ppv)
    {
        var fn = (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr*, Guid*, uint*, IntPtr*, int>)(*(void***)folder)[10];
        return fn(folder, owner, count, pidls, riid, reserved, ppv);
    }

    // IContextMenu: QueryContextMenu slot 3, InvokeCommand slot 4.
    private static int QueryContextMenu(
        IntPtr menu, IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags)
    {
        var fn = (delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, int>)(*(void***)menu)[3];
        return fn(menu, hmenu, index, idFirst, idLast, flags);
    }

    private static int InvokeCommand(IntPtr menu, CMINVOKECOMMANDINFO* info)
    {
        var fn = (delegate* unmanaged<IntPtr, CMINVOKECOMMANDINFO*, int>)(*(void***)menu)[4];
        return fn(menu, info);
    }

    // IContextMenu2 adds HandleMenuMsg at slot 6.
    private static int HandleMenuMsg(IntPtr menu, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var fn = (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, int>)(*(void***)menu)[6];
        return fn(menu, msg, wParam, lParam);
    }
}
