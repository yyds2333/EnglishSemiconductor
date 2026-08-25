using WordPin.Domain;

namespace WordPin.Application;

public interface IWordRepository
{
    Task<WordCaptureResult> CaptureAsync(
        NewWordCapture capture,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WordRecord>> FindCandidatesAsync(
        string term,
        string language = "en",
        EntryKind entryKind = EntryKind.Word,
        CancellationToken cancellationToken = default);

    Task<bool> UndoLastCaptureAsync(
        WordCaptureResult capture,
        CancellationToken cancellationToken = default);
}
