namespace Kairo.Core.Tools;

/// <summary>
/// Where a path really leads. A folder junction or symbolic link inside the working folder can point anywhere (a sandboxed command can
/// make one without any rights), and file tools run outside the sandbox with the user's rights: "is this path inside the project" has to
/// be asked of the real path, not of the text.
/// </summary>
public static class RealPath
{
    private const int MaxLinks = 8;

    /// <summary>The full path with every link on the way followed. A part that does not exist yet is kept as written.</summary>
    public static string Of(string path) => Resolve(path, 0);

    private static string Resolve(string path, int depth)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path; // not a path at all (a command line, say): nothing to follow
        }
        if (depth > MaxLinks)
            return full;

        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                // Nothing deeper exists yet, so there is no link to follow: the rest stays as written.
                for (var j = i; j < parts.Length; j++)
                    current = Path.Combine(current, parts[j]);
                return current;
            }

            FileSystemInfo? target = null;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            current = target is null ? next : Resolve(target.FullName, depth + 1);
        }
        return current;
    }
}
