using UnityEngine;

/// <summary>
/// Turns the configuration into live providers, once per battle.
///
/// Kept apart from DecisionRouter so the router stays a pure lookup with no opinion about
/// where providers come from - which is what lets tests register a scripted provider
/// directly without going anywhere near the command line.
/// </summary>
public static class DecisionBridge
{
    static RemoteDecisionProvider _remote0;
    static RemoteDecisionProvider _remote1;

    /// <summary>
    /// Registers the configured providers. Safe to call for every battle: existing remote
    /// connections are reused, because reconnecting once per game would pay the agent's
    /// startup cost - card pool, checkpoint, torch - on every single game.
    /// </summary>
    public static void SetupForGame()
    {
        HeadlessConfig.EnsureParsed();

        if (!HeadlessConfig.BridgeActive)
        {
            DecisionRouter.Clear();
            return;
        }

        DecisionRouter.Register(0, Build(HeadlessConfig.Provider0, 0));
        DecisionRouter.Register(1, Build(HeadlessConfig.Provider1, 1));
    }

    /// <summary>Ends the battle for the providers, keeping their connections open.</summary>
    public static void TeardownForGame(int winnerPlayerId, string reason)
    {
        if (!DecisionRouter.AnyRegistered)
        {
            return;
        }

        DecisionRouter.NotifyGameEnd(winnerPlayerId, reason);

        int answered = DecisionRouter.AnsweredCount(0) + DecisionRouter.AnsweredCount(1);
        int declined = DecisionRouter.DeclinedCount(0) + DecisionRouter.DeclinedCount(1);

        if (declined > 0)
        {
            // Loud on purpose. A run where some decisions silently fell back to the built-in
            // bot is not a measurement of the agent, and the number belongs in the report
            // rather than in a log nobody reads.
            Debug.LogWarning($"[Bridge] game finished with {declined} declined decisions " +
                             $"out of {answered + declined} — results are NOT a clean agent run");
        }
        else
        {
            Debug.Log($"[Bridge] game finished, {answered} decisions answered by providers");
        }
    }

    /// <summary>Closes the sockets. Called when the process is shutting down.</summary>
    public static void Shutdown()
    {
        DecisionRouter.Clear();

        _remote0?.Close();
        _remote1?.Close();

        _remote0 = null;
        _remote1 = null;
    }

    static IDecisionProvider Build(string kind, int seat)
    {
        switch (kind)
        {
            case "random":
                return new RandomLegalProvider();

            case "random-isolated":
                return new RandomIsolatedProvider(seat);

            case "remote":
                if (seat == 0)
                {
                    _remote0 ??= new RemoteDecisionProvider(
                        HeadlessConfig.BridgeHost, HeadlessConfig.BridgePort0,
                        HeadlessConfig.BridgeTimeout, "remote-seat0");
                    return _remote0;
                }

                _remote1 ??= new RemoteDecisionProvider(
                    HeadlessConfig.BridgeHost, HeadlessConfig.BridgePort1,
                    HeadlessConfig.BridgeTimeout, "remote-seat1");
                return _remote1;

            default:
                return null;
        }
    }
}
