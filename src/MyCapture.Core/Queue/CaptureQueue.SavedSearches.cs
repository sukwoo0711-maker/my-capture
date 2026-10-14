using System.Collections.ObjectModel;
using System.Text.Json;

namespace MyCapture.Core.Queue;

public sealed record CaptureSavedSearch(Guid Id, string Name, string Query);

public sealed partial class CaptureQueue
{
    private readonly ObservableCollection<CaptureSavedSearch> _savedSearches = [];
    private readonly SemaphoreSlim _savedSearchGate = new(1, 1);
    public ReadOnlyObservableCollection<CaptureSavedSearch> SavedSearches => new(_savedSearches);

    public Task<CaptureSavedSearch?> SaveSearchAsync(string name, string query, Guid? id = null,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim(); query = query.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl) || query.Length is 0 or > 4096)
            throw new ArgumentException("A search needs a name and a query within the supported lengths.");
        return MutateSavedSearchAsync(id, new(id ?? Guid.NewGuid(), name, query), cancellationToken);
    }

    public Task<CaptureSavedSearch?> DeleteSavedSearchAsync(Guid id, CancellationToken cancellationToken = default) =>
        MutateSavedSearchAsync(id, null, cancellationToken);

    private async Task<CaptureSavedSearch?> MutateSavedSearchAsync(Guid? id, CaptureSavedSearch? replacement,
        CancellationToken cancellationToken)
    {
        await _savedSearchGate.WaitAsync(cancellationToken);
        CaptureSavedSearch[] previous = _savedSearches.ToArray();
        try
        {
            using CaptureWriteReservation reservation = await ReservePublicationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int index = id is { } key ? _savedSearches.ToList().FindIndex(search => search.Id == key) : -1;
            if (id is not null && index < 0) return null;
            if (replacement is not null && _savedSearches.Any(search => search.Id != replacement.Id
                && string.Equals(search.Name, replacement.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A search with this name already exists.");
            if (index >= 0)
            {
                if (replacement is null) _savedSearches.RemoveAt(index);
                else _savedSearches[index] = replacement;
            }
            else if (replacement is not null) _savedSearches.Add(replacement);
            try { await PublishSavedSearchesAsync(reservation); }
            catch
            {
                _savedSearches.Clear();
                foreach (CaptureSavedSearch search in previous) _savedSearches.Add(search);
                await PublishSavedSearchesAsync(null);
                throw;
            }
            return replacement;
        }
        finally { _savedSearchGate.Release(); }
    }

    private Task PublishSavedSearchesAsync(CaptureWriteReservation? reservation)
    {
        lock (_writeGate)
            return EnqueuePublication(reservation,
                BuildIndexWrites(JsonSerializer.Serialize(CreateIndexSnapshot(), SerializerOptions)));
    }

    private void RestoreSavedSearches(JsonElement? searches)
    {
        _savedSearches.Clear();
        // Optional navigation data must never invalidate the capture index or trigger recovery.
        if (searches is not { ValueKind: JsonValueKind.Array } entries) return;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            CaptureSavedSearch? search;
            try { search = entry.Deserialize<CaptureSavedSearch>(SerializerOptions); }
            catch (JsonException) { continue; }
            if (search is { Id: var id, Name: { Length: > 0 and <= 80 }, Query: { Length: > 0 and <= 4096 } }
                && id != Guid.Empty && !string.IsNullOrWhiteSpace(search.Name) && !string.IsNullOrWhiteSpace(search.Query)
                && !search.Name.Any(char.IsControl) && !_savedSearches.Any(existing => existing.Id == id
                    || string.Equals(existing.Name, search.Name, StringComparison.OrdinalIgnoreCase)))
                _savedSearches.Add(search);
        }
    }
}
