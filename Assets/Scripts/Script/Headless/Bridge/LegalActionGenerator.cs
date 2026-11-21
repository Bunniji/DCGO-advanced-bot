using System.Collections.Generic;

/// <summary>
/// Enumerates the main-phase actions the engine will actually accept.
///
/// Design note. The remote agent never constructs a DCGO action; it only picks an index
/// out of this list. That removes an entire class of failure - an agent cannot send an
/// illegal play, because illegal plays are never offered - and it means the legality rules
/// stay in exactly one place: DCGO's own predicates, reused here rather than reimplemented.
///
/// The predicates below are the same ones the built-in bot and the UI consult
/// (CanPlayFromHandDuringMainPhase, CanPlayCardTargetFrame, CanAttack,
/// CanAttackTargetDigimon, CanDeclareSkill). What is deliberately NOT copied from the bot
/// is its `AttackingPermanent.DP >= permanent.DP` filter: that is the bot's taste in
/// attacks, not a rule, and inheriting it would silently hide legal moves from the agent.
/// </summary>
public static class LegalActionGenerator
{
    /// <summary>
    /// Every legal main-phase action for the turn player, always including Pass last.
    ///
    /// Ordering is deterministic (hand order, then frame order, then field order), which
    /// matters: the action list is part of the request the agent answers by index, so an
    /// unstable order would make traces unreplayable.
    /// </summary>
    public static List<DecisionOption> MainPhase(TurnStateMachine tsm)
    {
        var options = new List<DecisionOption>();

        if (tsm == null || tsm.gameContext == null || tsm.gameContext.TurnPlayer == null)
        {
            return options;
        }

        GameContext ctx = tsm.gameContext;
        Player player = ctx.TurnPlayer;

        AddPlayAndDigivolve(options, ctx, player);
        AddAttacks(options, ctx, player);
        AddActivations(options, ctx, player);

        options.Add(new DecisionOption
        {
            Kind = "pass",
            Label = "Pass",
            OwnerPlayerId = player.PlayerID
        });

        return options;
    }

    #region play / digivolve

    static void AddPlayAndDigivolve(List<DecisionOption> options, GameContext ctx, Player player)
    {
        if (player.HandCards == null)
        {
            return;
        }

        foreach (CardSource card in player.HandCards)
        {
            if (card == null || !card.CanPlayFromHandDuringMainPhase)
            {
                continue;
            }

            int cardIndex = ctx.ActiveCardList.IndexOf(card);

            if (cardIndex < 0)
            {
                // SetPlayCard resolves the card through ActiveCardList, so a card that is
                // not in it cannot be addressed at all.
                continue;
            }

            if (!card.IsPermanent)
            {
                // Options resolve without a frame; the bot leaves TargetFrameID untouched.
                options.Add(new DecisionOption
                {
                    Kind = "play",
                    CardId = card.CardID,
                    CardIndex = cardIndex,
                    OwnerPlayerId = player.PlayerID,
                    Label = "Play " + card.CardID
                });
                continue;
            }

            bool emptyFrameOffered = false;

            for (int i = 0; i < player.fieldCardFrames.Count; i++)
            {
                FieldCardFrame frame = player.fieldCardFrames[i];

                if (!card.CanPlayCardTargetFrame(frame, true, null))
                {
                    continue;
                }

                if (frame.IsEmptyFrame())
                {
                    // Every empty frame is the same move. DCGO itself canonicalises this
                    // (TurnStateMachine rewrites a chosen empty frame to PreferredFrame),
                    // so offering one option per empty slot would inflate the branching
                    // factor with duplicates and distort the search statistics.
                    if (emptyFrameOffered)
                    {
                        continue;
                    }

                    emptyFrameOffered = true;

                    FieldCardFrame preferred = card.PreferredFrame();
                    int frameId = preferred != null ? preferred.FrameID : frame.FrameID;

                    options.Add(new DecisionOption
                    {
                        Kind = "play",
                        CardId = card.CardID,
                        CardIndex = cardIndex,
                        TargetFrameId = frameId,
                        OwnerPlayerId = player.PlayerID,
                        Label = "Play " + card.CardID
                    });
                    continue;
                }

                // Occupied frame: this is a digivolution onto whatever stands there. The
                // distinction matters to the agent (different cost, different result), so
                // it gets its own kind even though the engine action is the same.
                Permanent target = frame.GetFramePermanent();
                string targetId = target != null && target.TopCard != null ? target.TopCard.CardID : "?";

                options.Add(new DecisionOption
                {
                    Kind = "digivolve",
                    CardId = card.CardID,
                    CardIndex = cardIndex,
                    TargetFrameId = frame.FrameID,
                    OwnerPlayerId = player.PlayerID,
                    Label = "Digivolve " + targetId + " -> " + card.CardID
                });
            }
        }
    }

    #endregion

    #region attacks

    static void AddAttacks(List<DecisionOption> options, GameContext ctx, Player player)
    {
        List<Permanent> field = player.GetFieldPermanents();
        List<Permanent> enemyField = ctx.NonTurnPlayer != null
            ? ctx.NonTurnPlayer.GetFieldPermanents()
            : new List<Permanent>();

        for (int a = 0; a < field.Count; a++)
        {
            Permanent attacker = field[a];

            if (attacker == null || !attacker.CanAttack(null))
            {
                continue;
            }

            string attackerId = attacker.TopCard != null ? attacker.TopCard.CardID : "?";

            // Security attack. CanAttackTargetDigimon(null, ...) is DCGO's own way of asking
            // "may this permanent attack the player rather than a Digimon".
            if (attacker.CanAttackTargetDigimon(null, null))
            {
                options.Add(new DecisionOption
                {
                    Kind = "attack",
                    CardId = attackerId,
                    AttackerIndex = a,
                    DefenderIndex = -1,
                    OwnerPlayerId = player.PlayerID,
                    Label = attackerId + " -> security"
                });
            }

            for (int d = 0; d < enemyField.Count; d++)
            {
                Permanent defender = enemyField[d];

                if (defender == null || !attacker.CanAttackTargetDigimon(defender, null))
                {
                    continue;
                }

                string defenderId = defender.TopCard != null ? defender.TopCard.CardID : "?";

                options.Add(new DecisionOption
                {
                    Kind = "attack",
                    CardId = attackerId,
                    AttackerIndex = a,
                    DefenderIndex = d,
                    OwnerPlayerId = player.PlayerID,
                    Label = attackerId + " -> " + defenderId
                });
            }
        }
    }

    #endregion

    #region activated effects

    static void AddActivations(List<DecisionOption> options, GameContext ctx, Player player)
    {
        AddPermanentActivations(options, player);
        AddCardActivations(options, ctx, player.HandCards, player, "hand");
        AddCardActivations(options, ctx, player.TrashCards, player, "trash");
    }

    static void AddPermanentActivations(List<DecisionOption> options, Player player)
    {
        List<Permanent> field = player.GetFieldPermanents();

        for (int p = 0; p < field.Count; p++)
        {
            Permanent permanent = field[p];

            if (permanent == null || permanent.TopCard == null || !permanent.CanDeclareSkill())
            {
                continue;
            }

            // Index convention, and it is NOT the same as for cards: SetActSkill indexes the
            // UNFILTERED EffectList(OnDeclaration), while SetActCardSkill indexes the FILTERED
            // CanDeclareSkillList. Using the filtered index here would activate the wrong
            // effect whenever an earlier effect happened to be unusable.
            List<ICardEffect> all = permanent.EffectList(EffectTiming.OnDeclaration);

            for (int s = 0; s < all.Count; s++)
            {
                ICardEffect effect = all[s];

                if (!(effect is ActivateICardEffect) || !effect.CanUse(null))
                {
                    continue;
                }

                options.Add(new DecisionOption
                {
                    Kind = "activate_permanent",
                    CardId = permanent.TopCard.CardID,
                    AttackerIndex = p,
                    SkillIndex = s,
                    OwnerPlayerId = player.PlayerID,
                    Label = "Activate " + permanent.TopCard.CardID + " #" + s
                });
            }
        }
    }

    static void AddCardActivations(List<DecisionOption> options, GameContext ctx,
                                   List<CardSource> zone, Player player, string zoneName)
    {
        if (zone == null)
        {
            return;
        }

        foreach (CardSource card in zone)
        {
            if (card == null || !card.CanDeclareSkill)
            {
                continue;
            }

            int cardIndex = ctx.ActiveCardList.IndexOf(card);

            if (cardIndex < 0)
            {
                continue;
            }

            // Filtered list here - see the comment in AddPermanentActivations.
            List<ICardEffect> declarable = card.CanDeclareSkillList;

            for (int s = 0; s < declarable.Count; s++)
            {
                options.Add(new DecisionOption
                {
                    Kind = "activate_card",
                    CardId = card.CardID,
                    CardIndex = cardIndex,
                    SkillIndex = s,
                    OwnerPlayerId = player.PlayerID,
                    Label = "Activate " + card.CardID + " (" + zoneName + ") #" + s
                });
            }
        }
    }

    #endregion
}
