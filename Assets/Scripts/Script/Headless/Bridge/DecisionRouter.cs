using System;
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

    /// <summary>
    /// Decisions of a bridged seat that DCGO took itself without the provider's answer. Since
    /// praca-v1.2 subsets, "select nothing", the order of simultaneous effects and the
    /// mulligan are all routed, so what is left are paths outside the ST-1/ST-2 pool and
    /// selections the effect itself rejects (pick_rejected, incomplete_set, no_legal_pick).
    /// Unlike a decline, the provider was not the one that gave up, so these need a counter
    /// of their own. A clean run has zero.
    /// </summary>
    static readonly int[] _unrouted = new int[2];

    static readonly SortedDictionary<string, int>[] _unroutedByKind =
    {
        new SortedDictionary<string, int>(),
        new SortedDictionary<string, int>()
    };

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

    public static int UnroutedCount(int playerId)
    {
        return playerId >= 0 && playerId <= 1 ? _unrouted[playerId] : 0;
    }

    /// <summary>"kind:count;kind:count", sorted by kind; empty when nothing was counted.</summary>
    public static string UnroutedKinds(int playerId)
    {
        if (playerId < 0 || playerId > 1)
        {
            return "";
        }

        var parts = new List<string>();

        foreach (KeyValuePair<string, int> entry in _unroutedByKind[playerId])
        {
            parts.Add(entry.Key + ":" + entry.Value);
        }

        return string.Join(";", parts);
    }

    /// <summary>
    /// Records a decision of <paramref name="seat"/> that DCGO took without asking the seat's
    /// provider. Does nothing for a seat without a provider, so the ordinary game and the
    /// built-in bot are unaffected.
    /// </summary>
    public static void CountUnrouted(Player seat, string kind)
    {
        if (!HasProvider(seat))
        {
            return;
        }

        int id = seat.PlayerID;
        _unrouted[id]++;

        _unroutedByKind[id].TryGetValue(kind, out int count);
        _unroutedByKind[id][kind] = count + 1;

        // In the decision log too: a replay of this game in dcg_core has to stop here, because
        // DCGO decided on its own and may have drawn from the game's random sequence.
        DecisionLog.Unrouted(seat, kind);
    }

    public static void ResetCounters()
    {
        for (int i = 0; i < 2; i++)
        {
            _answered[i] = 0;
            _declined[i] = 0;
            _unrouted[i] = 0;
            _unroutedByKind[i].Clear();
        }
    }

    #endregion

    #region game lifecycle

    public static void NotifyGameStart(GameContext ctx)
    {
        ResetMainPhase();
        DecisionLog.BeginGame(ctx);

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
        DecisionLog.EndGame(winnerPlayerId, reason);

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
    static DecisionRequest _pendingRequest;
    static string _pendingState;

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
        _pendingRequest = null;
        _pendingState = null;
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

            _pendingRequest = new DecisionRequest
            {
                Kind = DecisionKind.MainPhase,
                Seat = player,
                Options = _pendingOptions,
                Context = "main_phase"
            };

            // The state is captured when the question is asked, not when the answer arrives:
            // the replay compares the model's state at the same point.
            _pendingState = DecisionLog.Enabled ? GameStateHash.Describe() : null;

            provider.BeginChoose(_pendingRequest);

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
            DecisionLog.Decision("declined", _pendingRequest, -1, _pendingState);
            return false;
        }

        MainPhaseAction action = _pendingOptions[index].ToMainPhaseAction();

        if (action == null)
        {
            _declined[player.PlayerID]++;
            _declinedThisDecision = true;
            DecisionLog.Decision("declined", _pendingRequest, -1, _pendingState);
            return false;
        }

        DecisionLog.Decision("decision", _pendingRequest, index, _pendingState);

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
    ///
    /// A single option is answered here without asking (praca-v1.2): one option that cannot
    /// be refused is not a decision, and dcg_core does not create a node for it either. The
    /// decision log still records it, as "forced", so a replay stays step-aligned.
    /// </summary>
    public static int Choose(DecisionKind kind, Player seat, List<DecisionOption> options, string context,
                             string sourceCardId = null, string effectName = null,
                             int pickedSoFar = 0, int maxPicks = 1)
    {
        IDecisionProvider provider = Get(seat);

        if (provider == null || options == null || options.Count == 0)
        {
            return -1;
        }

        var request = new DecisionRequest
        {
            Kind = kind,
            Seat = seat,
            Options = options,
            Context = context ?? "",
            SourceCardId = sourceCardId,
            EffectName = effectName,
            PickedSoFar = pickedSoFar,
            MaxPicks = maxPicks
        };

        string stateBefore = DecisionLog.Enabled ? GameStateHash.Describe() : null;

        if (options.Count == 1)
        {
            DecisionLog.Decision("forced", request, 0, stateBefore);
            return 0;
        }

        int index = provider.Choose(request);

        if (index < 0 || index >= options.Count)
        {
            _declined[seat.PlayerID]++;
            DecisionLog.Decision("declined", request, -1, stateBefore);
            return -1;
        }

        _answered[seat.PlayerID]++;
        DecisionLog.Decision("decision", request, index, stateBefore);
        return index;
    }

    /// <summary>Picks one integer out of a set. Returns false when the caller should decide itself.</summary>
    public static bool ChooseInt(Player seat, List<int> values, string context, out int chosen,
                                 string sourceCardId = null, string effectName = null)
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

        int index = Choose(DecisionKind.SelectInt, seat, options, context, sourceCardId, effectName);

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
                                  DecisionKind kind = DecisionKind.SelectBool,
                                  string sourceCardId = null, string effectName = null)
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

        int index = Choose(kind, seat, options, context, sourceCardId, effectName);

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
            DecisionOption option = CardOption(candidates[i]);
            option.CardIndex = i;
            options.Add(option);
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
            DecisionOption option = PermanentOption(candidates[i]);
            option.CardIndex = i;
            options.Add(option);
        }

        return Choose(DecisionKind.SelectPermanent, seat, options, context);
    }

    #endregion

    #region selections of several items or of none (praca-v1.2)

    /// <summary>
    /// A selection of up to <paramref name="maxCount"/> items, asked one pick at a time.
    ///
    /// This is how dcg_core models it too (ST1-15 Giga Destroyer is two successive single
    /// choices with a "done" option), so both engines ask the same questions in the same
    /// order. Each step offers the items not picked yet that the effect still accepts, plus:
    ///   - "decline" before the first pick, when the effect allows selecting nothing
    ///     (canNoSelect - e.g. "do not block");
    ///   - "end" after a pick, when the effect allows stopping early (canEndNotMax) and the
    ///     set picked so far is a legal end.
    /// A step with one option and no way out is answered without asking (see Choose).
    ///
    /// Returns false when the seat has no provider or the provider declined; the caller then
    /// keeps DCGO's original behaviour. On true, <paramref name="picked"/> may be empty,
    /// which means "select nothing".
    /// </summary>
    public static bool ChooseSequence<T>(Player seat, DecisionKind kind, string context,
                                         string sourceCardId, string effectName,
                                         List<T> candidates, int maxCount,
                                         bool canNoSelect, bool canEndNotMax,
                                         Func<List<T>, T, bool> canAddToPicked,
                                         Func<List<T>, bool> canEnd,
                                         Func<T, DecisionOption> describe,
                                         out List<T> picked) where T : class
    {
        picked = null;

        if (!HasProvider(seat) || candidates == null || candidates.Count == 0 || maxCount <= 0)
        {
            return false;
        }

        var chosen = new List<T>();
        bool stopped = false;

        while (chosen.Count < maxCount)
        {
            var remaining = new List<T>();

            foreach (T candidate in candidates)
            {
                if (candidate == null || chosen.Contains(candidate))
                {
                    continue;
                }

                if (canAddToPicked != null && !canAddToPicked(chosen, candidate))
                {
                    continue;
                }

                remaining.Add(candidate);
            }

            if (remaining.Count == 0)
            {
                break;
            }

            bool legalEnd = canEnd == null || canEnd(chosen);
            bool mayStop = chosen.Count == 0
                ? canNoSelect || (canEndNotMax && legalEnd)
                : canEndNotMax && legalEnd;

            var options = new List<DecisionOption>(remaining.Count + 1);

            for (int i = 0; i < remaining.Count; i++)
            {
                DecisionOption option = describe(remaining[i]);
                option.CardIndex = i;
                options.Add(option);
            }

            if (mayStop)
            {
                options.Add(new DecisionOption
                {
                    Kind = chosen.Count == 0 ? "decline" : "end",
                    Label = chosen.Count == 0 ? "select nothing" : "done",
                    OwnerPlayerId = seat.PlayerID
                });
            }

            int index = Choose(kind, seat, options, context, sourceCardId, effectName, chosen.Count, maxCount);

            if (index < 0)
            {
                return false;
            }

            if (index >= remaining.Count)
            {
                stopped = true;
                break;
            }

            chosen.Add(remaining[index]);
        }

        if (chosen.Count == 0 && !stopped && !canNoSelect)
        {
            // The effect accepts none of the candidates, yet selecting nothing is not allowed
            // either. Nothing sensible to ask; DCGO decides as before.
            CountUnrouted(seat, "no_legal_pick");
            return false;
        }

        if (chosen.Count > 0 && chosen.Count < maxCount && !canEndNotMax)
        {
            // Ran out of candidates before reaching an exact count the effect requires. DCGO's
            // own rule (CanEndSelect) rejects such a set, so DCGO decides as before.
            CountUnrouted(seat, "incomplete_set");
            return false;
        }

        if (chosen.Count > 0 && canEnd != null && !canEnd(chosen))
        {
            // Every pick was acceptable on its own but the set as a whole is not.
            CountUnrouted(seat, "pick_rejected");
            return false;
        }

        picked = chosen;
        return true;
    }

    /// <summary>Option describing a permanent, addressed the same way as in the observation.</summary>
    public static DecisionOption PermanentOption(Permanent permanent)
    {
        CardSource top = permanent != null ? permanent.TopCard : null;
        Player owner = top != null ? top.Owner : null;

        return new DecisionOption
        {
            Kind = "permanent",
            CardId = top != null ? top.CardID : "?",
            Label = top != null ? top.CardID : "?",
            OwnerPlayerId = owner != null ? owner.PlayerID : -1,
            FieldIndex = owner != null ? owner.GetFieldPermanents().IndexOf(permanent) : -1
        };
    }

    /// <summary>Option describing a card, with the zone it is in - the model needs to know which copy.</summary>
    public static DecisionOption CardOption(CardSource card)
    {
        Player owner = card != null ? card.Owner : null;

        var option = new DecisionOption
        {
            Kind = "card",
            CardId = card != null ? card.CardID : "?",
            Label = card != null ? card.CardID : "?",
            OwnerPlayerId = owner != null ? owner.PlayerID : -1
        };

        if (owner == null)
        {
            return option;
        }

        int index;

        if ((index = owner.HandCards.IndexOf(card)) >= 0)
        {
            option.Zone = "hand";
        }
        else if ((index = owner.TrashCards.IndexOf(card)) >= 0)
        {
            option.Zone = "trash";
        }
        else if ((index = owner.SecurityCards.IndexOf(card)) >= 0)
        {
            option.Zone = "security";
        }
        else if ((index = owner.LibraryCards.IndexOf(card)) >= 0)
        {
            option.Zone = "deck";
        }
        else if ((index = owner.DigitamaLibraryCards.IndexOf(card)) >= 0)
        {
            option.Zone = "egg_deck";
        }
        else
        {
            Permanent permanent = card.PermanentOfThisCard();

            if (permanent != null)
            {
                option.FieldIndex = owner.GetFieldPermanents().IndexOf(permanent);
                option.Zone = permanent.TopCard == card ? "field" : "stack";
                index = permanent.DigivolutionCards != null ? permanent.DigivolutionCards.IndexOf(card) : -1;
            }
            else
            {
                option.Zone = "other";
                index = -1;
            }
        }

        option.ZoneIndex = index;
        return option;
    }

    #endregion
}
