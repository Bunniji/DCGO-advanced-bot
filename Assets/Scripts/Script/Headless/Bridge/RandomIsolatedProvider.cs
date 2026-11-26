/// <summary>
/// Picks uniformly at random from the offered options, with its OWN generator.
///
/// RandomLegalProvider draws from GameRandom, the game's seeded sequence - every one of its
/// decisions moves the sequence that also shuffles decks. That is fine for b0 inside DCGO,
/// but it makes a game impossible to replay in dcg_core: the model receives the choices
/// from the decision log and draws nothing for them, so the two sequences part at the first
/// shuffle after a decision (a mulligan, for one).
///
/// This provider leaves GameRandom to the game's own mechanics, exactly as in the model.
/// Its generator is seeded from the game seed and the seat, so a logged game is still
/// reproducible. Used for the transition test (praca-v1.2): -provider0/1 random-isolated.
/// </summary>
public sealed class RandomIsolatedProvider : IDecisionProvider
{
    readonly int _seat;
    System.Random _rng;
    int _pending = -1;

    public RandomIsolatedProvider(int seat)
    {
        _seat = seat;
    }

    public string Name { get { return "random-isolated"; } }

    public void OnGameStart(int seatPlayerId, string seatName)
    {
        // A fresh generator per game: game N's choices depend only on its own seed, so any
        // single game of a batch can be replayed on its own.
        long seed = HeadlessConfig.CurrentSeed;
        unchecked
        {
            _rng = new System.Random((int)(seed * 2654435761L) ^ (0x5bd1e995 * (_seat + 1)));
        }
    }

    public void OnGameEnd(int winnerPlayerId, string reason) { }

    public int Choose(DecisionRequest request)
    {
        if (request == null || request.Options == null || request.Options.Count == 0)
        {
            return -1;
        }

        if (_rng == null)
        {
            OnGameStart(_seat, null);
        }

        return _rng.Next(0, request.Options.Count);
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
