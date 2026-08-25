namespace WordPin.Domain;

public enum ReviewFeedback
{
    Again = 0,
    Hard = 1,
    Good = 2,
    Easy = 3
}

public sealed record MasteryState(
    int Score = 0,
    int Level = 0,
    double StabilityDays = 0,
    int EvidencePointsTenths = 0,
    int LapseCount = 0,
    int SuccessStreak = 0,
    double ReviewIntervalDays = 0,
    DateTimeOffset? FirstReviewedAt = null,
    DateTimeOffset? LastReviewedAt = null,
    DateTimeOffset? NextReviewAt = null,
    ReviewFeedback? LastFeedback = null,
    string AlgorithmVersion = MasteryAlgorithm.Version);

public sealed record MasteryEvaluation(
    MasteryState Before,
    MasteryState After,
    double ActualElapsedDays,
    DateTimeOffset ReviewedAt,
    ReviewFeedback Feedback,
    bool UsedHint);

public static class MasteryAlgorithm
{
    public const string Version = "mvp-1";
    private const double MaxIntervalDays = 180;

    public static MasteryEvaluation Evaluate(
        MasteryState before,
        ReviewFeedback feedback,
        DateTimeOffset reviewedAt,
        bool usedHint = false)
    {
        ArgumentNullException.ThrowIfNull(before);
        var normalizedReviewedAt = reviewedAt.ToUniversalTime();
        var actualElapsedDays = before.LastReviewedAt is null
            ? 0
            : Math.Max(0, (normalizedReviewedAt - before.LastReviewedAt.Value).TotalDays);
        var firstReviewedAt = before.FirstReviewedAt ?? normalizedReviewedAt;
        var evidenceGain = GetEvidenceGain(before, feedback, actualElapsedDays);
        var evidence = feedback == ReviewFeedback.Again
            ? before.EvidencePointsTenths
            : Math.Min(50, before.EvidencePointsTenths + evidenceGain);
        var lapseCount = feedback == ReviewFeedback.Again ? before.LapseCount + 1 : before.LapseCount;
        var stabilityDays = GetStabilityDays(before.StabilityDays, feedback);
        var intervalDays = Math.Min(MaxIntervalDays, GetIntervalDays(before.ReviewIntervalDays, feedback));
        var successStreak = feedback == ReviewFeedback.Again ? 0 : before.SuccessStreak + 1;
        var score = CalculateScore(stabilityDays, evidence, lapseCount, before, feedback);
        var level = CalculateLevel(
            score,
            stabilityDays,
            evidence,
            firstReviewedAt,
            normalizedReviewedAt,
            before.Level,
            feedback);

        var after = new MasteryState(
            Score: score,
            Level: level,
            StabilityDays: stabilityDays,
            EvidencePointsTenths: evidence,
            LapseCount: lapseCount,
            SuccessStreak: successStreak,
            ReviewIntervalDays: intervalDays,
            FirstReviewedAt: firstReviewedAt,
            LastReviewedAt: normalizedReviewedAt,
            NextReviewAt: normalizedReviewedAt.AddDays(intervalDays),
            LastFeedback: feedback,
            AlgorithmVersion: Version);
        return new MasteryEvaluation(before, after, actualElapsedDays, normalizedReviewedAt, feedback, usedHint);
    }

    private static int GetEvidenceGain(MasteryState before, ReviewFeedback feedback, double actualElapsedDays)
    {
        var baseGain = feedback switch
        {
            ReviewFeedback.Hard => 5,
            ReviewFeedback.Good => 10,
            ReviewFeedback.Easy => 15,
            _ => 0
        };
        if (baseGain == 0 || before.ReviewIntervalDays <= 0)
        {
            return baseGain;
        }

        return actualElapsedDays >= before.ReviewIntervalDays * 0.8 ? baseGain : 0;
    }

    private static double GetStabilityDays(double oldStability, ReviewFeedback feedback) => feedback switch
    {
        ReviewFeedback.Again => Math.Max(0.25, oldStability * 0.25),
        ReviewFeedback.Hard => Math.Max(1, oldStability * 1.2),
        ReviewFeedback.Good => Math.Max(2, oldStability * 2),
        ReviewFeedback.Easy => Math.Max(4, oldStability * 2),
        _ => throw new ArgumentOutOfRangeException(nameof(feedback), feedback, "Unsupported review feedback.")
    };

    private static double GetIntervalDays(double oldInterval, ReviewFeedback feedback) => feedback switch
    {
        ReviewFeedback.Again => TimeSpan.FromMinutes(10).TotalDays,
        ReviewFeedback.Hard => Math.Max(1, oldInterval * 1.2),
        ReviewFeedback.Good => Math.Max(2, oldInterval * 2),
        ReviewFeedback.Easy => Math.Max(4, oldInterval * 2),
        _ => throw new ArgumentOutOfRangeException(nameof(feedback), feedback, "Unsupported review feedback.")
    };

    private static int CalculateScore(
        double stabilityDays,
        int evidencePointsTenths,
        int lapseCount,
        MasteryState before,
        ReviewFeedback feedback)
    {
        var score = (int)Math.Round(
            stabilityDays * 2 + Math.Min(evidencePointsTenths / 10d, 5) * 8 - lapseCount * 6,
            MidpointRounding.ToEven);
        if (feedback == ReviewFeedback.Again && before.LastFeedback == ReviewFeedback.Again)
        {
            score -= 8;
        }

        return Math.Clamp(score, 0, 100);
    }

    private static int CalculateLevel(
        int score,
        double stabilityDays,
        int evidencePointsTenths,
        DateTimeOffset firstReviewedAt,
        DateTimeOffset reviewedAt,
        int previousLevel,
        ReviewFeedback feedback)
    {
        var actualDays = Math.Max(0, (reviewedAt - firstReviewedAt).TotalDays);
        var level = score >= 88 && stabilityDays >= 21 && evidencePointsTenths >= 50 && actualDays >= 21
            ? 5
            : score >= 70 && stabilityDays >= 7 && evidencePointsTenths >= 30 && actualDays >= 7
                ? 4
                : score >= 45
                    ? 3
                    : score >= 25
                        ? 2
                        : previousLevel > 0 || score > 0 ? 1 : 0;

        if (feedback == ReviewFeedback.Again && previousLevel >= 4)
        {
            level = Math.Min(level, previousLevel == 5 ? 4 : 3);
        }

        return level;
    }
}
