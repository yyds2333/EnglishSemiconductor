[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Clamp([double]$value, [double]$minimum, [double]$maximum) {
    return [Math]::Min($maximum, [Math]::Max($minimum, $value))
}

function Apply-Review([hashtable]$state, [string]$rating, [datetime]$reviewedAt) {
    $previousLevel = $state.Level
    $elapsedDays = if ($null -eq $state.LastReviewedAt) { 0 } else { [Math]::Max(0, ($reviewedAt - $state.LastReviewedAt).TotalDays) }

    switch ($rating) {
        'AGAIN' {
            $state.StabilityDays = [Math]::Max(0.25, $state.StabilityDays * 0.25)
            $state.IntervalDays = 10.0 / 1440.0
            $state.LapseCount++
            $state.SuccessStreak = 0
        }
        'HARD' {
            $state.EvidencePoints += 0.5
            $state.StabilityDays = [Math]::Max(1, $state.StabilityDays * 1.2)
            $state.IntervalDays = [Math]::Min(180, [Math]::Max(1, $state.IntervalDays * 1.2))
            $state.SuccessStreak++
        }
        'GOOD' {
            $state.EvidencePoints += 1
            $state.StabilityDays = [Math]::Max(2, $state.StabilityDays * 2.0)
            $state.IntervalDays = [Math]::Min(180, [Math]::Max(2, $state.IntervalDays * 2.0))
            $state.SuccessStreak++
        }
        'EASY' {
            $state.EvidencePoints += 1.5
            $state.StabilityDays = [Math]::Max(4, $state.StabilityDays * 2.0)
            $state.IntervalDays = [Math]::Min(180, [Math]::Max(4, $state.IntervalDays * 2.0))
            $state.SuccessStreak++
        }
        default { throw "Unsupported rating: $rating" }
    }

    if ($null -eq $state.FirstReviewedAt) { $state.FirstReviewedAt = $reviewedAt }
    $state.LastReviewedAt = $reviewedAt
    $state.ReviewCount++
    $rawScore = ($state.StabilityDays * 2) + ([Math]::Min($state.EvidencePoints, 5) * 8) - ($state.LapseCount * 6)
    $state.Score = [int][Math]::Round((Clamp $rawScore 0 100), 0)
    $daysSinceFirst = ($reviewedAt - $state.FirstReviewedAt).TotalDays

    if ($state.Score -ge 88 -and $state.StabilityDays -ge 21 -and $state.EvidencePoints -ge 5 -and $daysSinceFirst -ge 21) {
        $state.Level = 5
    } elseif ($state.Score -ge 70 -and $state.StabilityDays -ge 7 -and $state.EvidencePoints -ge 3 -and $daysSinceFirst -ge 7) {
        $state.Level = 4
    } elseif ($state.Score -ge 45) {
        $state.Level = 3
    } elseif ($state.Score -ge 25) {
        $state.Level = 2
    } else {
        $state.Level = 1
    }

    if ($rating -eq 'AGAIN' -and $previousLevel -ge 4) {
        $state.Level = [Math]::Min($state.Level, $previousLevel - 1)
    }
    if ($rating -eq 'AGAIN' -and $state.LapseCount -ge 2) {
        $state.Score = [int][Math]::Max(0, $state.Score - 8)
        $state.Level = [Math]::Max(1, [Math]::Min($state.Level, $previousLevel - 1))
    }

    [pscustomobject]@{
        Rating = $rating
        ReviewAt = $reviewedAt
        ElapsedDays = $elapsedDays
        Score = $state.Score
        Level = $state.Level
        StabilityDays = $state.StabilityDays
        EvidencePoints = $state.EvidencePoints
        NextIntervalDays = $state.IntervalDays
    }
}

function Invoke-Sequence([string]$name, [string[]]$ratings) {
    $state = @{
        Level = 0
        Score = 0
        StabilityDays = 0.0
        IntervalDays = 0.0
        EvidencePoints = 0.0
        LapseCount = 0
        SuccessStreak = 0
        ReviewCount = 0
        FirstReviewedAt = $null
        LastReviewedAt = $null
    }
    $reviewAt = [datetime]'2026-01-01T09:00:00Z'
    $rows = foreach ($rating in $ratings) {
        $row = Apply-Review $state $rating $reviewAt
        $reviewAt = $reviewAt.AddDays($state.IntervalDays)
        $row
    }
    $last = $rows[-1]
    [pscustomobject]@{ Name = $name; Reviews = $rows.Count; LastLevel = $last.Level; LastScore = $last.Score; LastReviewAt = $last.ReviewAt; Rows = $rows }
}

$good = Invoke-Sequence 'GOOD x5' @('GOOD', 'GOOD', 'GOOD', 'GOOD', 'GOOD')
$easy = Invoke-Sequence 'EASY x4' @('EASY', 'EASY', 'EASY', 'EASY')
$failure = Invoke-Sequence 'GOOD x4, AGAIN' @('GOOD', 'GOOD', 'GOOD', 'GOOD', 'AGAIN')

if ($good.LastLevel -lt 5) { throw 'GOOD sequence did not reach L5.' }
if ($easy.LastLevel -lt 5) { throw 'EASY sequence did not reach L5.' }
if ($easy.LastReviewAt -gt $good.LastReviewAt) { throw 'EASY reached L5 later than GOOD.' }
if ($failure.LastLevel -gt 3) { throw 'L4 failure did not cap the level at L3 or below.' }

@($good, $easy, $failure) | Select-Object Name, Reviews, LastLevel, LastScore, LastReviewAt | Format-Table -AutoSize
Write-Output 'Mastery invariants passed.'
