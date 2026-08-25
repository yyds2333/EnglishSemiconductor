using WordPin.Domain;

namespace WordPin.Infrastructure.Tests;

public sealed class MasteryAlgorithmTests
{
    [Fact]
    public void GoodAndEasyUseDeterministicIntervalsAndEvidence()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var initial = new MasteryState();

        var good = MasteryAlgorithm.Evaluate(initial, ReviewFeedback.Good, start);
        var easy = MasteryAlgorithm.Evaluate(good.After, ReviewFeedback.Easy, start.AddDays(2));

        Assert.Equal(2, good.After.StabilityDays);
        Assert.Equal(2, good.After.ReviewIntervalDays);
        Assert.Equal(10, good.After.EvidencePointsTenths);
        Assert.Equal(4, easy.After.StabilityDays);
        Assert.Equal(4, easy.After.ReviewIntervalDays);
        Assert.Equal(25, easy.After.EvidencePointsTenths);
        Assert.Equal(2, easy.After.Level);
    }

    [Fact]
    public void AgainLowersStabilityAndCapsDemotion()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var before = new MasteryState(
            Score: 92,
            Level: 5,
            StabilityDays: 30,
            EvidencePointsTenths: 50,
            LapseCount: 0,
            SuccessStreak: 4,
            ReviewIntervalDays: 30,
            FirstReviewedAt: start.AddDays(-30),
            LastReviewedAt: start.AddDays(-1),
            NextReviewAt: start,
            LastFeedback: ReviewFeedback.Good);

        var evaluation = MasteryAlgorithm.Evaluate(before, ReviewFeedback.Again, start);

        Assert.Equal(7.5, evaluation.After.StabilityDays);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalDays, evaluation.After.ReviewIntervalDays);
        Assert.Equal(1, evaluation.After.LapseCount);
        Assert.Equal(0, evaluation.After.SuccessStreak);
        Assert.InRange(evaluation.After.Level, 0, 4);
        Assert.True(evaluation.After.Score < before.Score);
    }
}
