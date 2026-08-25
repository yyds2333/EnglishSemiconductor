using WordPin.Domain;

namespace WordPin.Application;

public interface IDefinitionRepository
{
    Task<IReadOnlyList<SavedDefinition>> GetForWordAsync(
        Guid wordId,
        CancellationToken cancellationToken = default);

    Task<SavedDefinition> SaveAsync(
        DefinitionDraft draft,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);
}
