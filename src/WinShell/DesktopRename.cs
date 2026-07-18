using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// F2 rename, done the way Explorer does it: a real EDIT control placed over the icon's label
/// rather than a modal dialog.
///
/// The control has to be subclassed because EDIT swallows Return and Escape - they never
/// reach the parent, so without intercepting them there would be no way to commit or cancel
/// from the keyboard.
/// </summary>
internal static unsafe class DesktopRename
{
    private static IntPtr _edit;
    private static IntPtr _originalProc;
    private static DesktopItem? _target;

    public static bool Active => _edit != IntPtr.Zero;

    public static void Begin(IntPtr parent, DesktopItem item, IntPtr font)
    {
        Commit();

        RECT label = item.LabelBounds;

        // A little taller than the label: an EDIT with its border needs the room, and a text
        // box clipped to exactly the text height looks broken.
        int height = Math.Max(label.Height, 22);

        _edit = Win32.CreateWindowEx(
            0, "EDIT", item.Name,
            WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL | WS_TABSTOP,
            label.Left - 2, label.Top - 2, label.Width + 4, height,
            parent, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_edit == IntPtr.Zero)
            return;

        _target = item;

        Win32.SendMessage(_edit, WM_SETFONT, font, new IntPtr(1));

        // Select everything, so typing replaces the name - the same behaviour as Explorer,
        // where F2 followed by typing does not append to the old name.
        Win32.SendMessage(_edit, EM_SETSEL, IntPtr.Zero, new IntPtr(-1));

        _originalProc = Win32.SetWindowLongPtr(_edit, GWLP_WNDPROC, (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&EditProc);

        Win32.SetFocus(_edit);
    }

    /// <summary>Applies the typed name and tears the editor down. Safe to call when inactive.</summary>
    public static void Commit()
    {
        if (_edit == IntPtr.Zero)
            return;

        DesktopItem? item = _target;
        string typed = Win32.GetWindowTitle(_edit);

        Destroy();

        if (item == null || typed.Length == 0 || typed == item.Name)
            return;

        Apply(item, typed);
    }

    public static void Cancel() => Destroy();

    private static void Destroy()
    {
        if (_edit == IntPtr.Zero)
            return;

        // Restore the original procedure before destroying, so the teardown messages are not
        // routed through a subclass that is about to stop existing.
        if (_originalProc != IntPtr.Zero)
            Win32.SetWindowLongPtr(_edit, GWLP_WNDPROC, _originalProc);

        Win32.DestroyWindow(_edit);

        _edit = IntPtr.Zero;
        _originalProc = IntPtr.Zero;
        _target = null;
    }

    /// <summary>
    /// Renames on disk. The typed text is a display name, so a hidden extension has to be put
    /// back - otherwise renaming "Report" would silently strip ".docx" and break the file.
    /// </summary>
    private static void Apply(DesktopItem item, string typed)
    {
        try
        {
            string? directory = Path.GetDirectoryName(item.Path);

            if (directory == null)
                return;

            string currentName = Path.GetFileName(item.Path);
            string extension = Path.GetExtension(currentName);

            bool extensionWasHidden =
                extension.Length > 0 &&
                !item.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

            string newName = extensionWasHidden && !typed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? typed + extension
                : typed;

            string destination = Path.Combine(directory, newName);

            if (string.Equals(destination, item.Path, StringComparison.OrdinalIgnoreCase))
                return;

            if (item.IsDirectory)
                Directory.Move(item.Path, destination);
            else
                File.Move(item.Path, destination);

            // The folder watcher will notice and refresh; nothing to do here.
        }
        catch
        {
            // A rejected rename (name in use, file locked, permissions) leaves the original
            // name in place, which is the right outcome and already visible on screen.
        }
    }

    [UnmanagedCallersOnly]
    private static IntPtr EditProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_KEYDOWN when (int)wParam == VK_RETURN:
                    Commit();
                    return IntPtr.Zero;

                case WM_KEYDOWN when (int)wParam == VK_ESCAPE:
                    Cancel();
                    return IntPtr.Zero;

                case WM_CHAR when (int)wParam is VK_RETURN or VK_ESCAPE:
                    // Swallowed so the EDIT does not beep at a character it cannot insert.
                    return IntPtr.Zero;

                case WM_KILLFOCUS:
                    Commit();
                    return IntPtr.Zero;
            }

            return Win32.CallWindowProc(_originalProc, hwnd, msg, wParam, lParam);
        }
        catch
        {
            return Win32.CallWindowProc(_originalProc, hwnd, msg, wParam, lParam);
        }
    }
}
