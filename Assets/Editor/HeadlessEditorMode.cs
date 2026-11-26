using UnityEditor;
using UnityEngine;

/// <summary>
/// Lets the batch harness be driven from play mode instead of a standalone build.
/// Useful while iterating: a player build of this project takes tens of minutes,
/// entering play mode takes seconds.
/// </summary>
public class HeadlessEditorMode : EditorWindow
{
    [MenuItem("DCGO/Headless Settings")]
    public static void Open()
    {
        GetWindow<HeadlessEditorMode>(false, "Headless", true).minSize = new Vector2(420, 260);
    }

    [MenuItem("DCGO/Toggle Headless Run On Play")]
    public static void Toggle()
    {
        bool value = !EditorPrefs.GetBool(HeadlessConfig.EditorPrefEnabled, false);
        EditorPrefs.SetBool(HeadlessConfig.EditorPrefEnabled, value);
        Debug.Log($"[Headless] run on play: {(value ? "ON" : "OFF")}");
    }

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "When enabled, entering play mode runs bot-vs-bot games with no input and writes " +
            "one JSON line per game, then exits play mode.\n\n" +
            "Leave the deck codes empty to use the first valid deck saved in this DCGO profile.",
            MessageType.Info);

        EditorGUILayout.Space();

        bool enabled = EditorGUILayout.Toggle(
            new GUIContent("Run on play", "Start the batch runner when entering play mode"),
            EditorPrefs.GetBool(HeadlessConfig.EditorPrefEnabled, false));
        EditorPrefs.SetBool(HeadlessConfig.EditorPrefEnabled, enabled);

        using (new EditorGUI.DisabledScope(!enabled))
        {
            int seed = EditorGUILayout.IntField(
                new GUIContent("Base seed", "Game N uses baseSeed + N"),
                EditorPrefs.GetInt(HeadlessConfig.EditorPrefSeed, 1));
            EditorPrefs.SetInt(HeadlessConfig.EditorPrefSeed, seed);

            int games = Mathf.Max(1, EditorGUILayout.IntField(
                "Games",
                EditorPrefs.GetInt(HeadlessConfig.EditorPrefGames, 3)));
            EditorPrefs.SetInt(HeadlessConfig.EditorPrefGames, games);

            // Slider capped at the measured safe ceiling. A sweep with "Repeat same seed"
            // reproduced the identical state hash at 1, 4 and 8, but at 16 three games out of
            // five soft-locked on turn 8. Raising it further would not help anyway: 40.5 s per
            // game is scene loading and per-frame coroutine waits, which timeScale never touches.
            float timeScale = EditorGUILayout.Slider(
                new GUIContent("Time scale",
                    "Shortens every WaitForSeconds. Capped at 8 — above that the emulator soft-locks."),
                EditorPrefs.GetFloat(HeadlessConfig.EditorPrefTimeScale, HeadlessConfig.MaxSafeTimeScale),
                1f, HeadlessConfig.MaxSafeTimeScale);
            EditorPrefs.SetFloat(HeadlessConfig.EditorPrefTimeScale, timeScale);

            bool setupOnly = EditorGUILayout.Toggle(
                new GUIContent("Setup only",
                    "Zrzuc stan po przygotowaniu partii i przejdz do kolejnego ziarna, bez rozgrywania."),
                EditorPrefs.GetBool(HeadlessConfig.EditorPrefSetupOnly, false));
            EditorPrefs.SetBool(HeadlessConfig.EditorPrefSetupOnly, setupOnly);

            int rngProbe = Mathf.Max(0, EditorGUILayout.IntField(
                new GUIContent("RNG probe",
                    "Gdy > 0: zapisz tyle surowych wartosci GameRandom dla ziarna bazowego i zakoncz, bez grania."),
                EditorPrefs.GetInt(HeadlessConfig.EditorPrefRngProbe, 0)));
            EditorPrefs.SetInt(HeadlessConfig.EditorPrefRngProbe, rngProbe);

            bool repeatSeed = EditorGUILayout.Toggle(
                new GUIContent("Repeat same seed",
                    "Determinism check: play every game with the base seed. All games must end with the same state hash."),
                EditorPrefs.GetBool(HeadlessConfig.EditorPrefRepeatSeed, false));
            EditorPrefs.SetBool(HeadlessConfig.EditorPrefRepeatSeed, repeatSeed);

            int timeout = Mathf.Max(10, EditorGUILayout.IntField(
                new GUIContent("Game timeout (s)", "Watchdog per game; a soft-locked game is recorded as not completed"),
                EditorPrefs.GetInt(HeadlessConfig.EditorPrefTimeout, 120)));
            EditorPrefs.SetInt(HeadlessConfig.EditorPrefTimeout, timeout);

            string deck0 = EditorGUILayout.TextField(
                new GUIContent("Deck 0 code", "Deck for the 'You' side"),
                EditorPrefs.GetString(HeadlessConfig.EditorPrefDeck0, ""));
            EditorPrefs.SetString(HeadlessConfig.EditorPrefDeck0, deck0);

            string deck1 = EditorGUILayout.TextField(
                new GUIContent("Deck 1 code", "Deck for the bot side"),
                EditorPrefs.GetString(HeadlessConfig.EditorPrefDeck1, ""));
            EditorPrefs.SetString(HeadlessConfig.EditorPrefDeck1, deck1);

            string outPath = EditorGUILayout.TextField(
                new GUIContent("Output file", "Relative to the project folder"),
                EditorPrefs.GetString(HeadlessConfig.EditorPrefOut, "headless-results.jsonl"));
            EditorPrefs.SetString(HeadlessConfig.EditorPrefOut, outPath);
        }

        EditorGUILayout.Space();
        DrawBridgeSection();

        EditorGUILayout.Space();

        if (GUILayout.Button("Enter play mode now"))
        {
            EditorApplication.isPlaying = true;
        }
    }

    /// <summary>
    /// Decision bridge settings. Outside the "Run on play" disabled scope on purpose: the
    /// bridge and batch mode are independent. Evaluation against human players runs the GUI
    /// at normal speed with a remote agent on one seat, which is bridge on, batch mode off.
    /// </summary>
    static readonly string[] ProviderNames = { "none", "remote", "random", "random-isolated" };

    void DrawBridgeSection()
    {
        EditorGUILayout.LabelField("Decision bridge", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            "none = DCGO's built-in bot (baseline B1)\n" +
            "remote = a Python agent over TCP loopback (start the server first)\n" +
            "random = uniform over the enumerated legal actions (baseline B0)\n" +
            "random-isolated = the same, with its own generator (transition test)\n\n" +
            "Works with or without batch mode. Seat 0 is the 'You' side.",
            MessageType.None);

        DrawProvider("Seat 0 provider", HeadlessConfig.EditorPrefProvider0);
        DrawProvider("Seat 1 provider", HeadlessConfig.EditorPrefProvider1);

        string host = EditorGUILayout.TextField(
            new GUIContent("Bridge host", "Loopback only — this is not a service"),
            EditorPrefs.GetString(HeadlessConfig.EditorPrefBridgeHost, "127.0.0.1"));
        EditorPrefs.SetString(HeadlessConfig.EditorPrefBridgeHost, host);

        int port0 = EditorGUILayout.IntField("Seat 0 port",
            EditorPrefs.GetInt(HeadlessConfig.EditorPrefBridgePort0, 8770));
        EditorPrefs.SetInt(HeadlessConfig.EditorPrefBridgePort0, port0);

        int port1 = EditorGUILayout.IntField("Seat 1 port",
            EditorPrefs.GetInt(HeadlessConfig.EditorPrefBridgePort1, 8771));
        EditorPrefs.SetInt(HeadlessConfig.EditorPrefBridgePort1, port1);

        float timeout = EditorGUILayout.Slider(
            new GUIContent("Decision timeout (s)",
                "A search takes seconds; too low a value silently replaces the agent with the built-in bot"),
            EditorPrefs.GetFloat(HeadlessConfig.EditorPrefBridgeTimeout, 30f), 1f, 600f);
        EditorPrefs.SetFloat(HeadlessConfig.EditorPrefBridgeTimeout, timeout);
    }

    static void DrawProvider(string label, string prefKey)
    {
        string current = EditorPrefs.GetString(prefKey, "none");

        int index = System.Array.IndexOf(ProviderNames, current);
        index = EditorGUILayout.Popup(label, index < 0 ? 0 : index, ProviderNames);

        EditorPrefs.SetString(prefKey, ProviderNames[index]);
    }
}
