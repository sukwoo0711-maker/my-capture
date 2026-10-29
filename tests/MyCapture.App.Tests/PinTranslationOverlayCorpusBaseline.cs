using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Tests;

/// <summary>Optional replay of a retained pre-change compositor; never loads a profile or a window.</summary>
internal sealed class PinTranslationOverlayCorpusBaseline
{
    internal const string AssemblyVariable = "MYCAPTURE_PIN_OVERLAY_BASELINE_ASSEMBLY";
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly Assembly assembly;
    private readonly Type overlay;
    private readonly Type translation;

    internal PinTranslationOverlayCorpusBaseline(string path)
    {
        var context = new BaselineContext();
        assembly = context.LoadFromAssemblyPath(System.IO.Path.GetFullPath(path));
        overlay = assembly.GetType("MyCapture.App.Pinning.PinTranslationOverlay", throwOnError: true)!;
        translation = assembly.GetType("MyCapture.App.Pinning.PinOverlayTranslation", throwOnError: true)!;
    }

    internal string Version => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal async Task<(BitmapSource Image, IReadOnlyList<PinOverlayCorpusDecision> Decisions)> RunAsync(
        OcrResult recognized, BitmapSource image, ITextTranslator translator, PinTranslationDirection direction)
    {
        bool canOverlay = (bool)overlay.GetMethod("CanOverlay", Members)!.Invoke(null, [recognized])!;
        if (!canOverlay) return (image, []);
        object[] blocks = ((IEnumerable)overlay.GetMethod("BuildBlocks", Members)!.Invoke(null, [recognized])!).Cast<object>().ToArray();
        var units = new List<string>();
        var counts = new int[blocks.Length];
        for (int index = 0; index < blocks.Length; index++)
        {
            string source = Get<string>(blocks[index], "Text");
            if (PinTranslationService.Evaluate(source, direction == PinTranslationDirection.EnglishToKorean).Availability
                != PinTranslationAvailability.Translatable) continue;
            string[] sentences = [.. TranslationSegmenter.Sentences(source)];
            if (sentences.Length == 0) sentences = [source];
            counts[index] = sentences.Length;
            units.AddRange(sentences);
        }
        IReadOnlyList<TranslatedText> results = units.Count == 0 ? []
            : await translator.TranslateAsync(units, null, CancellationToken.None);
        if (results.Count != units.Count) throw new InvalidOperationException("Incomplete baseline translation batch.");
        MethodInfo preserves = assembly.GetType("MyCapture.App.Pinning.PinTranslationService", true)!
            .GetMethod("PreservesProtectedValues", Members)!;
        Array translations = Array.CreateInstance(translation, blocks.Length);
        var candidates = new string?[blocks.Length];
        int cursor = 0;
        for (int index = 0; index < blocks.Length; index++)
        {
            var parts = new List<string>();
            bool anyAccepted = false;
            for (int sentence = 0; sentence < counts[index]; sentence++, cursor++)
            {
                TranslatedText result = results[cursor];
                if (!string.Equals(result.Source, units[cursor], StringComparison.Ordinal))
                    throw new InvalidOperationException("Mismatched baseline translation batch.");
                string text = (result.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
                bool safe = text.Length > 0 && !result.NeedsReview
                    && (bool)preserves.Invoke(null, [units[cursor], text])!;
                anyAccepted |= safe;
                parts.Add(safe ? text : units[cursor].Trim());
            }
            candidates[index] = anyAccepted ? string.Join(" ", parts) : null;
            translations.SetValue(Activator.CreateInstance(translation, blocks[index], candidates[index]), index);
        }
        var rendered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        MethodInfo compose = overlay.GetMethod("Compose", Members)!;
        Type callbackType = compose.GetParameters()[3].ParameterType;
        Type[] callbackArgs = callbackType.GetGenericArguments();
        ParameterExpression[] parameters = callbackArgs.Select(Expression.Parameter).ToArray();
        Expression add = Expression.Call(Expression.Constant(rendered), typeof(HashSet<object>).GetMethod("Add")!,
            Expression.Convert(parameters[0], typeof(object)));
        Delegate callback = Expression.Lambda(callbackType, Expression.Block(add, Expression.Empty()), parameters).Compile();
        object composed = compose.Invoke(null, [image, translations, true, callback])!;
        var decisions = blocks.Select((block, index) =>
        {
            string source = Get<string>(block, "Text");
            string reason = rendered.Contains(block) ? "rendered" : counts[index] == 0 ? "not-eligible"
                : candidates[index] is null ? "all-rejected"
                : PinTranslationOverlay.NormalizeWhitespace(source) == PinTranslationOverlay.NormalizeWhitespace(candidates[index]!)
                    ? "identity" : "fit";
            OcrLine[] lines = ((IEnumerable)Get<object>(block, "Lines")).Cast<OcrLine>().ToArray();
            return new PinOverlayCorpusDecision(source, Get<OcrRect>(block, "Bounds"), reason,
                lines.Sum(line => line.Bounds.Width * line.Bounds.Height));
        }).ToArray();
        return (Get<BitmapSource>(composed, "Image"), decisions);
    }

    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Members)!.GetValue(value)!;

    private sealed class BaselineContext() : AssemblyLoadContext("PinOverlayCorpusBaseline", isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => Default.LoadFromAssemblyName(assemblyName);
    }
}
