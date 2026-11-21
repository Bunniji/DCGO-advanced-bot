using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the standalone player used by the research harness.
///
/// From the command line:
///   Unity.exe -quit -batchmode -nographics -projectPath &lt;proj&gt; \
///             -executeMethod HeadlessBuild.BuildWindows \
///             -buildOut &lt;dir&gt; -logFile &lt;log&gt;
/// </summary>
public static class HeadlessBuild
{
    [MenuItem("DCGO/Build Headless Player (Windows 64)")]
    public static void BuildWindows()
    {
        string outDir = GetArg("-buildOut");

        if (string.IsNullOrEmpty(outDir))
        {
            outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build", "Headless");
        }

        try
        {
            Directory.CreateDirectory(outDir);
        }
        catch (Exception e)
        {
            Debug.LogError($"[HeadlessBuild] cannot create output directory '{outDir}': {e.Message}");
            Exit(2);
            return;
        }

        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Debug.LogError("[HeadlessBuild] no enabled scenes in build settings");
            Exit(2);
            return;
        }

        string exePath = Path.Combine(outDir, "DCGO-headless.exe");

        Debug.Log($"[HeadlessBuild] building {scenes.Length} scenes -> {exePath}");

        foreach (string scene in scenes)
        {
            Debug.Log($"[HeadlessBuild]   scene: {scene}");
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = exePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,

            // Development keeps the full managed stack traces, which is what makes a
            // soft-lock or a null reference in batch mode diagnosable at all.
            options = BuildOptions.Development,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;

        Debug.Log($"[HeadlessBuild] result={summary.result} " +
                  $"size={summary.totalSize / (1024 * 1024)}MB " +
                  $"time={summary.totalTime} " +
                  $"errors={summary.totalErrors} warnings={summary.totalWarnings}");

        if (summary.result != BuildResult.Succeeded)
        {
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                    {
                        Debug.LogError($"[HeadlessBuild] {step.name}: {message.content}");
                    }
                }
            }

            Exit(1);
            return;
        }

        Debug.Log($"[HeadlessBuild] OK -> {exePath}");
        Exit(0);
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    static void Exit(int code)
    {
        // Only force an exit for command line runs; from the menu we just leave the editor alone.
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(code);
        }
    }
}
