using System;

/// <summary>
/// The seam between the emulator and the batch harness.
///
/// Kept in its own file on purpose: the changes needed inside DCGO's own scripts
/// stay down to a single call each, so the diff against upstream is trivial to review
/// and the normal (non-headless) code path is provably untouched.
/// </summary>
public static class HeadlessHooks
{
    /// <summary>
    /// Raised once per game from TurnStateMachine.EndGame.
    /// winnerPlayerId is -1 for a draw or a disconnect.
    /// </summary>
    public static event Action<GameEndInfo> GameEnded;

    /// <summary>
    /// Deck the bot side should use. When null, DCGO keeps its default behaviour
    /// of picking a random deck out of the local saved deck list.
    /// </summary>
    public static DeckData OpponentDeckOverride { get; set; }

    /// <summary>
    /// Whether this player's choices go through the interactive UI.
    ///
    /// DCGO routes every selection by `isYou`: the local seat opens a panel and waits for a
    /// click, the opponent seat takes the automatic path. In a headless run nobody is there
    /// to click, so the local seat has to take the automatic path too.
    /// </summary>
    public static bool UsesInteractiveUI(Player player)
    {
        return player != null && player.isYou && !HeadlessConfig.Enabled;
    }

    public static void RaiseGameEnded(GameEndInfo info)
    {
        // Providers are told first: an in-flight decision has to be cancelled before the
        // scene starts tearing down, or a worker thread outlives the objects it is reading.
        DecisionBridge.TeardownForGame(info.WinnerPlayerId,
            info.Surrendered ? "surrender"
                : (string.IsNullOrEmpty(info.EffectName) ? "rules" : info.EffectName));

        GameEnded?.Invoke(info);
    }

    public struct GameEndInfo
    {
        public int WinnerPlayerId;
        public string WinnerName;
        public bool WinnerIsYou;
        public bool Surrendered;
        public string EffectName;
        public int TurnCount;
    }
}
