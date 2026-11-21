using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The single seam between DCGO's decision points and whatever is answering them.
///
/// Every call site inside the emulator's own scripts is one static call to this class,
/// guarded so that a seat with no provider behaves exactly as it did before. That is what
/// keeps risk R2 under control: the diff against upstream stays reviewable, the GUI path is
/// untouched, and the built-in bot - which is baseline B1 and must not change after it has
/// been measured - keeps running its original code.
///
/// Seats are addressed by PlayerID (0 / 1), never by isYou. The local seat is the human one
/// during evaluation against players, and the agent's one in batch mode; routing by isYou
/// would tie the agent to a seat for no reason and would silently swap sides between the two
/// modes.
/// </summary>
public static class DecisionRouter
{
    static readonly IDecisionProvider[] _providers = new IDecisionProvider[2];

    /// <summary>Decisions answered by a provider, per seat. Reported at the end of a run.</summary>
    static readonly int[] _answered = new int[2];

    /// <summary>Decisions a provider declined, per seat - each one fell back to DCGO.</summary>
    static readonly int[] _declined = new int[2];

    #region registration

    public static void Register(int playerId, IDecisionProvider provider)
    {
        if (playerId < 0 || playerId > 1)
        {
            return;
        }

        _providers[playerId] = provider;

        if (provider != null)
        {
            Debug.Log($"[Bridge] seat {playerId} -> {provider.Name}");
        }
    }

    public static void Clear()
    {
        for (int i = 0; i < _providers.Length; i++)
        {
            _providers[i] = null;
        }

        ResetMainPhase();
    }

    public static bool AnyRegistered
    {
        get { return _providers[0] != null || _providers[1] != null; }
    }

    public static bool HasProvider(Player player)
    {
        return Get(player) != null;
    }

    public static IDecisionProvider Get(Player player)
    {
        if (player == null || player.PlayerID < 0 || player.PlayerID > 1)
        {
            return null;
        }

        return _providers[player.PlayerID];
    }

    public static int AnsweredCount(int playerId)
    {
        return playerId >= 0 && playerId <= 1 ? _answered[playerId] : 0;
    }

    public static int DeclinedCount(int playerId)
    {
        return playerId >= 0 && playerId <= 1 ? _declined[playerId] : 0;
    }

    public static void ResetCounters()
    {
        for (int i = 0; i < 2; i++)
        {
            _answered[i] = 0;
            _declined[i] = 0;
        }
    }

    #endregion

    #region game lifecycle

    public static void NotifyGameStart(GameContext ctx)
    {
        ResetMainPhase();

        for (int id = 0; id < 2; id++)
        {
            if (_providers[id] == null)
            {
                continue;
            }

            Player seat = ctx != null ? ctx.PlayerFromID(id) : null;
            _providers[id].OnGameStart(id, seat != null ? seat.PlayerName : "seat" + id);
        }
    }

    public static void NotifyGameEnd(int winnerPlayerId, string reason)
    {
        ResetMainPhase();

        for (int id = 0; id < 2; id++)
        {
            if (_providers[id] != null)
            {
                _providers[id].OnGameEnd(winnerPlayerId, reason);
            }
        }
    }

    #endregion

    #region main phase (asynchronous)

    static bool _requestSent;
    static bool _actionQueued;
    static bool _declinedThisDecision;
    static List<DecisionOption> _pendingOptions;

    /// <summary>
    /// Called once per outer main-phase iteration, from ResetMainPhaseParameter. Ends the
    /// current decision cycle so the next one issues a fresh request.
    /// </summary>
    public static void ResetMainPhase()
    {
        _requestSent = false;
        _actionQueued = false;
        _declinedThisDecision = false;
        _pendingOptions = null;
    }

    /// <summary>
    /// Drives the main-phase decision for <paramref name="player"/>.
    ///
    /// Returns true while the provider owns the decision - the caller must then leave the
    /// built-in bot alone. Returns false once the provider has declined, which hands the
    /// decision back to DCGO's own logic for this cycle.
    ///
    /// This is the one decision point that is asynchronous, because it is the one that costs
    /// real time (an ISMCTS search runs for hundreds of milliseconds). The enclosing loop in
    /// TurnStateMachine already yields once per frame while waiting for a choice, so polling
    /// costs nothing and the emulator keeps rendering while the agent thinks.
    /// </summary>
    public static bool PollMainPhase(TurnStateMachine tsm, Player player)
    {
        IDecisionProvider provider = Get(player);

        if (provider == null || _declinedThisDecision)
        {
            return false;
        }

        // Decision already made and queued, waiting for the state machine to consume it.
        //
        // Without this the provider is asked twice for every single action: the queue is
        // drained at the top of the next frame, so for one frame the router sees no request
        // in flight and starts another search. The second answer is discarded, the game
        // plays correctly - and the agent quietly pays double for every move. Measured on
        // the first bridged run: 104 requests for 61 actions.
        if (_actionQueued)
        {
            return true;
        }

        if (!_requestSent)
        {
            _pendingOptions = LegalActionGenerator.MainPhase(tsm);

            if (_pendingOptions.Count == 0)
            {
                // Liczone jako odmowa. Bez tego przypadek "generator nie zaproponowal nic"
                // bylby niewidoczny w statystykach: partia wygladalaby na czysty przebieg
                // agenta, choc ruch zagralby wbudowany bot. Licznik odmow jest jedynym
                // sygnalem, ze przebieg NIE jest pomiarem agenta - musi lapac kazda sciezke.
                _declined[player.PlayerID]++;
                _declinedThisDecision = true;
                return false;
            }

            provider.BeginChoose(new DecisionRequest
            {
                Kind = DecisionKind.MainPhase,
                Seat = player,
                Options = _pendingOptions,
                Context = "main_phase"
            });

            _requestSent = true;
            return true;
        }

        if (!provider.TryGetChoice(out int index))
        {
            return true; // still thinking
        }

        if (index < 0 || index >= _pendingOptions.Count)
        {
            _declined[player.PlayerID]++;
            _declinedThisDecision = true;
            return false;
        }

        MainPhaseAction action = _pendingOptions[index].ToMainPhaseAction();

        if (action == null)
        {
            _declined[player.PlayerID]++;
            _declinedThisDecision = true;
            return false;
        }

        // Goes through the queue rather than writing TurnStateMachine's fields directly.
        // The built-in bot takes the shortcut; using the queue means the agent travels the
        // same path as a networked human player, which is also the path the engine validates.
        player.QueueMainPhaseAction(action);
        _answered[player.PlayerID]++;
        _actionQueued = true;

        return true;
    }

    #endregion

    #region other decision points (synchronous)

    /// <summary>
    /// Asks the seat's provider to pick one option. Returns -1 when there is no provider or
    /// the provider declined, in which case the caller keeps its original behaviour.
    /// </summary>
    public static int Choose(DecisionKind kind, Player seat, List<DecisionOption> options, string context)
    {
        IDecisionProvider provider = Get(seat);

        if (provider == null || options == null || options.Count == 0)
        {
            return -1;
        }

        int index = provider.Choose(new DecisionRequest
        {
            Kind = kind,
            Seat = seat,
            Options = options,
            Context = context ?? ""
        });

        if (index < 0 || index >= options.Count)
        {
            _declined[seat.PlayerID]++;
            return -1;
        }

        _answered[seat.PlayerID]++;
        return index;
    }

    /// <summary>Picks one integer out of a set. Returns false when the caller should decide itself.</summary>
    public static bool ChooseInt(Player seat, List<int> values, string context, out int chosen)
    {
        chosen = 0;

        if (!HasProvider(seat) || values == null || values.Count == 0)
        {
            return false;
        }

        var options = new List<DecisionOption>(values.Count);

        foreach (int value in values)
        {
            options.Add(new DecisionOption
            {
                Kind = "value",
                Value = value,
                Label = value.ToString(),
                OwnerPlayerId = seat.PlayerID
            });
        }

        int index = Choose(DecisionKind.SelectInt, seat, options, context);

        if (index < 0)
        {
            return false;
        }

        chosen = values[index];
        return true;
    }

    /// <summary>
    /// Picks yes or no out of the offered values.
    ///
    /// <paramref name="kind"/> defaults to SelectBool but must be overridden where the
    /// question is a phase decision rather than an effect prompt. The engine asks both with
    /// the same one-bit call, but they are not the same question: the breeding choice is a
    /// real strategic decision the agent should search, while an effect prompt is usually
    /// forced. A provider that cannot tell them apart answers both with the same fallback -
    /// which is exactly what happened on the first bridged run.
    /// </summary>
    public static bool ChooseBool(Player seat, List<bool> values, string context, out bool chosen,
                                  DecisionKind kind = DecisionKind.SelectBool)
    {
        chosen = false;

        if (!HasProvider(seat) || values == null || values.Count == 0)
        {
            return false;
        }

        var options = new List<DecisionOption>(values.Count);

        foreach (bool value in values)
        {
            options.Add(new DecisionOption
            {
                Kind = "value",
                Value = value ? 1 : 0,
                Label = value ? "yes" : "no",
                OwnerPlayerId = seat.PlayerID
            });
        }

        int index = Choose(kind, seat, options, context);

        if (index < 0)
        {
            return false;
        }

        chosen = values[index];
        return true;
    }

    /// <summary>Picks one card out of a candidate list. Returns -1 to leave the choice to DCGO.</summary>
    public static int ChooseCard(Player seat, List<CardSource> candidates, string context)
    {
        if (!HasProvider(seat) || candidates == null || candidates.Count == 0)
        {
            return -1;
        }

        var options = new List<DecisionOption>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            CardSource card = candidates[i];

            options.Add(new DecisionOption
            {
                Kind = "card",
                CardId = card != null ? card.CardID : "?",
                CardIndex = i,
                OwnerPlayerId = card != null && card.Owner != null ? card.Owner.PlayerID : -1,
                Label = card != null ? card.CardID : "?"
            });
        }

        return Choose(DecisionKind.SelectCard, seat, options, context);
    }

    /// <summary>Picks one permanent out of a candidate list.</summary>
    public static int ChoosePermanent(Player seat, List<Permanent> candidates, string context)
    {
        if (!HasProvider(seat) || candidates == null || candidates.Count == 0)
        {
            return -1;
        }

        var options = new List<DecisionOption>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            Permanent permanent = candidates[i];
            CardSource top = permanent != null ? permanent.TopCard : null;

            options.Add(new DecisionOption
            {
                Kind = "permanent",
                CardId = top != null ? top.CardID : "?",
                CardIndex = i,
                OwnerPlayerId = top != null && top.Owner != null ? top.Owner.PlayerID : -1,
                Label = top != null ? top.CardID : "?"
            });
        }

        return Choose(DecisionKind.SelectPermanent, seat, options, context);
    }

    #endregion
}
