using UnityEngine;
using UnityEditor;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System;
using System.Diagnostics;
using System.Security.Permissions;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

public class BuildLibunity : MonoBehaviour
{
    #pragma warning disable
    static readonly string PATCHER_WINDOWS_X64 = "https://security-patches.unity.com/bc0977e0-21a9-4f6e-9414-4f44b242110a/unity-patcher/UnityApplicationPatcher-1.3.3-Win.zip";
    static readonly string PATCHER_MAC_X64 = "https://security-patches.unity.com/bc0977e0-21a9-4f6e-9414-4f44b242110a/unity-patcher/UnityApplicationPatcher-1.3.3-macOS-x64.zip";
    static readonly string PATCHER_MAC_ARM64 = "https://security-patches.unity.com/bc0977e0-21a9-4f6e-9414-4f44b242110a/unity-patcher/UnityApplicationPatcher-1.3.3-macOS-Arm64.zip";
    static readonly string PATCHER_LINUX_X64 = "https://security-patches.unity.com/bc0977e0-21a9-4f6e-9414-4f44b242110a/unity-patcher/UnityApplicationPatcher-1.3.3-Linux.zip";

    static void MakeExecutable(string file)
    {
        try
        {
            var proc = new Process();
            proc.StartInfo.FileName = "chmod";
            proc.StartInfo.Arguments = $"+x '{file}'";
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
            proc.WaitForExit();
        }
        catch(Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to make '{file}' executable " + e.ToString());
            EditorUtility.ClearProgressBar();
            return;
        }
    }

    public static void ExtractWithExecutableBits(string zipPath, string destinationDir, Action<int, int> onExtract)
    {
        using (ZipArchive archive = ZipFile.OpenRead(zipPath))
        {
            int fileCount = archive.Entries.Count;
            int extracted = 0;
            onExtract(0, fileCount);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));

                if (entry.FullName.EndsWith("/"))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                entry.ExtractToFile(destinationPath, overwrite: true);

                int externalAttributes = entry.ExternalAttributes;
                int unixMode = externalAttributes >> 16;

                if (unixMode != 0)
                {
                    // Check if any executable bit is set (User, Group, or Others)
                    bool isExecutable = (unixMode & 0b001_001_001) != 0;

                    if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        MakeExecutable(destinationPath);
                    }
                }

                extracted += 1;
                onExtract(extracted, fileCount);
            }
        }
    }

    static string? GetPatcherURL()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return PATCHER_WINDOWS_X64;
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return PATCHER_LINUX_X64;
        if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if(RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return PATCHER_MAC_ARM64;
            if(RuntimeInformation.ProcessArchitecture == Architecture.X64) return PATCHER_MAC_X64;
        }
        return null;
    }

    static BuildTarget GetHostBuildTarget()
    {
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return BuildTarget.StandaloneWindows64;
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return BuildTarget.StandaloneLinux64;
        if(RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return BuildTarget.StandaloneOSX;
        throw new PlatformNotSupportedException("Unsupported host platform");
    }

    static string GetHostEngineLibraryName(BuildTarget target)
    {
        return target switch
        {
            BuildTarget.StandaloneWindows64 => "UnityPlayer.dll",
            BuildTarget.StandaloneLinux64 => "UnityPlayer.so",
            BuildTarget.StandaloneOSX => "UnityPlayer.dylib",
            _ => throw new PlatformNotSupportedException($"No known engine library for {target}"),
        };
    }

    static string FindFile(string rootDir, string fileName)
    {
        var match = Directory.EnumerateFiles(rootDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
        if (match == null)
        {
            throw new FileNotFoundException($"Could not find '{fileName}' under '{rootDir}'");
        }
        return match;
    }

    [MenuItem("Jobs/Build libunity.so (Host)")]
    static async void BuildHost()
    {
        await Build(GetHostBuildTarget());
    }

    [MenuItem("Jobs/Build libunity.so (Android)")]
    static async void BuildAndroid()
    {
        await Build(BuildTarget.Android);
    }

    static async Task Build(BuildTarget target)
    {
        // Pick the player output filename Unity expects for this target
        var outputName = target switch
        {
            BuildTarget.Android => "game.apk",
            BuildTarget.StandaloneWindows64 => "game.exe",
            _ => "game",
        };
        var outputFile = Path.Join(Application.dataPath, "..", outputName);
        var options = new BuildPlayerOptions
        {
            locationPathName = outputFile,
            target = target,
            options = BuildOptions.None,
        };
        // Build the player for the requested target
        if(target == BuildTarget.Android) EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Debugging;
        if(target == BuildTarget.Android) EditorUserBuildSettings.androidCreateSymbolsZip = true;
        var build = BuildPipeline.BuildPlayer(options);
        if(build.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            return;
        }

        if(target == BuildTarget.Android)
        {
            var buildDirectory = Path.GetDirectoryName(outputFile)!;
            var symbolsZip = Directory.EnumerateFiles(buildDirectory, "*.symbols.zip", SearchOption.TopDirectoryOnly)
                .Where(path => File.GetLastWriteTimeUtc(path) >= build.summary.buildStartedAt.ToUniversalTime())
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if(symbolsZip == null)
            {
                throw new FileNotFoundException($"Could not find the symbols package generated for '{outputFile}'");
            }

            using (ZipArchive symbolsArchive = ZipFile.OpenRead(symbolsZip))
            {
                var symbolsEntry = symbolsArchive.Entries.FirstOrDefault(entry =>
                    entry.Name == "libunity.so" || entry.Name == "libunity.sym.so" || entry.Name == "libunity.dbg.so" ||
                    entry.Name == "libunity.so.sym" || entry.Name == "libunity.so.dbg");
                if(symbolsEntry == null)
                {
                    throw new FileNotFoundException($"Could not find libunity symbols in '{symbolsZip}'");
                }
                symbolsEntry.ExtractToFile(Path.Join(Application.dataPath, "..", "libunity.sym.so"), overwrite: true);
            }

            // Android needs the extra patch-and-extract pass
            await PatchAndroidLibunity(outputFile);
        }
        else
        {
            // Other targets: just copy the engine library straight out of the build output
            var libraryPath = FindFile(Path.GetDirectoryName(outputFile)!, GetHostEngineLibraryName(target));
            File.Copy(libraryPath, Path.Join(Application.dataPath, "..", Application.unityVersion + ".so"), true);
        }
    }

    static async Task PatchAndroidLibunity(string apkOutputFile)
    {
        // Grab the unpatched libunity.so straight from the built apk
        var unpatchedLibunity = Path.Join(Application.dataPath, "..", Application.unityVersion + "-unpatched.so");
        using (ZipArchive apkArchive = ZipFile.OpenRead(apkOutputFile))
        {
            apkArchive.GetEntry("lib/arm64-v8a/libunity.so")!.ExtractToFile(unpatchedLibunity, overwrite: true);
        }

        var patcherDirectory = Path.Join(Application.dataPath, "..", "unity-application-patcher");
        var executableName = "UnityApplicationPatcherCLI" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
        var patcherExecutable = Path.Join(patcherDirectory, executableName);
        if(!File.Exists(patcherExecutable))
        {
            // Patcher tool isn't present yet, so fetch and unpack it first
            EditorUtility.DisplayProgressBar("Build libunity.so", "Downloading patcher tool", 0);
            var client = new HttpClient();
            var url = GetPatcherURL();
            if(url == null)
            {
                EditorUtility.ClearProgressBar();
                UnityEngine.Debug.LogError("Unsupported platform!");
                return;
            }
            var response = await client.GetAsync(url);
            if(response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                UnityEngine.Debug.LogError($"Downloading patcher returned status code {((int)response.StatusCode)}");
                return;
            }
            var archiveName = patcherDirectory + ".zip";
            var buffer = new byte[1000000];
            var stream = await response.Content.ReadAsStreamAsync();
            int bytesRead = 0;
            int totalBytesRead = 0;
            var file = new FileStream(archiveName, FileMode.CreateNew);
            // Stream the patcher zip to disk in chunks, reporting progress
            while((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, default).ConfigureAwait(false)) != 0)
            {
                await file.WriteAsync(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;
                EditorUtility.DisplayProgressBar("Build libunity.so", "Downloading patcher tool", totalBytesRead / (float)response.Content.Headers.ContentLength);
            }
            file.Close();
            stream.Close();
            // Unpack the patcher tool, preserving executable bits on Linux/macOS
            ExtractWithExecutableBits(archiveName, patcherDirectory, (extracted, total) => EditorUtility.DisplayProgressBar("Build libunity.so", "Extracting patcher tool", (float)extracted / (float)total));
            File.Delete(archiveName);
            EditorUtility.ClearProgressBar();
        }

        EditorUtility.DisplayProgressBar("Build libunity.so", "Patching apk", .5f);
        // zipalign fails to find libc++.so otherwise
        System.Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", Path.Join(patcherDirectory, "Data/StreamingAssets/Android/SDK/platform-tools/lib64"));
        var process = new Process();
        process.StartInfo.FileName = patcherExecutable;
        process.StartInfo.UseShellExecute = true;
        // versionCode 0 means ignore
        process.StartInfo.Arguments = $"-android -versionCode 0 -applicationPath \"{apkOutputFile}\"";
        EditorUtility.DisplayProgressBar("Build libunity.so", "Patching apk", .5f);
        try
        {
            UnityEngine.Debug.Log(process.StartInfo.FileName);
            UnityEngine.Debug.Log(process.StartInfo.Arguments);
            // Run the patcher CLI against the built apk, producing game.patched.apk
            process.Start();
            process.WaitForExit();
            UnityEngine.Debug.Log($"Process exited with code {process.ExitCode}");
        }
        catch(Exception e)
        {
            UnityEngine.Debug.LogError(e.ToString());
            EditorUtility.ClearProgressBar();
            return;
        }
        EditorUtility.ClearProgressBar();

        EditorUtility.DisplayProgressBar("Build libunity.so", "Extracting file from apk", .8f);
        var patchedApk = Path.Join(Application.dataPath, "..", "game.patched.apk");
        var patchedLibunity = Path.Join(Application.dataPath, "..", Application.unityVersion + ".so");
        // Grab the patched libunity.so out of the patcher's output apk
        using (ZipArchive patchedArchive = ZipFile.OpenRead(patchedApk))
        {
            patchedArchive.GetEntry("lib/arm64-v8a/libunity.so")!.ExtractToFile(patchedLibunity, overwrite: true);
        }
        EditorUtility.ClearProgressBar();
    }
}
