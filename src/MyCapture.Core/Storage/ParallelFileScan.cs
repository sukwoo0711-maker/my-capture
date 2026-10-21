using System.IO.Enumeration;
using System.Runtime.ExceptionServices;

namespace MyCapture.Core.Storage;

/// <summary>
/// File-system inspection that spreads independent directory listings and per-item probes over a
/// few worker threads and still returns exactly what the sequential code returns, in its order.
/// </summary>
/// <remarks>
/// Startup inspects every capture directory several times. Each listing or probe waits on the
/// file system, so a small fixed number of workers shortens the wait without changing what is
/// found. The caller's thread takes part in the work; nothing here touches a dispatcher.
/// </remarks>
public static class ParallelFileScan
{
    /// <summary>
    /// Workers per scan. The work waits on the file system, not on the CPU, so two help even on a
    /// small machine and eight are enough for an SSD.
    /// </summary>
    public static int MaxDegreeOfParallelism { get; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    // The recursive walk's options for one directory: inaccessible directories and reparse points
    // are handled by .NET exactly as in the recursive walk. AttributesToSkip replaces the default
    // Hidden | System, so hidden and system entries are listed, as they are there.
    private static readonly EnumerationOptions SingleDirectory = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Returns, for each name in <paramref name="fileNames"/>, exactly the sequence
    /// <c>Directory.EnumerateFiles(root, name, options)</c> returns with
    /// <c>RecurseSubdirectories = true</c>, <c>IgnoreInaccessible = true</c> and
    /// <c>AttributesToSkip = FileAttributes.ReparsePoint</c>, from one walk of the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET's recursive enumerator walks breadth-first: it lists one directory in the order the
    /// file system returns its entries and queues the subdirectories in the order it meets them.
    /// This walk lists all directories of one level in parallel and then concatenates their files
    /// and their subdirectories in directory order, which yields the same sequence.
    /// </para>
    /// <para>
    /// A name matches as the search pattern does (an exact, case-insensitive name on Windows).
    /// The root is opened under the caller's spelling, so a missing root throws the same
    /// exception; a subdirectory that disappears before it is listed is skipped, as the recursive
    /// enumerator skips it. If listings of one level fail, the first failure in walk order is
    /// rethrown.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string>[] FindFiles(string root, params string[] fileNames)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(fileNames);
        foreach (string fileName in fileNames)
        {
            ArgumentException.ThrowIfNullOrEmpty(fileName, nameof(fileNames));
            if (fileName.AsSpan().IndexOfAny(['*', '?', '\0', '\\', '/']) >= 0 || Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("Only exact file names without a directory can be searched.", nameof(fileNames));
            }
        }

        string[] names = (string[])fileNames.Clone();
        var matches = new List<string>[names.Length];
        for (int i = 0; i < matches.Length; i++)
        {
            matches[i] = [];
        }

        // The same root as FileSystemEnumerator: the full path without a trailing separator.
        string rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var walk = new Walk(root, rootDirectory, names);
        List<string> level = [rootDirectory];
        bool isRootLevel = true;
        while (level.Count > 0)
        {
            DirectoryListing[] listings = isRootLevel
                ? [walk.List(root, rootDirectory, isRoot: true)]
                : ListLevel(walk, level);
            isRootLevel = false;

            var next = new List<string>();
            foreach (DirectoryListing listing in listings)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (listing.Matches[i] is { } found)
                    {
                        matches[i].AddRange(found);
                    }
                }

                next.AddRange(listing.Subdirectories);
            }

            level = next;
        }

        return matches;
    }

    /// <summary>
    /// Runs <paramref name="inspect"/> for every item on up to <see cref="MaxDegreeOfParallelism"/>
    /// threads and returns one outcome per item in item order. An exception is kept with its item
    /// and rethrown by <see cref="ParallelOutcome{T}.GetValue"/>, so a caller that reads the
    /// outcomes in order fails at the same item as a sequential loop would.
    /// </summary>
    /// <remarks><paramref name="inspect"/> must only read: it runs concurrently with itself.</remarks>
    public static ParallelOutcome<TResult>[] InspectEach<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, TResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(inspect);

        var outcomes = new ParallelOutcome<TResult>[items.Count];
        Parallel.For(0, outcomes.Length, NewOptions(), i =>
        {
            try
            {
                outcomes[i] = new ParallelOutcome<TResult>(inspect(items[i]));
            }
            catch (Exception ex)
            {
                outcomes[i] = new ParallelOutcome<TResult>(ExceptionDispatchInfo.Capture(ex));
            }
        });
        return outcomes;
    }

    private static DirectoryListing[] ListLevel(Walk walk, List<string> level)
    {
        var listings = new DirectoryListing[level.Count];
        var failures = new ExceptionDispatchInfo?[level.Count];
        Parallel.For(0, level.Count, NewOptions(), i =>
        {
            try
            {
                listings[i] = walk.List(OpenPath(level[i]), level[i], isRoot: false);
            }
            catch (Exception ex)
            {
                failures[i] = ExceptionDispatchInfo.Capture(ex);
            }
        });

        foreach (ExceptionDispatchInfo? failure in failures)
        {
            failure?.Throw();
        }

        return listings;
    }

    private static ParallelOptions NewOptions() => new() { MaxDegreeOfParallelism = MaxDegreeOfParallelism };

    /// <summary>
    /// The path a subdirectory is opened under. The recursive enumerator opens it by name relative
    /// to its parent, so a name the Win32 path rules would rewrite (a trailing dot or space) still
    /// reaches that directory. The extended-length form skips that rewriting in the same way.
    /// </summary>
    private static string OpenPath(string directory)
    {
        if (directory.Length >= 4
            && IsSeparator(directory[0])
            && (IsSeparator(directory[1]) || directory[1] == '?')
            && directory[2] is '?' or '.'
            && IsSeparator(directory[3]))
        {
            return directory;
        }

        return IsSeparator(directory[0]) && directory.Length > 1 && IsSeparator(directory[1])
            ? @"\\?\UNC\" + directory[2..]
            : @"\\?\" + directory;
    }

    private static bool IsSeparator(char value) => value is '\\' or '/';

    private sealed class Walk(string originalRoot, string rootDirectory, string[] names)
    {
        public DirectoryListing List(string openPath, string directory, bool isRoot)
        {
            FileSystemEnumerable<(string Name, bool IsDirectory)> entries;
            try
            {
                entries = new FileSystemEnumerable<(string Name, bool IsDirectory)>(
                    openPath,
                    (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory),
                    SingleDirectory)
                {
                    // Directories are kept to walk into; "." and ".." and skipped attributes never
                    // reach this predicate.
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        entry.IsDirectory || MatchIndex(entry.FileName, 0) >= 0,
                };
            }
            catch (DirectoryNotFoundException) when (!isRoot)
            {
                return DirectoryListing.Empty(names.Length);
            }

            var listing = DirectoryListing.Empty(names.Length);
            foreach ((string name, bool isDirectory) in entries)
            {
                if (isDirectory)
                {
                    listing.Subdirectories.Add(Path.Join(directory, name));
                    continue;
                }

                string path = SpecifiedPath(directory, name);
                for (int i = MatchIndex(name, 0); i >= 0; i = MatchIndex(name, i + 1))
                {
                    (listing.Matches[i] ??= []).Add(path);
                }
            }

            return listing;
        }

        private int MatchIndex(ReadOnlySpan<char> fileName, int start)
        {
            for (int i = start; i < names.Length; i++)
            {
                if (FileSystemName.MatchesSimpleExpression(names[i], fileName, ignoreCase: true))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>FileSystemEntry.ToSpecifiedFullPath: the caller's root spelling, then the rest.</summary>
        private string SpecifiedPath(string directory, string fileName)
        {
            ReadOnlySpan<char> relative = directory.AsSpan(rootDirectory.Length);
            if (Path.EndsInDirectorySeparator(originalRoot) && relative.Length > 0 && IsSeparator(relative[0]))
            {
                relative = relative[1..];
            }

            return Path.Join(originalRoot, relative, fileName);
        }
    }

    private sealed class DirectoryListing
    {
        private DirectoryListing(int nameCount) => Matches = new List<string>?[nameCount];

        public List<string>?[] Matches { get; }

        public List<string> Subdirectories { get; } = [];

        public static DirectoryListing Empty(int nameCount) => new(nameCount);
    }
}

/// <summary>One item's result from <see cref="ParallelFileScan.InspectEach"/>, or its exception.</summary>
public readonly struct ParallelOutcome<T>
{
    private readonly T _value;
    private readonly ExceptionDispatchInfo? _failure;

    internal ParallelOutcome(T value)
    {
        _value = value;
        _failure = null;
    }

    internal ParallelOutcome(ExceptionDispatchInfo failure)
    {
        _value = default!;
        _failure = failure;
    }

    /// <summary>Returns the item's result, or rethrows the exception its inspection threw.</summary>
    public T GetValue()
    {
        _failure?.Throw();
        return _value;
    }
}
