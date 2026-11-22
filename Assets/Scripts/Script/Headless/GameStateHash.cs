using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Canonical fingerprint of the game state: zones, permanents, phase and memory. Effect
/// durations and once-per-turn flags are not covered, so equal hashes are necessary but
/// not sufficient for two identical runs.
///
/// Two purposes:
///   1. Reproducibility - the same seed replayed must produce the same hash at every step.
///   2. Differential validation (D7) - the Python model is compared against the emulator by
///      replaying the same action sequence and diffing these fingerprints step by step.
///
/// It deliberately covers hidden information too (deck order, security contents). This is a
/// debugging and verification tool, never an agent observation: an agent that saw this would
/// be cheating.
/// </summary>
public static class GameStateHash
{
    /// <summary>16 hex characters, FNV-1a 64-bit over the canonical description.</summary>
    public static string Compute()
    {
        return Hash(Describe());
    }

    /// <summary>
    /// Human-readable canonical form. When two runs disagree, diffing this pinpoints the
    /// divergence far faster than comparing hashes ever could.
    /// </summary>
    public static string Describe()
    {
        GManager gm = GManager.instance;

        if (gm == null || gm.turnStateMachine == null || gm.turnStateMachine.gameContext == null)
        {
            return "<no-game>";
        }

        TurnStateMachine tsm = gm.turnStateMachine;
        GameContext ctx = tsm.gameContext;

        var sb = new StringBuilder(512);

        sb.Append("T").Append(tsm.TurnCount);
        sb.Append("|F").Append((int)ctx.TurnPhase);
        sb.Append("|M").Append(ctx.Memory);
        sb.Append("|P").Append(ctx.TurnPlayer != null ? ctx.TurnPlayer.PlayerID : -1);
        sb.Append("|E").Append(tsm.endGame ? 1 : 0);

        // Ordered by PlayerID so the fingerprint does not depend on which seat is local.
        foreach (Player player in OrderedPlayers(ctx))
        {
            AppendPlayer(sb, player);
        }

        return sb.ToString();
    }

    static IEnumerable<Player> OrderedPlayers(GameContext ctx)
    {
        Player a = ctx.PlayerFromID(0);
        Player b = ctx.PlayerFromID(1);

        if (a != null) yield return a;
        if (b != null) yield return b;
    }

    static void AppendPlayer(StringBuilder sb, Player player)
    {
        sb.Append("\n#").Append(player.PlayerID);

        AppendZone(sb, "H", player.HandCards);
        AppendZone(sb, "L", player.LibraryCards);
        AppendZone(sb, "G", player.DigitamaLibraryCards);
        AppendZone(sb, "S", player.SecurityCards);
        AppendZone(sb, "R", player.TrashCards);

        AppendPermanents(sb, "B", player.GetFieldPermanents());
        AppendPermanents(sb, "N", player.GetBreedingAreaPermanents());
    }

    static void AppendZone(StringBuilder sb, string tag, List<CardSource> zone)
    {
        sb.Append(';').Append(tag).Append(':');

        if (zone == null)
        {
            return;
        }

        for (int i = 0; i < zone.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(CardKey(zone[i]));
        }
    }

    static void AppendPermanents(StringBuilder sb, string tag, List<Permanent> permanents)
    {
        sb.Append(';').Append(tag).Append(':');

        if (permanents == null)
        {
            return;
        }

        for (int p = 0; p < permanents.Count; p++)
        {
            Permanent permanent = permanents[p];

            if (p > 0)
            {
                sb.Append('/');
            }

            if (permanent == null)
            {
                sb.Append('-');
                continue;
            }

            sb.Append('[');

            // Digivolution stack bottom-up, then the top card: the order carries the inherited
            // effects, so it has to be part of the fingerprint.
            List<CardSource> stack = permanent.DigivolutionCards;

            if (stack != null)
            {
                foreach (CardSource card in stack)
                {
                    sb.Append(CardKey(card)).Append('.');
                }
            }

            sb.Append(CardKey(permanent.TopCard));

            sb.Append(permanent.IsSuspended ? "|s" : "|u");
            sb.Append('|').Append(permanent.DP.ToString(CultureInfo.InvariantCulture));
            sb.Append(']');
        }
    }

    /// <summary>
    /// Identity of the CARD, not of the object.
    ///
    /// CardSource.CardIndex is a per-game running instance id, so it fingerprints object
    /// identity: fine for "same seed twice in this engine", useless for comparing two
    /// engines. CardID ("ST1-03") is the catalog identity and is what dcg_core can match.
    /// </summary>
    static string CardKey(CardSource card)
    {
        if (card == null)
        {
            return "-";
        }

        try
        {
            string id = card.CardID;
            return string.IsNullOrEmpty(id) ? "?" : id;
        }
        catch
        {
            // Karta bez przypisanej encji - zdarza sie przy tokenach i w trakcie tworzenia.
            return "?";
        }
    }

    /// <summary>FNV-1a 64-bit. Not cryptographic - this only needs to be stable and cheap.</summary>
    static string Hash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;

        foreach (char c in value)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= prime;
            hash ^= (byte)(c >> 8);
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
