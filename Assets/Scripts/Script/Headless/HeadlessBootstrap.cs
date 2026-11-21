using UnityEngine;

/// <summary>
/// Entry point for batch mode.
///
/// Hooks itself in before the first scene loads, so no scene or prefab has to be edited.
/// That matters: Unity scenes are binary-ish YAML and editing them outside the editor is
/// both error-prone and merge-hostile, while this file is plain code.
///
/// When -dcgo-headless is absent this does nothing at all.
/// </summary>
public static class HeadlessBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        HeadlessConfig.EnsureParsed();

        if (!HeadlessConfig.Enabled)
        {
            return;
        }

        ApplyPerformanceSettings();

        GameObject go = new GameObject("[HeadlessRunner]");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<HeadlessRunner>();
    }

    /// <summary>
    /// Strips away everything that costs wall-clock time but does not affect the rules:
    /// audio, frame rate caps, and the animation delays scattered through the card effects.
    /// </summary>
    static void ApplyPerformanceSettings()
    {
        // timeScale is deliberately left alone here. HeadlessRunner raises it only once the
        // Opening scene has finished initialising: speeding the clock up during startup makes
        // the WaitForSeconds calls in the loading sequence race the Task-based asset loading.

        // Coroutines that yield per frame are bounded by frame rate, not timeScale.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        AudioListener.volume = 0f;
        AudioListener.pause = true;

        // Keep the process alive while it grinds through games with no input.
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

        Debug.Log("[Headless] targetFrameRate=uncapped audio=off (timeScale raised after startup)");
    }
}
