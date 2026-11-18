using System.Reflection;

namespace MyCapture.App.Tests;

/// <summary>
/// An area owns its scene declarations and capture callback in one partial file. Adding a phase
/// requires no edit to the runner or another area's list. Duplicate IDs fail before windows open.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NativeFeelSceneGroupAttribute(int order, bool baseline = false) : Attribute
{
    internal int Order { get; } = order;
    internal bool Baseline { get; } = baseline;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class NativeFeelSceneAttribute(string id, string surface) : Attribute
{
    internal string Id { get; } = id;
    internal string Surface { get; } = surface;
}

internal sealed record NativeFeelSceneContext(NativeFeelDesktopCapture Desktop, string Profile, string Theme);
internal sealed record NativeFeelSceneDefinition(string Id, string Surface);
internal sealed record NativeFeelSceneGroup(string Name, int Order, bool Baseline,
    IReadOnlyList<NativeFeelSceneDefinition> Scenes, Action<NativeFeelSceneContext> Capture);

internal static class NativeFeelSceneRegistry
{
    internal static IReadOnlyList<NativeFeelSceneGroup> Discover(Type owner)
    {
        var groups = new List<NativeFeelSceneGroup>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodInfo method in owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            NativeFeelSceneGroupAttribute? group = method.GetCustomAttribute<NativeFeelSceneGroupAttribute>();
            NativeFeelSceneAttribute[] scenes = method.GetCustomAttributes<NativeFeelSceneAttribute>().ToArray();
            if (group is null)
            {
                if (scenes.Length != 0)
                    throw new InvalidOperationException($"{method.Name} declares scenes without a capture group.");
                continue;
            }
            if (method.ReturnType != typeof(void) || method.ContainsGenericParameters
                || !method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(NativeFeelSceneContext)]))
                throw new InvalidOperationException($"{method.Name} must be a static void method taking NativeFeelSceneContext.");
            if (scenes.Length == 0)
                throw new InvalidOperationException($"{method.Name} has no declared scenes.");
            foreach (NativeFeelSceneAttribute scene in scenes)
            {
                if (string.IsNullOrWhiteSpace(scene.Id) || string.IsNullOrWhiteSpace(scene.Surface)
                    || scene.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                    throw new InvalidOperationException($"{method.Name} has an invalid scene ID or surface.");
                if (!ids.Add(scene.Id))
                    throw new InvalidOperationException($"Duplicate evidence scene: {scene.Id}.");
            }
            groups.Add(new(method.Name, group.Order, group.Baseline,
                scenes.Select(scene => new NativeFeelSceneDefinition(scene.Id, scene.Surface)).ToArray(),
                method.CreateDelegate<Action<NativeFeelSceneContext>>()));
        }
        if (groups.Count == 0) throw new InvalidOperationException("No desktop evidence scene groups were found.");
        return groups.OrderBy(group => group.Order).ThenBy(group => group.Name, StringComparer.Ordinal).ToArray();
    }
}

internal static class NativeFeelCoverage
{
    internal const string ScaleVariable = "MYCAPTURE_NATIVE_FEEL_SCALE";
    internal static readonly IReadOnlyList<NativeFeelSceneGroup> Groups = NativeFeelSceneRegistry.Discover(typeof(SettingsThemePreviewTests));
    private static readonly IReadOnlyDictionary<string, string> Surfaces = Groups.SelectMany(group => group.Scenes)
        .ToDictionary(scene => scene.Id, scene => scene.Surface, StringComparer.Ordinal);
    internal static readonly string[] Requested = Surfaces.Keys.ToArray();
    internal static readonly string[] BaselineRequested = Groups.Where(group => group.Baseline)
        .SelectMany(group => group.Scenes).Select(scene => scene.Id).ToArray();
    internal static readonly double[] FoundationScales = [1, 1.5];
    internal static readonly string[] FoundationScenes =
        ["editor-empty-selection", "editor-property-bar", "settings-general", "foundation-settings-capture"];

    internal static string SurfaceOf(string id) => Surfaces.TryGetValue(id, out string? surface) ? surface
        : throw new ArgumentException("Unknown evidence case.", nameof(id));

    internal static double? RequestedScale(string? value) => value switch
    {
        null or "" => null,
        "100" => 1,
        "150" => 1.5,
        _ => throw new ArgumentException($"{ScaleVariable} must be 100 or 150, or unset for the current native scale."),
    };

    internal static IReadOnlyList<NativeFeelMatrixCase> FoundationMatrix(IEnumerable<NativeFeelCase> cases)
    {
        NativeFeelCase[] observed = cases.ToArray();
        return (from scale in FoundationScales
                from theme in new[] { "dark", "light" }
                from id in FoundationScenes
                let capture = observed.SingleOrDefault(item => item.Id == id && item.Theme == theme && item.Scale == scale)
                select new NativeFeelMatrixCase(id, theme, scale, capture?.Status ?? "not-run",
                    capture?.Note ?? $"Run this test at native {scale * 100:0}% with {ScaleVariable}={scale * 100:0}; no display scale is changed by the tool."))
            .ToArray();
    }
}

internal sealed record NativeFeelMatrixCase(string Id, string Theme, double Scale, string Status, string Note);
