using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// One JSON line per event of a game played through the decision bridge: the start, every
/// decision a provider took (or that was forced, or declined), every decision DCGO took on
/// its own, and the end.
///
/// Purpose: the transition test (praca-v1.2). dcg_core replays the game from the same seed,
/// feeding it the logged choices, and compares its own state with "state" - the canonical
/// description from GameStateHash, taken BEFORE each decision - step by step. The first
/// mismatch points at the action whose resolution differs between the two engines.
///
/// "state" contains hidden information (deck order, security). The log is a verification
/// tool and must never be shown to an agent.
///
/// Off unless -decision-log is given. Writing does not touch GameRandom, so a logged game
/// plays exactly like an unlogged one.
/// </summary>
public static class DecisionLog
{
    static StreamWriter _writer;
    static string _openPath;
    static int _game = -1;
    static long _seed;
    static int _step;

    public static bool Enabled
    {
        get { return !string.IsNullOrEmpty(HeadlessConfig.DecisionLogPath); }
    }

    public static void BeginGame(GameContext ctx)
    {
        if (!Enabled)
        {
            return;
        }

        _game++;
        _seed = HeadlessConfig.CurrentSeed;
        _step = 0;

        var w = Header("start");
        w.Str("deck0", HeadlessConfig.Deck0Code ?? "");
        w.Str("deck1", HeadlessConfig.Deck1Code ?? "");
        // No "first" field: the first player is set after this point (StartGame), and every
        // "state" carries the turn player (|P) and the turn number (T) anyway.
        w.Str("provider0", HeadlessConfig.Provider0);
        w.Str("provider1", HeadlessConfig.Provider1);
        w.Str("state", GameStateHash.Describe());
        Write(w.Close());
    }

    /// <summary><paramref name="evt"/>: "decision", "forced" or "declined" (chosen = -1).</summary>
    public static void Decision(string evt, DecisionRequest request, int chosen, string stateBefore)
    {
        if (!Enabled || request == null)
        {
            return;
        }

        var options = new List<string>(request.Options != null ? request.Options.Count : 0);

        if (request.Options != null)
        {
            foreach (DecisionOption option in request.Options)
            {
                options.Add(option.ToJson());
            }
        }

        var w = Header(evt);
        w.Num("step", _step++);
        w.Num("seat", request.Seat != null ? request.Seat.PlayerID : -1);
        w.Str("kind", request.Kind.ToString());
        w.Str("context", request.Context ?? "");
        if (request.SourceCardId != null) w.Str("source", request.SourceCardId);
        if (request.EffectName != null) w.Str("effect", request.EffectName);
        if (request.MaxPicks > 1)
        {
            w.Num("picked_so_far", request.PickedSoFar);
            w.Num("max_picks", request.MaxPicks);
        }
        w.Array("options", options);
        w.Num("chosen", chosen);
        w.Str("state", stateBefore ?? GameStateHash.Describe());
        Write(w.Close());
    }

    /// <summary>A decision of a bridged seat that DCGO took without asking (DecisionRouter.CountUnrouted).</summary>
    public static void Unrouted(Player seat, string kind)
    {
        if (!Enabled)
        {
            return;
        }

        var w = Header("unrouted");
        w.Num("step", _step++);
        w.Num("seat", seat != null ? seat.PlayerID : -1);
        w.Str("kind", kind ?? "");
        w.Str("state", GameStateHash.Describe());
        Write(w.Close());
    }

    public static void EndGame(int winnerPlayerId, string reason)
    {
        if (!Enabled)
        {
            return;
        }

        var w = Header("end");
        w.Num("winner", winnerPlayerId);
        w.Str("reason", reason ?? "");
        int turns = GManager.instance != null && GManager.instance.turnStateMachine != null
            ? GManager.instance.turnStateMachine.TurnCount : -1;
        w.Num("turns", turns);
        w.Str("state", GameStateHash.Describe());
        Write(w.Close());
    }

    static BridgeJson.Writer Header(string evt)
    {
        var w = new BridgeJson.Writer();
        w.Num("game", _game);
        w.Num("seed", _seed);
        w.Str("event", evt);
        return w;
    }

    static void Write(string line)
    {
        try
        {
            string path = HeadlessConfig.DecisionLogPath;

            if (_writer == null || _openPath != path)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(path, append: true, encoding: new UTF8Encoding(false)) { AutoFlush = true };
                _openPath = path;
            }

            _writer.WriteLine(line);
        }
        catch (Exception e)
        {
            // A broken log must not end the game; the missing lines show up in the replay.
            Debug.LogWarning($"[DecisionLog] write failed: {e.Message}");
        }
    }
}
