using System.IO;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

/// <summary>Names a whole portable tree within the receiving directory's path budget.</summary>
internal static class GalleryExportPathBudget
{
    internal const int MaximumPathLength = 240;

    internal static GalleryFolderExportPlan Fit(GalleryFolderExportPlan plan, int prefixLength)
    {
        int budget = MaximumPathLength - prefixLength - 1;
        if (plan.Directories.Concat(plan.Entries.Select(e => e.RelativePath)).All(p => p.Length <= budget)) return plan;
        var root = new Node(string.Empty, true);
        foreach (string path in plan.Directories) Add(path, true);
        foreach (var entry in plan.Entries) Add(entry.RelativePath, false);
        void Add(string path, bool directory)
        {
            Node node = root;
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!node.Children.TryGetValue(parts[i], out var child))
                    node.Children.Add(parts[i], child = new Node(parts[i], i < parts.Length - 1 || directory));
                node = child;
            }
            node.Original = path;
        }
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void NameChildren(Node parent, string prefix)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Node child in parent.Children.Values.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
            {
                int remaining = budget - (prefix.Length == 0 ? 0 : prefix.Length + 1);
                int depth = child.Depth;
                // Share the remaining characters across the deepest branch; leave room
                // for separators and a media extension even at the sixteenth folder.
                int limit = child.Directory ? (remaining - (depth - 1) - 4) / depth : remaining;
                string name = Allocate(child.Name, limit, child.Directory, used);
                string relative = prefix.Length == 0 ? name : prefix + "/" + name;
                if (child.Original is { } original) names.Add(original, relative);
                NameChildren(child, relative);
            }
        }
        NameChildren(root, string.Empty);
        var entries = plan.Entries.Select(e => e with { RelativePath = names[e.RelativePath] }).ToArray();
        string[] directories = plan.Directories.Select(d => names[d]).ToArray();
        string[] roots = directories.Concat(entries.Select(e => e.RelativePath))
            .Select(p => p.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(entries, directories, roots);
    }

    private static string Allocate(string original, int limit, bool directory, HashSet<string> used)
    {
        string extension = directory ? string.Empty : Path.GetExtension(original);
        string stem = directory ? original : Path.GetFileNameWithoutExtension(original);
        for (int index = 1; index <= 10_000; index++)
        {
            string suffix = index == 1 ? string.Empty : "~" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int available = limit - extension.Length - suffix.Length;
            if (available < 1) throw new PathTooLongException("The export destination leaves too little room for the folder hierarchy.");
            string shortened = stem[..Math.Min(stem.Length, available)].TrimEnd('.', ' ');
            if (shortened.Length > 0 && char.IsHighSurrogate(shortened[^1])) shortened = shortened[..^1];
            shortened = CaptureFolderNaming.SafeStem(shortened, "_");
            if (shortened.Length > available) shortened = "_";
            string candidate = shortened + suffix + extension;
            if (used.Add(candidate)) return candidate;
        }
        throw new IOException("No available export name.");
    }

    private sealed class Node(string name, bool directory)
    {
        internal string Name { get; } = name;
        internal bool Directory { get; } = directory;
        internal string? Original { get; set; }
        internal Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal int Depth => 1 + (Children.Count == 0 ? 0 : Children.Values.Max(n => n.Depth));
    }
}
