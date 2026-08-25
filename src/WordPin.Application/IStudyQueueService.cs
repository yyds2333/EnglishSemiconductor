using WordPin.Domain;

namespace WordPin.Application;

public interface IStudyQueueService
{
    Task<StudySessionSnapshot> GetOrCreateAsync(
        string localDate,
        DateTimeOffset now,
        int dailyLimit = 12,
        CancellationToken cancellationToken = default);
}
