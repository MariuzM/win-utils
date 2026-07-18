namespace WinSnip.Capture;

internal static class SaveTarget
{
    // DesktopDirectory rather than a hand-built %USERPROFILE%\Desktop: the Desktop is routinely
    // redirected to OneDrive or a network share, and only the shell folder lookup follows that.
    //
    // The lookup can still come back empty - it does for accounts with no interactive profile, such
    // as SYSTEM. Falling through to the profile path and then failing loudly matters because an
    // empty folder would make Path.Combine produce a bare file name, silently writing the
    // screenshot into the working directory instead.
    public static string DesktopFolder()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
            return desktop;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            string candidate = Path.Combine(profile, "Desktop");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            "Could not locate the current user's Desktop folder.");
    }

    // "Screenshot 2026-07-18 at 20.31.15.png". Dots rather than colons in the time because Windows
    // forbids a colon in a file name.
    public static string NextPath(DateTime now)
    {
        string folder = DesktopFolder();
        string stamp = now.ToString("yyyy-MM-dd 'at' HH.mm.ss");

        string path = Path.Combine(folder, $"Screenshot {stamp}.png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"Screenshot {stamp} ({i}).png");

        return path;
    }
}
