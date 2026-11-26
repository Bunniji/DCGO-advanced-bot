using System;
using System.Collections.Generic;

/// <summary>
/// Builds the JSON observation sent to a remote provider.
///
/// Written from the deciding seat's point of view and enforcing the hidden-information
/// split of the comprehensive rules:
///
///     field    [3-4-2]  public          -> full contents
///     trash    [3-6-3]  public          -> full contents
///     hand     [3-5-3]  owner only      -> contents for the viewer, count for the opponent
///     deck     [3-2-2]  private         -> count only, for BOTH players
///     egg deck [3-3-2]  private         -> count only
///     security [3-7-2]  private to all  -> count only, INCLUDING the viewer's own
///
/// The last one is the one that is easy to get wrong: a player does not know the contents
/// of their own security stack. An agent that saw it would be cheating, and the cheating
/// would not show up as an error anywhere - only as suspiciously good play. Keeping the
/// serializer as the single place where a zone can become visible is what makes that
/// mistake structurally hard rather than merely unlikely.
///
/// GameStateHash is the deliberate opposite of this class: it dumps everything, hidden
/// zones included, and is never shown to an agent.
/// </summary>
public static class ObservationSerializer
{
    /// <summary>Serialises the whole observable state for <paramref name="viewer"/>.</summary>
    public static string Build(TurnStateMachine tsm, Player viewer)
    {
        if (tsm == null || tsm.gameContext == null || viewer == null)
        {
            return "{}";
        }

        GameContext ctx = tsm.gameContext;
        Player opponent = ctx.PlayerFromID(viewer.PlayerID == 0 ? 1 : 0);

        var w = new BridgeJson.Writer();

        w.Num("viewer", viewer.PlayerID);
        w.Num("turn_player", ctx.TurnPlayer != null ? ctx.TurnPlayer.PlayerID : -1);
        w.Num("phase", (int)ctx.TurnPhase);
        w.Str("phase_name", ctx.TurnPhase.ToString());
        w.Num("turn", tsm.TurnCount);

        // Raw engine memory plus the viewer-relative value. dcg_core uses the same sign
        // convention as Player.MemoryForPlayer (positive = on player 1's side), so the raw
        // number transfers directly; memory_for is sent as well because every heuristic
        // wants it and recomputing it on the far side is one more place to get a sign wrong.
        w.Num("memory", ctx.Memory);
        w.Num("memory_for_viewer", viewer.MemoryForPlayer);

        w.Raw("me", SerializePlayer(viewer, isViewer: true));
        w.Raw("opponent", SerializePlayer(opponent, isViewer: false));

        return w.Close();
    }

    static string SerializePlayer(Player player, bool isViewer)
    {
        var w = new BridgeJson.Writer();

        if (player == null)
        {
            return w.Close();
        }

        w.Num("player_id", player.PlayerID);
        w.Str("name", player.PlayerName);

        // GetBattleAreaPermanents, NIE GetFieldPermanents: to drugie nie filtruje obszaru
        // wychowania (brak warunku isBattleAreaFrameID, ktory ma metoda bliźniacza), więc
        // Digimon z wychowania trafiał do obserwacji dwa razy i model widział go jako
        // permanent na polu. Wykryła to walidacja różnicowa D7.
        List<Permanent> addressing = player.GetFieldPermanents();

        w.Array("battle_area", Permanents(player.GetBattleAreaPermanents(), addressing));
        w.Array("breeding", Permanents(player.GetBreedingAreaPermanents(), addressing));

        w.StrArray("trash", CardIds(player.TrashCards));

        if (isViewer)
        {
            w.StrArray("hand", CardIds(player.HandCards));
        }

        w.Num("hand_count", Count(player.HandCards));
        w.Num("deck_count", Count(player.LibraryCards));
        w.Num("egg_deck_count", Count(player.DigitamaLibraryCards));
        w.Num("security_count", Count(player.SecurityCards));

        w.Num("memory", player.MemoryForPlayer);

        // Odpowiedzi predykatow samego emulatora, wystawione do walidacji roznicowej.
        //
        // Nie sa agentowi do niczego potrzebne - sa tu po to, zeby Python mogl je porownac
        // z wlasnym modelem przy KAZDEJ decyzji. Powod jest konkretny: rozbieznosc w
        // CanMove (regula 4-17-2) byla niewykrywalna przez porownywanie zapytan, bo gdy
        // ani wyklucie, ani przeniesienie nie jest legalne, emulator o faze wychowania w
        // ogole nie pyta. Brak pytania nie niesie informacji; odpowiedz predykatu niesie.
        w.Bool("can_hatch", SafeCanHatch(player));
        w.Bool("can_move", SafeCanMove(player));

        // Effects on the player that last until a turn ends (e.g. ST1-14). praca-v1.2.
        var effects = new List<string>();
        AddEffects(effects, player.UntilEachTurnEndEffects, "each_turn_end");
        AddEffects(effects, player.UntilOwnerTurnEndEffects, "owner_turn_end");
        AddEffects(effects, player.UntilOpponentTurnEndEffects, "opponent_turn_end");
        AddEffects(effects, player.UntilEndBattleEffects, "end_battle");
        AddEffects(effects, player.UntilOwnerActivePhaseEffects, "owner_active_phase");
        AddEffects(effects, player.UntilSecurityCheckEndEffects, "security_check_end");
        w.Array("effects", effects);

        return w.Close();
    }

    /// <summary>
    /// Serialises one zone. Emits BOTH index conventions, because DCGO uses two and
    /// conflating them silently targets the wrong permanent:
    ///
    ///   index         position within this zone - what the Python model uses
    ///   action_index  position in GetFieldPermanents() - what AttackPermanentAction and
    ///                 ActivatePermanentAction address, since that is the list
    ///                 SetAttackingPermaent and SetActSkill index into
    ///
    /// They coincide only while the breeding area is empty, which is exactly the kind of
    /// agreement that hides a bug until it matters.
    /// </summary>
    static IEnumerable<string> Permanents(List<Permanent> permanents, List<Permanent> addressing)
    {
        if (permanents == null)
        {
            yield break;
        }

        for (int i = 0; i < permanents.Count; i++)
        {
            Permanent permanent = permanents[i];

            if (permanent == null || permanent.TopCard == null)
            {
                continue;
            }

            var w = new BridgeJson.Writer();

            w.Num("index", i);
            w.Num("action_index", addressing != null ? addressing.IndexOf(permanent) : -1);
            w.Str("card_id", permanent.TopCard.CardID);

            // Bottom-up, top card last: the order carries the inherited effects [4-3-3],
            // so it is information, not presentation.
            var stack = new List<string>();
            if (permanent.DigivolutionCards != null)
            {
                foreach (CardSource card in permanent.DigivolutionCards)
                {
                    stack.Add(card != null ? card.CardID : "?");
                }
            }
            stack.Add(permanent.TopCard.CardID);
            w.StrArray("stack", stack);

            w.Num("frame", permanent.PermanentFrame != null ? permanent.PermanentFrame.FrameID : -1);
            w.Bool("suspended", permanent.IsSuspended);
            w.Num("dp", permanent.DP);
            w.Num("level", permanent.Level);

            // Summoning sickness lives inside CanAttackTargetDigimon, so the answers are sent
            // rather than the inputs: they are what a policy needs and are authoritative.
            w.Bool("can_attack", SafeCanAttack(permanent));
            w.Bool("can_declare_skill", SafeCanDeclareSkill(permanent));

            // praca-v1.2 (B-1): what the model needs to rebuild the permanent, not only its
            // final DP - the turn it entered the field (Permanent.EnterFieldTurnCount), and its
            // temporary effects (+3000 from ST1-13, "cannot attack / block" from ST2-14, ...)
            // with how long they last.
            w.Num("entered_turn", permanent.EnterFieldTurnCount);
            var effects = new List<string>();
            AddEffects(effects, permanent.UntilEachTurnEndEffects, "each_turn_end");
            AddEffects(effects, permanent.UntilOwnerTurnEndEffects, "owner_turn_end");
            AddEffects(effects, permanent.UntilOpponentTurnEndEffects, "opponent_turn_end");
            AddEffects(effects, permanent.UntilEndBattleEffects, "end_battle");
            AddEffects(effects, permanent.UntilEndAttackEffects, "end_attack");
            AddEffects(effects, permanent.UntilNextUntapEffects, "next_untap");
            AddEffects(effects, permanent.UntilOwnerDrawPhaseEffects, "owner_draw_phase");
            AddEffects(effects, permanent.UntilOwnerTurnStartEffects, "owner_turn_start");
            w.Array("effects", effects);

            yield return w.Close();
        }
    }

    /// <summary>
    /// One entry per temporary effect: duration (relative to the permanent's owner, as DCGO
    /// stores it), effect class, source card and effect name. The amount (e.g. +3000) is not
    /// read from the effect object: dcg_core knows every effect of the pool by its source card,
    /// so the source and class identify the modifier.
    /// </summary>
    static void AddEffects(List<string> items, List<Func<EffectTiming, ICardEffect>> effects, string duration)
    {
        if (effects == null)
        {
            return;
        }

        foreach (Func<EffectTiming, ICardEffect> getEffect in effects)
        {
            ICardEffect effect = null;
            CardSource source = null;

            try
            {
                effect = getEffect != null ? getEffect(EffectTiming.None) : null;
                source = effect != null ? effect.EffectSourceCard : null;
            }
            catch
            {
                effect = null;
            }

            if (effect == null)
            {
                continue;
            }

            var w = new BridgeJson.Writer();
            w.Str("duration", duration);
            w.Str("type", effect.GetType().Name);
            w.Str("source", source != null ? source.CardID : "?");
            w.Str("name", effect.EffectName ?? "");
            items.Add(w.Close());
        }
    }

    static bool SafeCanHatch(Player player)
    {
        try { return player.CanHatch; }
        catch { return false; }
    }

    static bool SafeCanMove(Player player)
    {
        // Uwaga przy porownaniu: Player.CanMove wymaga TAKZE wolnej ramki w obszarze walki,
        // czego model nie sprawdza. Rozbieznosc przy pelnym polu jest wiec spodziewana i
        // walidator raportuje ja osobno.
        try { return player.CanMove; }
        catch { return false; }
    }

    static bool SafeCanAttack(Permanent permanent)
    {
        // Called while effects may be mid-resolution; a predicate that throws must not take
        // the whole observation with it.
        try { return permanent.CanAttack(null); }
        catch { return false; }
    }

    static bool SafeCanDeclareSkill(Permanent permanent)
    {
        try { return permanent.CanDeclareSkill(); }
        catch { return false; }
    }

    static IEnumerable<string> CardIds(List<CardSource> zone)
    {
        if (zone == null)
        {
            yield break;
        }

        foreach (CardSource card in zone)
        {
            yield return card != null ? card.CardID : "?";
        }
    }

    static int Count(List<CardSource> zone)
    {
        return zone != null ? zone.Count : 0;
    }
}
