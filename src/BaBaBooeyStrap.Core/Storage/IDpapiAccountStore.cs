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
}
