using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Plays a configured number of bot-vs-bot games without user input and appends one
/// JSON line per game to the output file.
///
/// Every game runs with a known seed, so a result can be reproduced by replaying the
/// same seed. That is the property the whole experimental methodology rests on.
/// </summary>
public class HeadlessRunner : MonoBehaviour
{
    const string BattleSceneName = "BattleScene";

    /// <summary>
    /// Budget for the Opening scene to load the card database and build the starter decks.
    /// Generous on purpose: the first run downloads and caches card images.
    /// </summary>
    const float StartupTimeoutSeconds = 300f;

    // All timing below uses Time.unscaledTime, not Time.realtimeSinceStartup: it counts real
    // seconds and ignores timeScale, but it advances only while the game loop runs. Wall-clock
    // time keeps ticking while the editor is paused, which turns a healthy game into a
    // recorded timeout the moment anyone hits pause.

    bool _gameFinished;
    HeadlessHooks.GameEndInfo _lastResult;
    StreamWriter _writer;

    void Start()
    {
        StartCoroutine(RunAll());
    }

    /// <summary>
    /// Dumps raw GameRandom output for a fixed seed, then exits. Proving that the Python
    /// port produces the identical stream is what lets D7 replay a whole game in both
    /// engines from nothing but a seed and an action list.
    /// </summary>
    void RunRngProbe()
    {
        int count = HeadlessConfig.RngProbe;
        long seed = HeadlessConfig.BaseSeed;

        Debug.Log($"[Headless] RNG probe: seed={seed}, {count} values");

        GameRandom.Seed(seed);

        var sb = new StringBuilder(count * 12);
        sb.Append("seed=").Append(seed).Append('\n');

        // Range(0, 1000000) exercises the same rejection-sampling path the game uses,
        // so a mismatch in that logic shows up here rather than silently later.
        for (int i = 0; i < count; i++)
        {
            sb.Append(GameRandom.Range(0, 1000000)).Append('\n');
        }

        try
        {
            string path = Path.GetFullPath(HeadlessConfig.OutputPath + ".rng.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Debug.Log($"[Headless] RNG probe written to {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Headless] RNG probe failed: {e.Message}");
        }

        Finish(0);
    }

    IEnumerator RunAll()
    {
        if (HeadlessConfig.RngProbe > 0)
        {
            RunRngProbe();
            yield break;
        }

        // In the editor, play mode starts from whatever scene is open. If that is already
        // BattleScene, loading another copy additively would corrupt the run.
        Scene existingBattle = SceneManager.GetSceneByName(BattleSceneName);

        if (existingBattle.IsValid() && existingBattle.isLoaded && SceneManager.sceneCount > 1)
        {
            Debug.LogWarning("[Headless] BattleScene was already open — unloading it first");
            yield return SceneManager.UnloadSceneAsync(existingBattle);
        }
        else if (existingBattle.IsValid() && existingBattle.isLoaded)
        {
            Debug.LogError("[Headless] BattleScene is the only open scene. Open Assets/Scenes/Opening.unity " +
                           "before entering play mode.");
            Finish(4);
            yield break;
        }

        // Deliberately NOT calling ContinuousController.LoadCoroutine() here.
        //
        // The Opening scene already owns that bootstrap (Opening.cs). LoadCoroutine guards on
        // `instance == null`, but LoadSceneAsync leaves instance null for several frames, so a
        // second caller sails past the guard and ContinuousControllerScene gets loaded twice:
        // duplicate PhotonView 400, PUN destroys one singleton, and the async Init() then dies
        // with MissingReferenceException before it reaches SetStarterDecks(). We just wait.
        Debug.Log("[Headless] waiting for ContinuousController to finish initialising");

        float deadline = Time.unscaledTime + StartupTimeoutSeconds;

        yield return new WaitUntil(() =>
            ContinuousController.instance != null || Time.unscaledTime > deadline);

        if (ContinuousController.instance == null)
        {
            Debug.LogError($"[Headless] ContinuousController never appeared within {StartupTimeoutSeconds}s. " +
                           "Is Assets/Scenes/Opening.unity the open scene?");
            Finish(5);
            yield break;
        }

        yield return new WaitUntil(() => CardDatabaseReady() || Time.unscaledTime > deadline);

        if (!CardDatabaseReady())
        {
            Debug.LogError($"[Headless] card database not loaded within {StartupTimeoutSeconds}s");
            Finish(5);
            yield break;
        }

        Debug.Log($"[Headless] card database ready ({ContinuousController.instance.CardList.Length} cards)");

        // CardList fills in well before ContinuousController.Init reaches SetStarterDecks(), so
        // waiting on the card database alone would ask for decks too early.
        yield return new WaitUntil(() =>
            ContinuousController.instance.DeckDatas.Count > 0 || Time.unscaledTime > deadline);

        Debug.Log($"[Headless] deck list ready ({ContinuousController.instance.DeckDatas.Count} decks)");

        // Startup is done: safe to speed the clock up now. Doing it earlier makes the
        // WaitForSeconds calls in the loading sequence race the Task-based asset loading.
        Time.timeScale = HeadlessConfig.TimeScale;
        Debug.Log($"[Headless] timeScale={Time.timeScale}");

        DeckData deck0 = ResolveDeck(HeadlessConfig.Deck0Code, "deck0", required: true);
        DeckData deck1 = ResolveDeck(HeadlessConfig.Deck1Code, "deck1", required: false);

        if (deck0 == null)
        {
            Debug.LogError("[Headless] no usable deck for player 0 — aborting");
            Finish(2);
            yield break;
        }

        if (!OpenWriter())
        {
            Finish(3);
            yield break;
        }

        int completed = 0;
        int aborted = 0;

        for (int i = 0; i < HeadlessConfig.Games; i++)
        {
            yield return StartCoroutine(RunOneGame(i, deck0, deck1));

            if (_gameFinished)
            {
                completed++;
            }
            else
            {
                aborted++;
            }
        }

        Debug.Log($"[Headless] done — {completed} completed, {aborted} aborted (timeout/soft-lock)");

        CloseWriter();
        Finish(0);
    }

    IEnumerator RunOneGame(int index, DeckData deck0, DeckData deck1)
    {
        long seed = HeadlessConfig.SeedForGame(index);
        HeadlessConfig.CurrentSeed = seed;

        ContinuousController cc = ContinuousController.instance;
        cc.isAI = true;
        cc.isRandomMatch = true;
        cc.BattleDeckData = deck0;

        HeadlessHooks.OpponentDeckOverride = deck1;

        _gameFinished = false;
        _lastResult = default;
        HeadlessHooks.GameEnded += OnGameEnded;

        float startedAt = Time.unscaledTime;

        Debug.Log($"[Headless] game {index + 1}/{HeadlessConfig.Games} starting (seed={seed})");

        yield return SceneManager.LoadSceneAsync(BattleSceneName, LoadSceneMode.Additive);

        // Zrzut stanu tuz po przygotowaniu partii: potasowane talie, rece, stosy security.
        // To jest material do walidacji roznicowej - porownujemy go z tym samym ziarnem
        // odtworzonym w dcg_core.
        if (HeadlessConfig.SetupOnly)
        {
            // Test porownawczy patrzy wylacznie na ten stan, wiec rozgrywanie partii do
            // konca byloby czystym marnotrawstwem czasu.
            yield return StartCoroutine(DumpSetupState(index, seed));

            Debug.Log($"[Headless] game {index + 1}/{HeadlessConfig.Games} setup dumped (seed={seed})");

            HeadlessHooks.GameEnded -= OnGameEnded;
            yield return StartCoroutine(TearDownBattle());
            yield break;
        }

        StartCoroutine(DumpSetupState(index, seed));

        float deadline = Time.unscaledTime + HeadlessConfig.GameTimeoutSeconds;

        while (!_gameFinished && Time.unscaledTime < deadline)
        {
            yield return null;
        }

        HeadlessHooks.GameEnded -= OnGameEnded;

        float duration = Time.unscaledTime - startedAt;
        int turns = CurrentTurnCount();

        // Must be taken before teardown, while the battle scene objects still exist.
        string stateHash = GameStateHash.Compute();

        WriteResult(index, seed, deck0, deck1, duration, turns, stateHash);

        if (_gameFinished)
        {
            Debug.Log($"[Headless] game {index + 1} finished in {duration:F1}s, " +
                      $"{turns} turns, winner={_lastResult.WinnerName ?? "draw"}");
        }
        else
        {
            Debug.LogWarning($"[Headless] game {index + 1} ABORTED after {duration:F1}s " +
                             $"({turns} turns) — watchdog fired");
        }

        yield return StartCoroutine(TearDownBattle());
    }

    /// <summary>
    /// Unloads the battle scene without going through ContinuousController.EndBattle,
    /// which depends on the Opening scene's UI objects being alive.
    /// </summary>
    IEnumerator TearDownBattle()
    {
        HeadlessHooks.OpponentDeckOverride = null;

        // A watchdog abort skips EndGame, so battle coroutines hosted on the persistent
        // ContinuousController keep running against objects that are about to be destroyed
        // and bleed into the next game. EndGame does exactly this on the normal path.
        if (!_gameFinished && ContinuousController.instance != null)
        {
            ContinuousController.instance.StopAllCoroutines();
        }

        Scene scene = SceneManager.GetSceneByName(BattleSceneName);

        if (scene.IsValid() && scene.isLoaded)
        {
            AsyncOperation unload = SceneManager.UnloadSceneAsync(scene);

            if (unload != null)
            {
                yield return unload;
            }
        }

        yield return Resources.UnloadUnusedAssets();

        // Let the destroyed singletons settle before the next scene load.
        yield return null;
    }

    void OnGameEnded(HeadlessHooks.GameEndInfo info)
    {
        if (_gameFinished)
        {
            return;
        }

        _lastResult = info;
        _gameFinished = true;
    }

    /// <summary>
    /// Czeka, az partia bedzie przygotowana, i zapisuje kanoniczny opis stanu.
    /// Zrzut nastepuje przy pierwszej turze, czyli po zbudowaniu stosow security i po
    /// mulliganie - to najwczesniejszy moment, w ktorym stan jest kompletny i stabilny.
    /// </summary>
    IEnumerator DumpSetupState(int index, long seed)
    {
        float deadline = Time.unscaledTime + 60f;

        yield return new WaitUntil(() =>
            _gameFinished
            || Time.unscaledTime > deadline
            || (GManager.instance != null
                && GManager.instance.turnStateMachine != null
                && GManager.instance.turnStateMachine.gameContext != null
                && GManager.instance.turnStateMachine.TurnCount >= 1));

        if (_gameFinished || GManager.instance == null || GManager.instance.turnStateMachine == null)
        {
            yield break;
        }

        try
        {
            string path = Path.GetFullPath(HeadlessConfig.OutputPath + ".setup.txt");
            string block =
                $"### game={index} seed={seed}\n{GameStateHash.Describe()}\n" +
                $"### hash={GameStateHash.Compute()}\n\n";
            File.AppendAllText(path, block, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Headless] setup dump failed: {e.Message}");
        }
    }

    static bool CardDatabaseReady()
    {
        return ContinuousController.instance != null &&
               ContinuousController.instance.CardList != null &&
               ContinuousController.instance.CardList.Length > 0;
    }

    static int CurrentTurnCount()
    {
        if (GManager.instance != null && GManager.instance.turnStateMachine != null)
        {
            return GManager.instance.turnStateMachine.TurnCount;
        }

        return -1;
    }

    #region deck resolution

    DeckData ResolveDeck(string deckCode, string label, bool required)
    {
        if (!string.IsNullOrEmpty(deckCode))
        {
            // Wygodniej podac nazwe talii ("st1") niz jej kod - a bez przypiecia obu talii
            // porownanie miedzy silnikami nie ma sensu, bo DCGO losuje talie przeciwnika.
            if (!DeckData.IsValidDeckCode(deckCode))
            {
                DeckData byName = FindSavedDeckByName(deckCode);

                if (byName != null)
                {
                    Debug.Log($"[Headless] {label}: uzyto zapisanej talii o nazwie '{deckCode}'");
                    return byName;
                }

                Debug.LogError($"[Headless] {label}: '{deckCode}' nie jest ani poprawnym kodem talii, "
                               + "ani nazwa zapisanej talii");
                return null;
            }

            DeckData deck = new DeckData(deckCode);

            if (!deck.IsValidDeckData())
            {
                Debug.LogError($"[Headless] {label}: deck code parsed but is not a legal deck " +
                               $"(main={deck.DeckCards().Count}, eggs={deck.DigitamaDeckCards().Count})");
                return null;
            }

            Debug.Log($"[Headless] {label}: '{deck.DeckName}' loaded from deck code");
            return deck;
        }

        // No code given: fall back to the first valid locally saved deck, so the harness
        // is usable before the ST-1/ST-2 deck codes exist.
        if (ContinuousController.instance != null)
        {
            foreach (DeckData saved in ContinuousController.instance.DeckDatas)
            {
                if (saved != null && saved.IsValidDeckData())
                {
                    Debug.Log($"[Headless] {label}: falling back to saved deck '{saved.DeckName}'");
                    return new DeckData(saved.GetThisDeckCode(), saved.DeckID);
                }
            }
        }

        if (required)
        {
            Debug.LogError($"[Headless] {label}: no deck code given and no valid saved deck found");
        }
        else
        {
            Debug.LogWarning($"[Headless] {label}: not set — DCGO will pick the bot deck itself");
        }

        return null;
    }

    static DeckData FindSavedDeckByName(string name)
    {
        if (ContinuousController.instance == null)
        {
            return null;
        }

        foreach (DeckData saved in ContinuousController.instance.DeckDatas)
        {
            if (saved == null || !saved.IsValidDeckData())
            {
                continue;
            }

            if (string.Equals(saved.DeckName, name, StringComparison.OrdinalIgnoreCase))
            {
                return new DeckData(saved.GetThisDeckCode(), saved.DeckID);
            }
        }

        return null;
    }

    #endregion

    #region result log

    bool OpenWriter()
    {
        try
        {
            string path = Path.GetFullPath(HeadlessConfig.OutputPath);
            string dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(path, append: true, encoding: new UTF8Encoding(false));
            _writer.AutoFlush = true;

            Debug.Log($"[Headless] writing results to {path}");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Headless] cannot open output file: {e.Message}");
            return false;
        }
    }

    void WriteResult(int index, long seed, DeckData deck0, DeckData deck1, float duration, int turns, string stateHash)
    {
        if (_writer == null)
        {
            return;
        }

        var sb = new StringBuilder(256);
        sb.Append('{');
        Field(sb, "game", index); sb.Append(',');
        Field(sb, "seed", seed); sb.Append(',');
        Field(sb, "completed", _gameFinished); sb.Append(',');
        Field(sb, "winner_id", _gameFinished ? _lastResult.WinnerPlayerId : -1); sb.Append(',');
        Field(sb, "winner_name", _gameFinished ? _lastResult.WinnerName : null); sb.Append(',');
        Field(sb, "winner_is_you", _gameFinished && _lastResult.WinnerIsYou); sb.Append(',');
        Field(sb, "surrendered", _gameFinished && _lastResult.Surrendered); sb.Append(',');
        Field(sb, "end_effect", _gameFinished ? _lastResult.EffectName : null); sb.Append(',');
        Field(sb, "turns", turns); sb.Append(',');
        Field(sb, "state_hash", stateHash); sb.Append(',');

        // Warunki przebiegu zapisane przy KAZDEJ partii, nie tylko w logu. Plik wynikowy
        // jest otwierany w trybie dopisywania, wiec kilka przebiegow laduje w jednym pliku;
        // bez tych pol nie da sie pozniej odroznic, ktora linia pochodzi z ktorych ustawien.
        // Kosztowalo to juz jedna rekonstrukcje po kolejnosci wpisow.
        Field(sb, "time_scale", Time.timeScale); sb.Append(',');
        Field(sb, "repeat_seed", HeadlessConfig.RepeatSameSeed); sb.Append(',');

        // Per-seat bridge accounting. A game with declines is not a clean measurement of the
        // agent - some of its moves were played by DCGO's random bot instead - so the numbers
        // travel with the result rather than living only in the log.
        Field(sb, "provider0", HeadlessConfig.Provider0); sb.Append(',');
        Field(sb, "provider1", HeadlessConfig.Provider1); sb.Append(',');
        Field(sb, "answered0", DecisionRouter.AnsweredCount(0)); sb.Append(',');
        Field(sb, "answered1", DecisionRouter.AnsweredCount(1)); sb.Append(',');
        Field(sb, "declined0", DecisionRouter.DeclinedCount(0)); sb.Append(',');
        Field(sb, "declined1", DecisionRouter.DeclinedCount(1)); sb.Append(',');
        Field(sb, "duration_s", duration); sb.Append(',');
        Field(sb, "deck0", deck0 != null ? deck0.DeckName : null); sb.Append(',');
        Field(sb, "deck1", deck1 != null ? deck1.DeckName : null);
        sb.Append('}');

        try
        {
            _writer.WriteLine(sb.ToString());
        }
        catch (Exception e)
        {
            Debug.LogError($"[Headless] failed to write result: {e.Message}");
        }
    }

    void CloseWriter()
    {
        if (_writer == null)
        {
            return;
        }

        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Headless] error closing output file: {e.Message}");
        }

        _writer = null;
    }

    static void Field(StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":");

        if (value == null)
        {
            sb.Append("null");
            return;
        }

        sb.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        sb.Append('"');
    }

    static void Field(StringBuilder sb, string key, bool value)
    {
        sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
    }

    static void Field(StringBuilder sb, string key, int value)
    {
        sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    static void Field(StringBuilder sb, string key, long value)
    {
        sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    static void Field(StringBuilder sb, string key, float value)
    {
        sb.Append('"').Append(key).Append("\":").Append(value.ToString("F3", CultureInfo.InvariantCulture));
    }

    #endregion

    void Finish(int exitCode)
    {
        CloseWriter();

        // Closes the bridge sockets. Without this the Python server keeps a half-open
        // connection and its next run refuses to bind the port.
        DecisionBridge.Shutdown();

        Debug.Log($"[Headless] exiting with code {exitCode}");

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(exitCode);
#endif
    }

    void OnDestroy()
    {
        HeadlessHooks.GameEnded -= OnGameEnded;
        CloseWriter();
        DecisionBridge.Shutdown();
    }
}
