using UnityEngine;
using UnityEditor;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System;
using System.Diagnostics;
using System.Security.Permissions;
using System.IO.Compression;

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

    [MenuItem("Jobs/Build libunity.so")]
    static async void Build()
    {
        var apkOutputFile = Path.Join(Application.dataPath, "..", "game.apk");
        var options = new BuildPlayerOptions
        {
            locationPathName = apkOutputFile,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };
        var build = BuildPipeline.BuildPlayer(options);
        if(build.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            return;
        }

        var patcherDirectory = Path.Join(Application.dataPath, "..", "unity-application-patcher");
        var executableName = "UnityApplicationPatcherCLI" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
        var patcherExecutable = Path.Join(patcherDirectory, executableName);
        if(!File.Exists(patcherExecutable))
        {
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
            while((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, default).ConfigureAwait(false)) != 0)
            {
                await file.WriteAsync(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;
                EditorUtility.DisplayProgressBar("Build libunity.so", "Downloading patcher tool", totalBytesRead / (float)response.Content.Headers.ContentLength);
            }
            file.Close();
            stream.Close();
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
        process.StartInfo.Arguments = $"-android -versionCode 0 -applicationPath '{apkOutputFile}'";
        EditorUtility.DisplayProgressBar("Build libunity.so", "Patching apk", .5f);
        try
        {
            UnityEngine.Debug.Log(process.StartInfo.FileName);
            UnityEngine.Debug.Log(process.StartInfo.Arguments);
            process.Start();
            process.WaitForExit();
            UnityEngine.Debug.Log($"Process exited with code {process.ExitCode}");
        }
        catch(Exception e)
        {
            UnityEngine.Debug.LogError(e.ToString());
            EditorUtility.ClearProgressBar();
        }
        EditorUtility.ClearProgressBar();

        EditorUtility.DisplayProgressBar("Build libunity.so", "Extracting file from apk", .8f);
        var apkExtractedFolder = Path.Join(Application.dataPath, "..", "game");
        ZipFile.ExtractToDirectory(Path.Join(Application.dataPath, "..", "game.patched.apk"), apkExtractedFolder, true);
        File.Copy(Path.Join(apkExtractedFolder, "lib/arm64-v8a/libunity.so"), Path.Join(Application.dataPath, "..", Application.unityVersion + ".so"), true);
        Directory.Delete(apkExtractedFolder, true);
        EditorUtility.ClearProgressBar();
    }
}
