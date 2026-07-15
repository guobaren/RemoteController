namespace Rc.InteractiveTestApp;

public sealed record InteractiveChallengeResult(bool IsSuccess, int NextRunCount, string Status);

public static class InteractiveChallengeSession
{
    public static InteractiveChallengeResult Evaluate(int historicalRunCount, string? historicalInput, int challenge, string? challengeInput)
    {
        if (!int.TryParse(historicalInput, out var suppliedHistory) || suppliedHistory != historicalRunCount)
        {
            return new InteractiveChallengeResult(false, historicalRunCount, "FIRST_INPUT_INVALID");
        }

        if (!int.TryParse(challengeInput, out var suppliedChallenge) || suppliedChallenge != challenge)
        {
            return new InteractiveChallengeResult(false, historicalRunCount, "SECOND_INPUT_INVALID");
        }

        return new InteractiveChallengeResult(true, checked(historicalRunCount + 1), "PASS");
    }
}
