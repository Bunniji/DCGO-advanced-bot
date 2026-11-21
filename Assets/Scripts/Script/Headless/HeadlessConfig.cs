using System;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Configuration for the batch (headless) mode used by the research harness.
///
/// Everything here is inert unless the process was started with -dcgo-headless,
/// so a normal build behaves exactly as before.
///
/// Command line:
///   DCGO.exe -batchmode -nographics -dcgo-headless
///            -seed 12345 -games 10
///            -deck0 &lt;deckCode&gt; -deck1 &lt;deckCode&gt;
///            -out results.jsonl -timescale 20 -timeout 300
/// </summary>
public static class HeadlessConfig
{
    /// <summary>
    /// Highest time acceleration that still reproduces the game exactly. Measured, see
    /// <see cref="TimeScale"/>. Above this the emulator soft-locks.
    /// </summary>
    public const float MaxSafeTimeScale = 8f;

    /// <summary>True when the process was started with -dcgo-headless.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Seed for the first game; game N uses BaseSeed + N.</summary>
    public static long BaseSeed { get; private set; } = 1L;

    /// <summary>Number of games to play before quitting.</summary>
    public static int Games { get; private set; } = 10;

    /// <summary>Deck code for player 0 (the "You" side).</summary>
    public static string Deck0Code { get; private set; } = "";

    /// <summary>Deck code for player 1 (the bot side). Empty = keep DCGO's random pick.</summary>
    public static string Deck1Code { get; private set; } = "";

    /// <summary>Where match results are appended, as one JSON object per line.</summary>
    public static string OutputPath { get; private set; } = "headless-results.jsonl";

    /// <summary>
    /// Multiplier applied to Time.timeScale, which shortens every WaitForSeconds.
    ///
    /// 8 is a measured ceiling, not a guess. A sweep with -repeat-seed (5 games each,
    /// ST-1 vs ST-2) gave an identical state hash at 1, 4 and 8, but at 16 three games out
    /// of five soft-locked on turn 8 in the same state and had to be killed by the watchdog.
    /// The previous default of 20 was therefore above the failure threshold: any batch run
    /// that did not pass -timescale explicitly would have hung.
    ///
    /// Raising this further is also pointless. Fitting time(ts) = fixed + scalable/ts to the
    /// sweep gives 40.5 s per game that timeScale cannot touch - scene load/unload and the
    /// coroutines that yield once per frame - against 76.7 s that it can. At 8 roughly 93%
    /// of the available gain is already taken. Faster runs need parallel instances or a
    /// standalone build, not a bigger multiplier.
    /// </summary>
    public static float TimeScale { get; private set; } = 8f;

    /// <summary>Per-game watchdog. The built-in bot is known to soft-lock, so games must be abortable.</summary>
    public static int GameTimeoutSeconds { get; private set; } = 300;

    /// <summary>
    /// Run the AI match through PUN's offline mode instead of connecting to the master server.
    /// Defaults to on in headless mode: it removes the network dependency and the room
    /// handshake, which otherwise costs seconds per game.
    /// </summary>
    public static bool UseOfflineAI { get; private set; } = true;

    /// <summary>
    /// Play every game with BaseSeed instead of BaseSeed + index. This is the determinism
    /// harness: N games from one seed must all end with the same state fingerprint.
    /// </summary>
    public static bool RepeatSameSeed { get; private set; }

    /// <summary>
    /// When &gt; 0, the process seeds GameRandom with BaseSeed, writes that many raw values to
    /// the output file and exits without playing. Used to prove the Python port of
    /// GameRandom generates a bit-identical stream (D7).
    /// </summary>
    public static int RngProbe { get; private set; }

    /// <summary>
    /// Dump the post-setup state and abandon the game immediately instead of playing it out.
    /// The setup comparison (D7) only ever looks at that state, so playing on is wasted time:
    /// this turns a 500-seed sweep from hours into seconds.
    /// </summary>
    public static bool SetupOnly { get; private set; }

    /// <summary>
    /// Who answers for each seat: "none" (DCGO's own bot, i.e. baseline B1), "remote"
    /// (a Python agent over the decision bridge) or "random" (uniform over the legal
    /// actions the bridge enumerates - baseline B0 played inside the emulator).
    /// </summary>
    public static string Provider0 { get; private set; } = "none";

    /// <summary>See <see cref="Provider0"/>.</summary>
    public static string Provider1 { get; private set; } = "none";

    /// <summary>Host the decision bridge connects to. Loopback only; this is not a service.</summary>
    public static string BridgeHost { get; private set; } = "127.0.0.1";

    /// <summary>Port for seat 0's remote provider.</summary>
    public static int BridgePort0 { get; private set; } = 8770;

    /// <summary>
    /// Port for seat 1's remote provider. Separate from seat 0 on purpose: two agents can
    /// then play each other inside DCGO, each with its own process and its own network.
    /// </summary>
    public static int BridgePort1 { get; private set; } = 8771;

    /// <summary>
    /// How long a single decision may take before the bridge gives up and falls back to
    /// DCGO's built-in behaviour. Generous by default - a 400-iteration ISMCTS search takes
    /// seconds, and a timeout that fires during normal thinking would silently replace the
    /// agent with the random bot for that move.
    /// </summary>
    public static float BridgeTimeout { get; private set; } = 30f;

    /// <summary>True when at least one seat is driven through the decision bridge.</summary>
    public static bool BridgeActive
    {
        get { return Provider0 != "none" || Provider1 != "none"; }
    }

    /// <summary>Seed of the game currently being played. Set by HeadlessRunner before each game.</summary>
    public static long CurrentSeed { get; set; } = 1L;

    /// <summary>Seed for game number <paramref name="index"/> (0-based).</summary>
    public static long SeedForGame(int index)
    {
        return RepeatSameSeed ? BaseSeed : BaseSeed + index;
    }

    // EditorPrefs keys, shared with HeadlessEditorMode.
    public const string EditorPrefEnabled = "DCGO.Headless.Enabled";
    public const string EditorPrefSeed = "DCGO.Headless.Seed";
    public const string EditorPrefGames = "DCGO.Headless.Games";
    public const string EditorPrefDeck0 = "DCGO.Headless.Deck0";
    public const string EditorPrefDeck1 = "DCGO.Headless.Deck1";
    public const string EditorPrefTimeScale = "DCGO.Headless.TimeScale";
    public const string EditorPrefOut = "DCGO.Headless.Out";
    public const string EditorPrefTimeout = "DCGO.Headless.Timeout";
    public const string EditorPrefRepeatSeed = "DCGO.Headless.RepeatSeed";
    public const string EditorPrefRngProbe = "DCGO.Headless.RngProbe";
    public const string EditorPrefSetupOnly = "DCGO.Headless.SetupOnly";
    public const string EditorPrefProvider0 = "DCGO.Headless.Provider0";
    public const string EditorPrefProvider1 = "DCGO.Headless.Provider1";
    public const string EditorPrefBridgeHost = "DCGO.Headless.BridgeHost";
    public const string EditorPrefBridgePort0 = "DCGO.Headless.BridgePort0";
    public const string EditorPrefBridgePort1 = "DCGO.Headless.BridgePort1";
    public const string EditorPrefBridgeTimeout = "DCGO.Headless.BridgeTimeout";

    static bool _parsed;

    /// <summary>
    /// Parses the command line. Safe to call repeatedly; only the first call does work.
    /// </summary>
    public static void EnsureParsed()
    {
        if (_parsed)
        {
            return;
        }

        _parsed = true;

        string[] args;

        try
        {
            args = Environment.GetCommandLineArgs();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Headless] Could not read command line: {e.Message}");
            return;
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-dcgo-headless":
                    Enabled = true;
                    break;

                case "-seed":
                    BaseSeed = ParseLong(Next(args, i), BaseSeed);
                    break;

                case "-games":
                    Games = Mathf.Max(1, ParseInt(Next(args, i), Games));
                    break;

                case "-deck0":
                    Deck0Code = Next(args, i) ?? Deck0Code;
                    break;

                case "-deck1":
                    Deck1Code = Next(args, i) ?? Deck1Code;
                    break;

                case "-out":
                    OutputPath = Next(args, i) ?? OutputPath;
                    break;

                case "-timescale":
                    // Clamped to the measured safe range. Values above 8 soft-lock the
                    // emulator (see the TimeScale docstring); accepting them silently would
                    // turn a typo into a run of watchdog timeouts.
                    TimeScale = Mathf.Clamp(ParseFloat(Next(args, i), TimeScale), 1f, MaxSafeTimeScale);
                    break;

                case "-timeout":
                    GameTimeoutSeconds = Mathf.Max(10, ParseInt(Next(args, i), GameTimeoutSeconds));
                    break;

                case "-online-ai":
                    UseOfflineAI = false;
                    break;

                case "-repeat-seed":
                    RepeatSameSeed = true;
                    break;

                case "-rng-probe":
                    RngProbe = Mathf.Max(0, ParseInt(Next(args, i), 64));
                    break;

                case "-setup-only":
                    SetupOnly = true;
                    break;

                case "-provider0":
                    Provider0 = NormaliseProvider(Next(args, i), Provider0);
                    break;

                case "-provider1":
                    Provider1 = NormaliseProvider(Next(args, i), Provider1);
                    break;

                case "-bridge-host":
                    BridgeHost = Next(args, i) ?? BridgeHost;
                    break;

                case "-bridge-port0":
                    BridgePort0 = ParseInt(Next(args, i), BridgePort0);
                    break;

                case "-bridge-port1":
                    BridgePort1 = ParseInt(Next(args, i), BridgePort1);
                    break;

                case "-bridge-timeout":
                    BridgeTimeout = Mathf.Clamp(ParseFloat(Next(args, i), BridgeTimeout), 1f, 600f);
                    break;
            }
        }

#if UNITY_EDITOR
        // The editor never gets -dcgo-headless on its command line, so allow the same run to
        // be triggered from the DCGO menu. That turns a smoke test from a 20 minute player
        // build into a few seconds of play mode.
        if (!Enabled && UnityEditor.EditorPrefs.GetBool(EditorPrefEnabled, false))
        {
            Enabled = true;
            BaseSeed = (long)UnityEditor.EditorPrefs.GetInt(EditorPrefSeed, 1);
            Games = Mathf.Max(1, UnityEditor.EditorPrefs.GetInt(EditorPrefGames, 3));
            Deck0Code = UnityEditor.EditorPrefs.GetString(EditorPrefDeck0, "");
            Deck1Code = UnityEditor.EditorPrefs.GetString(EditorPrefDeck1, "");
            TimeScale = Mathf.Clamp(UnityEditor.EditorPrefs.GetFloat(EditorPrefTimeScale, MaxSafeTimeScale),
                                    1f, MaxSafeTimeScale);
            OutputPath = UnityEditor.EditorPrefs.GetString(EditorPrefOut, "headless-results.jsonl");
            GameTimeoutSeconds = Mathf.Max(10, UnityEditor.EditorPrefs.GetInt(EditorPrefTimeout, 120));
            RepeatSameSeed = UnityEditor.EditorPrefs.GetBool(EditorPrefRepeatSeed, false);
            RngProbe = Mathf.Max(0, UnityEditor.EditorPrefs.GetInt(EditorPrefRngProbe, 0));
            SetupOnly = UnityEditor.EditorPrefs.GetBool(EditorPrefSetupOnly, false);
            Provider0 = NormaliseProvider(UnityEditor.EditorPrefs.GetString(EditorPrefProvider0, "none"), "none");
            Provider1 = NormaliseProvider(UnityEditor.EditorPrefs.GetString(EditorPrefProvider1, "none"), "none");
            BridgeHost = UnityEditor.EditorPrefs.GetString(EditorPrefBridgeHost, BridgeHost);
            BridgePort0 = UnityEditor.EditorPrefs.GetInt(EditorPrefBridgePort0, BridgePort0);
            BridgePort1 = UnityEditor.EditorPrefs.GetInt(EditorPrefBridgePort1, BridgePort1);
            BridgeTimeout = Mathf.Clamp(UnityEditor.EditorPrefs.GetFloat(EditorPrefBridgeTimeout, BridgeTimeout), 1f, 600f);

            Debug.Log("[Headless] enabled from editor menu (DCGO > Headless)");
        }

        // Bridge settings are read even when batch mode is off, because the two are
        // independent: evaluation against human players (thesis chapter 7) runs the GUI at
        // normal speed with a remote agent on the opponent seat, and that combination is
        // exactly "bridge on, headless off".
        else
        {
            Provider0 = NormaliseProvider(UnityEditor.EditorPrefs.GetString(EditorPrefProvider0, Provider0), Provider0);
            Provider1 = NormaliseProvider(UnityEditor.EditorPrefs.GetString(EditorPrefProvider1, Provider1), Provider1);
            BridgeHost = UnityEditor.EditorPrefs.GetString(EditorPrefBridgeHost, BridgeHost);
            BridgePort0 = UnityEditor.EditorPrefs.GetInt(EditorPrefBridgePort0, BridgePort0);
            BridgePort1 = UnityEditor.EditorPrefs.GetInt(EditorPrefBridgePort1, BridgePort1);
            BridgeTimeout = Mathf.Clamp(UnityEditor.EditorPrefs.GetFloat(EditorPrefBridgeTimeout, BridgeTimeout), 1f, 600f);
        }
#endif

        CurrentSeed = BaseSeed;

        if (Enabled)
        {
            Debug.Log($"[Headless] enabled | seed={BaseSeed} games={Games} timeScale={TimeScale} " +
                      $"offlineAI={UseOfflineAI} out={OutputPath}");
        }

        if (BridgeActive)
        {
            Debug.Log($"[Bridge] seat0={Provider0} seat1={Provider1} | {BridgeHost}:" +
                      $"{BridgePort0}/{BridgePort1} timeout={BridgeTimeout}s");
        }
    }

    /// <summary>
    /// Accepts only the three known provider names. An unrecognised value keeps the previous
    /// setting instead of silently disabling the agent, which would turn a typo in a script
    /// into a whole experiment quietly measuring the built-in bot instead.
    /// </summary>
    static string NormaliseProvider(string value, string fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        string lowered = value.Trim().ToLowerInvariant();

        if (lowered == "none" || lowered == "remote" || lowered == "random")
        {
            return lowered;
        }

        Debug.LogWarning($"[Bridge] unknown provider '{value}' — keeping '{fallback}'");
        return fallback;
    }

    static string Next(string[] args, int i)
    {
        return (i + 1 < args.Length) ? args[i + 1] : null;
    }

    static long ParseLong(string s, long fallback)
    {
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;
    }

    static int ParseInt(string s, int fallback)
    {
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    }

    static float ParseFloat(string s, float fallback)
    {
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }
}
