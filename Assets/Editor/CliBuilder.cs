using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class CliBuilder
{
    public static void Build()
    {
        var args = Environment.GetCommandLineArgs();
        string outputDir = string.Empty;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDir = args[i + 1].Trim('"');
                if (outputDir == string.Empty)
                {
                    LogError("Please specify a valid output directory.");
                    EditorApplication.Exit(1);
                    return;
                }
                break;
            }
        }
        BuildReport report = BuildPipeline.BuildPlayer(CreateOptions(outputDir));
        BuildSummary summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
            Log($"Build succeeded → {summary.totalSize / 1048576f:F1} MB at {outputDir}");
        else
        {
            LogError("Build failed!");
            EditorApplication.Exit(1);
        }
    }

    // GUI alternative to build.sh; uses the same build options.
    [MenuItem("MateEngine/Build Linux Player...")]
    public static void BuildFromMenu()
    {
        string projectDir = Path.GetDirectoryName(Application.dataPath);
        string buildDir = EditorUtility.SaveFolderPanel("Build Linux Player", projectDir, "Build");
        if (string.IsNullOrEmpty(buildDir))
            return;

        string outputPath = Path.Combine(buildDir, "MateEngineX.x86_64");
        BuildReport report = BuildPipeline.BuildPlayer(CreateOptions(outputPath));
        if (report.summary.result != BuildResult.Succeeded)
        {
            EditorUtility.DisplayDialog("Build Linux Player", "Build failed. See the Console for details.", "OK");
            return;
        }

        string packageCommand = $"./scripts/package-build.sh \"{buildDir}\"";
        Debug.Log($"CliBuilder: Build succeeded at {outputPath}. Package the presenter with: {packageCommand}");
        EditorUtility.DisplayDialog("Build Linux Player",
            $"Build succeeded.\n\nFrom the repository root, package the native Wayland presenter:\n\n{packageCommand}",
            "OK");
    }

    static BuildPlayerOptions CreateOptions(string outputPath)
    {
        return new BuildPlayerOptions
        {
            scenes = new[] { "Assets/MATE ENGINE - Scenes/Mate Engine Main.unity"},
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.CompressWithLz4HC
        };
    }

    static void Log(string message)
    {
        Console.WriteLine();
        Console.WriteLine("##############################################");
        Console.WriteLine("CliBuilder: " + message);
        Console.WriteLine("##############################################");
        Console.WriteLine();
    }
    
    static void LogError(string message)
    {
        Console.WriteLine();
        Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Console.WriteLine("CliBuilder: " + message);
        Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Console.WriteLine();
    }
}