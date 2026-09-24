using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MAAYAI.Matrix.Swarm.Editor
{
    /// <summary>
    /// Exports the GPU boid swarm engine (everything under Assets/BoidSwarmEngine) as a .unitypackage.
    ///
    /// Editor:    Tools > Export Boid Swarm Package  (writes to the Desktop)
    /// Batchmode: Unity.exe -batchmode -quit -projectPath &lt;project&gt;
    ///              -executeMethod MAAYAI.Matrix.Swarm.Editor.SwarmPackageExporter.ExportFromCommandLine
    ///              [-exportPath &lt;file.unitypackage&gt;]
    /// Batchmode only works while the project is closed in the Editor.
    /// </summary>
    public static class SwarmPackageExporter
    {
        private const string PackageFileName = "BoidSwarmEngine.unitypackage";
        private static readonly string[] ExportRoots = { "Assets/BoidSwarmEngine" };

        [MenuItem("Tools/Export Boid Swarm Package", priority = 20)]
        public static void ExportFromMenu()
        {
            try
            {
                string path = Export(DefaultOutputPath());
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Export Boid Swarm Package", e.Message, "OK");
            }
        }

        public static void ExportFromCommandLine()
        {
            try
            {
                Export(GetArgument("-exportPath") ?? DefaultOutputPath());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static string Export(string outputPath)
        {
            foreach (string root in ExportRoots)
            {
                if (!AssetDatabase.IsValidFolder(root))
                    throw new DirectoryNotFoundException($"Export root '{root}' does not exist.");
            }

            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Recurse covers scripts, compute, shader, material, mesh and editor tooling.
            // IncludeDependencies is deliberately off: it would drag in URP package assets.
            AssetDatabase.ExportPackage(ExportRoots, outputPath, ExportPackageOptions.Recurse);

            if (!File.Exists(outputPath))
                throw new IOException($"ExportPackage did not produce '{outputPath}'.");

            Debug.Log($"[SwarmPackageExporter] Exported {string.Join(", ", ExportRoots)} to {outputPath} " +
                      $"({new FileInfo(outputPath).Length / 1024f:F1} KB).");
            return outputPath;
        }

        // Environment resolves OneDrive-redirected Desktops correctly.
        private static string DefaultOutputPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), PackageFileName);

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }
    }
}
