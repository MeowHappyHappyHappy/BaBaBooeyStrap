using BaBaBooeyStrap.Core.Models;

namespace BaBaBooeyStrap.Core.Storage;

public interface IJoinTargetStore
{
    string FilePath { get; }
    IReadOnlyList<SavedJoinTarget> LoadAll();
    void SaveAll(IEnumerable<SavedJoinTarget> targets);
    void Upsert(SavedJoinTarget target);
    bool Delete(Guid id);
}
