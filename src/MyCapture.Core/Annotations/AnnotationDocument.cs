using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using MyCapture.Core.Serialization;
using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Annotations;

/// <summary>
/// The editable annotation layer for one capture.
/// </summary>
/// <remarks>
/// <para>
/// Persisted alongside the capture as <c>layers.json</c>. Keeping this document
/// instead of only the flattened PNG is what allows a capture from days earlier to
/// be reopened and its arrow nudged two pixels — the single capability that both
/// competing products lack.
/// </para>
/// <para>
/// Not thread-safe. All mutation happens on the UI thread; the queue writes the
/// serialised form on a background thread from an already-produced string.
/// </para>
/// </remarks>
public sealed class AnnotationDocument
{
    /// <summary>
    /// Bumped only when a change cannot be absorbed by property defaults.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = JsonDefaults.Compact;

    private static readonly JsonSerializerOptions ReadableSerializerOptions = JsonDefaults.Readable;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Pixel size of the image these coordinates apply to.
    /// </summary>
    /// <remarks>
    /// Recorded so a document can be rescaled correctly if it is ever applied to a
    /// resized version of the capture, and so a mismatch can be detected instead of
    /// silently drawing annotations in the wrong place.
    /// </remarks>
    public int CanvasWidth { get; set; }

    public int CanvasHeight { get; set; }

    /// <summary>
    /// Items in paint order. Index order is authoritative; <see cref="AnnotationItem.ZIndex"/>
    /// is a persisted mirror used to restore order across a load.
    /// </summary>
    public ObservableCollection<AnnotationItem> Items { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Raised on any structural change. Property-level changes are observed by
    /// subscribing to the items themselves.
    /// </summary>
    public event NotifyCollectionChangedEventHandler? ItemsChanged
    {
        add => Items.CollectionChanged += value;
        remove => Items.CollectionChanged -= value;
    }

    public static AnnotationDocument CreateFor(int canvasWidth, int canvasHeight) =>
        new() { CanvasWidth = canvasWidth, CanvasHeight = canvasHeight };

    public void Add(AnnotationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.ZIndex = Items.Count == 0 ? 0 : Items.Max(i => i.ZIndex) + 1;
        Items.Add(item);
    }

    public bool Remove(AnnotationItem item) => Items.Remove(item);

    /// <summary>
    /// Reinserts <paramref name="item"/> at <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// Undo of a delete must restore paint order, not append to the top. Appending
    /// would silently move the restored annotation above shapes it used to sit
    /// behind.
    /// </remarks>
    public void Insert(int index, AnnotationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Items.Insert(Math.Clamp(index, 0, Items.Count), item);
    }

    public int IndexOf(AnnotationItem item) => Items.IndexOf(item);

    /// <summary>
    /// Topmost item within <paramref name="tolerance"/> of <paramref name="point"/>.
    /// </summary>
    /// <remarks>
    /// Iterates back to front so the visually topmost annotation wins, which is what
    /// the user expects when annotations overlap.
    /// </remarks>
    public AnnotationItem? HitTest(PointD point, double tolerance)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            AnnotationItem item = Items[i];
            if (item.DistanceTo(point) <= tolerance)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Every item intersecting <paramref name="region"/>, in paint order.
    /// </summary>
    public IReadOnlyList<AnnotationItem> HitTestRegion(RectD region)
    {
        RectD r = region.Normalized();

        var result = new List<AnnotationItem>();
        foreach (AnnotationItem item in Items)
        {
            RectD b = item.Bounds;
            bool intersects = b.Left <= r.Right && b.Right >= r.Left &&
                              b.Top <= r.Bottom && b.Bottom >= r.Top;
            if (intersects)
            {
                result.Add(item);
            }
        }

        return result;
    }

    public void BringToFront(AnnotationItem item)
    {
        int index = Items.IndexOf(item);
        if (index >= 0 && index != Items.Count - 1)
        {
            Items.Move(index, Items.Count - 1);
        }
    }

    public void SendToBack(AnnotationItem item)
    {
        int index = Items.IndexOf(item);
        if (index > 0)
        {
            Items.Move(index, 0);
        }
    }

    /// <summary>
    /// Rewrites <see cref="AnnotationItem.ZIndex"/> to match index order.
    /// </summary>
    /// <remarks>
    /// Called before serialising. Reordering through <see cref="ObservableCollection{T}.Move"/>
    /// changes index order without touching the stored mirror, so without this the
    /// order would be lost on reload.
    /// </remarks>
    public void NormalizeZIndices()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].ZIndex = i;
        }
    }

    public string ToJson(bool indented = false)
    {
        NormalizeZIndices();
        return JsonSerializer.Serialize(this, indented ? ReadableSerializerOptions : SerializerOptions);
    }

    /// <summary>
    /// Parses a layer document.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the text is not a usable document. Callers treat
    /// that as "this capture has no annotations" rather than failing the load: the
    /// original PNG is still intact and showing it is better than showing nothing.
    /// </returns>
    public static AnnotationDocument? TryFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            AnnotationDocument? doc = JsonSerializer.Deserialize<AnnotationDocument>(json, SerializerOptions);
            if (doc is null)
            {
                return null;
            }

            // Restore paint order from the persisted mirror, then discard it as the
            // source of truth for the rest of the session.
            if (doc.Items.Count > 1)
            {
                List<AnnotationItem> ordered = [.. doc.Items.OrderBy(i => i.ZIndex)];
                doc.Items.Clear();
                foreach (AnnotationItem item in ordered)
                {
                    doc.Items.Add(item);
                }
            }

            doc.NormalizeZIndices();
            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deep copy, giving every item a fresh identity.
    /// </summary>
    public AnnotationDocument Clone()
    {
        var clone = new AnnotationDocument
        {
            SchemaVersion = SchemaVersion,
            CanvasWidth = CanvasWidth,
            CanvasHeight = CanvasHeight,
        };

        foreach (AnnotationItem item in Items)
        {
            clone.Items.Add(item.Clone());
        }

        clone.NormalizeZIndices();
        return clone;
    }
}
