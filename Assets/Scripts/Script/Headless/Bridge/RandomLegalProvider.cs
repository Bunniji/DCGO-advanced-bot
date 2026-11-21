/// <summary>
/// Picks uniformly at random from the offered options, using the seeded GameRandom.
///
/// Two jobs, neither of them decorative:
///
///   1. It is baseline B0 played inside DCGO rather than inside dcg_core, which is what
///      makes the two engines' B0 numbers comparable.
///
///   2. It is the test harness for LegalActionGenerator. A provider that only ever plays
///      offered actions must never produce an illegal play or a soft-lock; if it does, the
///      generator offered something the engine will not accept, and that is a bug worth
///      finding before an agent is blamed for it.
///
/// Uses GameRandom, not UnityEngine.Random, so a run stays reproducible from its seed.
/// </summary>
public sealed class RandomLegalProvider : IDecisionProvider
{
    int _pending = -1;

    public string Name { get { return "random-legal"; } }

    public void OnGameStart(int seatPlayerId, string seatName) { }

    public void OnGameEnd(int winnerPlayerId, string reason) { }

    public int Choose(DecisionRequest request)
    {
        if (request == null || request.Options == null || request.Options.Count == 0)
        {
            return -1;
        }

        return GameRandom.Range(0, request.Options.Count);
    }

    public void BeginChoose(DecisionRequest request)
    {
        _pending = Choose(request);
    }

    public bool TryGetChoice(out int index)
    {
        index = _pending;
        return true;
    }

    public void CancelChoose()
    {
        _pending = -1;
    }
}
