namespace WinShell;

internal static class FileSearch
{
    private const int MaxResults = 40;
    private const int BudgetMs = 350;
    private const int MaxDepth = 6;

    private static readonly string[] SkipNames =
    [
        "node_modules", ".git", "AppData", "$RECYCLE.BIN", "System Volume Information",
    ];

    public static List<(string Path, string Name)> Run(string query)
    {
        var hits = new List<(string, string)>();

        if (query.Length < 3)
            return hits;

        long deadline = Environment.TickCount64 + BudgetMs;
        var queue = new Queue<(string Dir, int Depth)>();

        foreach (string root in Roots())
        {
            if (root.Length > 0 && Directory.Exists(root))
                queue.Enqueue((root, 0));
        }

        while (queue.Count > 0 && hits.Count < MaxResults && Environment.TickCount64 < deadline)
        {
            (string dir, int depth) = queue.Dequeue();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (hits.Count >= MaxResults || Environment.TickCount64 >= deadline)
                    break;

                string name = Path.GetFileName(entry);

                if (name.Length == 0 || name[0] == '.' || Skip(name))
                    continue;

                bool isDir;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Hidden) != 0 || (attributes & FileAttributes.System) != 0)
                        continue;

                    isDir = (attributes & FileAttributes.Directory) != 0;

                    if (isDir && (attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue;
                }

                if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add((entry, name));

                if (isDir && depth < MaxDepth)
                    queue.Enqueue((entry, depth + 1));
            }
        }

        return hits;
    }

    private static IEnumerable<string> Roots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
            yield return Path.Combine(profile, "Downloads");
    }

    private static bool Skip(string name)
    {
        foreach (string skip in SkipNames)
        {
            if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
