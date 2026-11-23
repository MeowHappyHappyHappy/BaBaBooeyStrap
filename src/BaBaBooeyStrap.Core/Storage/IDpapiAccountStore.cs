using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

public interface IDpapiAccountStore
{
    string FilePath { get; }
    IReadOnlyList<RobloxAccount> LoadAll();
    void SaveAll(IEnumerable<RobloxAccount> accounts);
    RobloxAccount Upsert(RobloxAccount account);
    RobloxAccount? GetById(Guid id);
    bool Delete(Guid id);

    bool UpdateLastLaunchedAt(Guid id, DateTimeOffset timestamp)
    {
        var existing = GetById(id);
        if (existing == null) return false;
        existing.LastLaunchedAt = timestamp;
        Upsert(existing);
        return true;
    }

    RobloxAccount? GetMetadataByUserId(long userId)
    {
        var match = LoadAll().FirstOrDefault(a => a.UserId == userId);
        if (match == null) return null;
        return new RobloxAccount
        {
            Id = match.Id,
            Username = match.Username,
            DisplayName = match.DisplayName,
            UserId = match.UserId,
            Notes = match.Notes,
            CreatedAt = match.CreatedAt,
            LastLaunchedAt = match.LastLaunchedAt,
            RobloxSettings = match.RobloxSettings
        };
    }
}
