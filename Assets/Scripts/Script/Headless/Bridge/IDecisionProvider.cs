using System.Collections.Generic;

/// <summary>
/// The kind of question being asked. Python answers all of them the same way - by index -
/// but it needs the kind to route the question to the right policy.
/// </summary>
public enum DecisionKind
{
    /// <summary>Main phase: play, digivolve, attack, activate an effect, or pass.</summary>
    MainPhase,

    /// <summary>Breeding phase: hatch / promote, or do nothing.</summary>
    Breeding,

    /// <summary>Pick cards out of a set (hand, trash, security, revealed cards).</summary>
    SelectCard,

    /// <summary>Pick a permanent on either field.</summary>
    SelectPermanent,

    /// <summary>Pick one of a set of integers (e.g. how much DP to add).</summary>
    SelectInt,

    /// <summary>Yes / no.</summary>
    SelectBool,

    /// <summary>Pick a count (e.g. how many cards to trash).</summary>
    SelectCount,

    /// <summary>Pick which of several simultaneous effects resolves first.</summary>
    SelectSkill,

    /// <summary>Keep or mulligan the opening hand.</summary>
    Mulligan
}

/// <summary>
/// One answerable option.
///
/// Deliberately a single flat type carrying every field any decision kind might need,
/// rather than a class hierarchy. The whole point of the bridge is that every decision
/// point in DCGO collapses to "choose one index out of N" - a hierarchy would reintroduce
/// per-decision-point special cases on both sides of the socket, which is exactly the
/// complexity this design exists to avoid. Unused fields stay at their sentinel value and
/// are simply not serialised.
/// </summary>
public sealed class DecisionOption
{
    public const int None = -1;

    /// <summary>"play", "digivolve", "attack", "activate_permanent", "activate_card", "pass", "value", "card", "permanent".</summary>
    public string Kind = "value";

    /// <summary>Catalog identity ("ST1-03"). This is what dcg_core matches on, not CardIndex.</summary>
    public string CardId;

    /// <summary>Human-readable form; only ever used for logs and for the thesis's traces.</summary>
    public string Label;

    /// <summary>Payload for int/bool/count decisions.</summary>
    public int Value = None;

    /// <summary>Index into GameContext.ActiveCardList - DCGO's per-game instance id.</summary>
    public int CardIndex = None;

    /// <summary>Frame the card is played onto. Occupied frame = digivolution.</summary>
    public int TargetFrameId = None;

    /// <summary>Index into the card's or permanent's declarable-effect list.</summary>
    public int SkillIndex = None;

    /// <summary>Index into the turn player's field permanents.</summary>
    public int AttackerIndex = None;

    /// <summary>Index into the non-turn player's field permanents; None means attack security.</summary>
    public int DefenderIndex = None;

    /// <summary>Owner of the card or permanent this option refers to.</summary>
    public int OwnerPlayerId = None;

    /// <summary>
    /// Builds the engine-level action. Only meaningful for DecisionKind.MainPhase; the
    /// other kinds are answered by writing an IPlayerSelection into the player's queue.
    /// </summary>
    public MainPhaseAction ToMainPhaseAction()
    {
        switch (Kind)
        {
            case "play":
            case "digivolve":
                return new PlayCardAction(CardIndex, TargetFrameId, null, None, null);

            case "attack":
                return new AttackPermanentAction(AttackerIndex, DefenderIndex);

            case "activate_permanent":
                return new ActivatePermanentAction(AttackerIndex, SkillIndex);

            case "activate_card":
                return new ActivateCardAction(CardIndex, SkillIndex);

            case "pass":
                return new PassAction();

            default:
                return null;
        }
    }

    public string ToJson()
    {
        var w = new BridgeJson.Writer();
        w.Str("kind", Kind);

        if (CardId != null) w.Str("card_id", CardId);
        if (Label != null) w.Str("label", Label);
        if (CardIndex != None) w.Num("card_index", CardIndex);
        if (TargetFrameId != None) w.Num("frame", TargetFrameId);
        if (SkillIndex != None) w.Num("skill", SkillIndex);
        if (AttackerIndex != None) w.Num("attacker", AttackerIndex);
        if (OwnerPlayerId != None) w.Num("owner", OwnerPlayerId);

        // These two are written unconditionally for the kinds that own them, because -1 is a
        // legitimate payload there - "attack the security stack", or a count of -1 - and the
        // sentinel test used for the other fields would silently drop it.
        if (string.Equals(Kind, "attack")) w.Num("defender", DefenderIndex);
        if (string.Equals(Kind, "value")) w.Num("value", Value);

        return w.Close();
    }
}

/// <summary>One question put to a provider.</summary>
public sealed class DecisionRequest
{
    public DecisionKind Kind;

    /// <summary>The seat that has to answer. Not necessarily the turn player.</summary>
    public Player Seat;

    public List<DecisionOption> Options = new List<DecisionOption>();

    /// <summary>Free-form marker of where in the rules this question came from, for traces.</summary>
    public string Context = "";

    /// <summary>How many options must be picked. Only SelectCard ever asks for more than one.</summary>
    public int PickCount = 1;
}

/// <summary>
/// Where a seat's decisions come from.
///
/// Two shapes on purpose:
///
///   Choose      - synchronous, for the cheap decisions buried inside effect coroutines.
///                 Those call sites are not coroutines we can suspend without rewriting
///                 the effect scripts, which would break rule R2 (additive changes only).
///
///   Begin/Poll  - asynchronous, for the main-phase decision. That is where the search
///                 budget goes (hundreds of milliseconds), and it is the one call site
///                 that already polls once per frame, so it costs nothing to make async.
/// </summary>
public interface IDecisionProvider
{
    string Name { get; }

    void OnGameStart(int seatPlayerId, string seatName);

    void OnGameEnd(int winnerPlayerId, string reason);

    /// <summary>
    /// Answers immediately. Returns an index into request.Options, or -1 to decline,
    /// in which case the caller falls back to DCGO's own behaviour.
    /// </summary>
    int Choose(DecisionRequest request);

    /// <summary>Starts working on a decision. Must not block.</summary>
    void BeginChoose(DecisionRequest request);

    /// <summary>True once an answer is ready. -1 means "declined, use the built-in path".</summary>
    bool TryGetChoice(out int index);

    /// <summary>Abandons an in-flight decision (game ended, watchdog fired).</summary>
    void CancelChoose();
}
