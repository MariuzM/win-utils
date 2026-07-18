using System.Runtime.InteropServices;
using WinShell.Native;
using static WinShell.Native.Win32Const;

namespace WinShell;

/// <summary>
/// Deleting and dropping, both through SHFileOperation.
///
/// Doing these by hand with File.Delete and File.Copy would be a mistake: the shell version is
/// what provides the Recycle Bin, the progress dialog on a slow copy, the "are you sure",
/// the name collision prompt, and undo. A desktop where Delete destroys the file outright
/// would be actively worse than no desktop.
/// </summary>
internal static unsafe class DesktopFileOps
{
    /// <summary>Sends paths to the Recycle Bin. Returns true if anything actually happened.</summary>
    public static bool Recycle(IntPtr owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;

        IntPtr from = BuildList(paths);

        try
        {
            var operation = new SHFILEOPSTRUCTW
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = from,

                // ALLOWUNDO is the flag that means "Recycle Bin" rather than "gone".
                // WANTNUKEWARNING still warns on the files it cannot recycle.
                fFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
            };

            int result = Win32.SHFileOperation(&operation);

            return result == 0 && operation.fAnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>
    /// Handles files dropped onto the desktop. Same volume moves, different volume copies -
    /// which is what Explorer does, and what the user will expect having done it there.
    /// </summary>
    public static bool DropOnto(IntPtr owner, IReadOnlyList<string> paths, string targetDirectory)
    {
        if (paths.Count == 0 || targetDirectory.Length == 0)
            return false;

        // A drop from the desktop onto itself is a reposition, which the drag code has
        // already handled - copying the file to where it already is would be wrong.
        var incoming = new List<string>(paths.Count);

        foreach (string path in paths)
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.Equals(directory, targetDirectory, StringComparison.OrdinalIgnoreCase))
                incoming.Add(path);
        }

        if (incoming.Count == 0)
            return false;

        IntPtr from = BuildList(incoming);
        IntPtr to = BuildList(new[] { targetDirectory });

        try
        {
            var operation = new SHFILEOPSTRUCTW
            {
                hwnd = owner,
                wFunc = SameVolume(incoming[0], targetDirectory) ? FO_MOVE : FO_COPY,
                pFrom = from,
                pTo = to,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR,
            };

            int result = Win32.SHFileOperation(&operation);

            return result == 0 && operation.fAnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
            Marshal.FreeHGlobal(to);
        }
    }

    private static bool SameVolume(string left, string right)
    {
        try
        {
            string a = Path.GetPathRoot(Path.GetFullPath(left)) ?? string.Empty;
            string b = Path.GetPathRoot(Path.GetFullPath(right)) ?? string.Empty;

            return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unknown means copy: duplicating a file is recoverable, losing one is not.
            return false;
        }
    }

    /// <summary>
    /// SHFileOperation takes a double-null-terminated list, not a string. Joining on NUL and
    /// leaving a trailing one gets the second terminator from StringToHGlobalUni itself.
    /// </summary>
    private static IntPtr BuildList(IReadOnlyList<string> paths)
    {
        string joined = string.Join('\0', paths) + '\0';
        return Marshal.StringToHGlobalUni(joined);
    }
}
