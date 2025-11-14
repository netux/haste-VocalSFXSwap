using Landfall.Haste;
using Landfall.Modding;
using Newtonsoft.Json;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace HasteVocalSfxSwapMod;

[LandfallPlugin]
public class VocalSfxSwapMod
{
    private static readonly FieldInfo[] vocalBankSfxInstanceFields = typeof(VocalBank).GetFields()
        .Where((field) => field.FieldType == typeof(SFX_Instance))
        .ToArray();

    public static Dictionary<int, VocalSfxSwapSkinConfig> skinConfigs = [];
    public static Dictionary<int, VocalBank> skinVocalBankCache = [];

    private static Dictionary<string, AudioClip> pathToAudioClipCache = [];

    private static VocalBank? baseZoeVocalBank;
    public static VocalBank? BaseZoeVocalBank {
        get { return baseZoeVocalBank; }
    }

    private static readonly SemaphoreSlim replaceVocalsSemaphore = new(1, 1);

    static VocalSfxSwapMod()
    {
        FactSystem.SubscribeToFact(
            SkinManager.EquippedSkinBodyFact,
            async (skinIndex) => await ReplaceVocalsMaybe((int) skinIndex)
        );

        SceneManager.sceneLoaded += async (newScene, mode) =>
        {
            if (mode != LoadSceneMode.Single)
            {
                return;
            }

            if (BaseZoeVocalBank == null)
            {
                var skinIndex = (int)FactSystem.GetFact(SkinManager.EquippedSkinBodyFact);
                await ReplaceVocalsMaybe(skinIndex);
            }
        };

        foreach (var item in Modloader.LoadedItemDirectories.Values)
        {
            LoadVocalSfxs(item.directory);
        }

        Modloader.OnItemLoaded += (item) =>
        {
            if (Modloader.GetDirectoryFromFileId(item, out var directory, out var _isOverride))
            {
                LoadVocalSfxs(directory);
            }
        };
    }

    private static string VocalBankFieldToSfxName(FieldInfo field)
    {
        return field.Name.Remove(field.Name.IndexOf("Vocals"), "Vocals".Length);
    }

    private static void LoadVocalSfxs(string directory)
    {
        Dictionary<int, Dictionary<string, List<string>>> foundSoundFilesPerSkinPerSfx = [];

        void TryStoreWavFile(string wavFilePath)
        {
            // (name).(skin index).(sfx name).wav
            // (name).(skin index).(sfx name).(number).wav
            var split = Path.GetFileName(wavFilePath).Split(".");

            if (split.Length < 4 || split.Length > 5)
            {
                return;
            }

            if (!int.TryParse(split[1], out int skinIndex))
            {
                return;
            }

            var sfxNameInFileName = split[2];

            Dictionary<string, List<string>> replacements;
            if (!foundSoundFilesPerSkinPerSfx.ContainsKey(skinIndex))
            {
                replacements = [];
                foundSoundFilesPerSkinPerSfx.Add(skinIndex, replacements);
            }
            else
            {
                replacements = foundSoundFilesPerSkinPerSfx[skinIndex];
            }

            foreach (var field in vocalBankSfxInstanceFields)
            {
                var sfxName = VocalBankFieldToSfxName(field);
                if (sfxNameInFileName.ToLower() == sfxName.ToLower())
                {
                    List<string> clipPaths;
                    if (replacements.ContainsKey(field.Name))
                    {
                        clipPaths = replacements[field.Name];
                    }
                    else
                    {
                        clipPaths = [];
                        replacements[sfxName] = clipPaths;
                    }

                    clipPaths.Add(wavFilePath);
                    break;
                }
            }
        }

        void TryLoadConfigFile(string configFilePath)
        {
            // (name).(skin index).hastevocalssfx.json
            var split = Path.GetFileName(configFilePath).Split(".");

            if (split.Length != 4)
            {
                return;
            }

            if (!int.TryParse(split[1], out int skinIndex))
            {
                return;
            }

            using StreamReader file = File.OpenText(configFilePath);

            try
            {
                VocalSfxSwapSkinConfig? skinConfig = new JsonSerializer()
                    .Deserialize<VocalSfxSwapSkinConfig>(new JsonTextReader(file));
                if (skinConfig == null)
                {
                    // Not sure when this can happen...
                    return;
                }

                var configDirectoryBasePath = Path.GetDirectoryName(configFilePath);

                if (skinConfig.Swaps != null)
                {
                    foreach (var swapConfig in skinConfig.Swaps.Values)
                    {
                        if (swapConfig.Clips == null)
                        {
                            continue;
                        }

                        // Post-process: Resolve all paths
                        swapConfig.Clips = swapConfig.Clips
                            .Select(soundFilePath =>
                            {
                                if (Path.IsPathFullyQualified(soundFilePath))
                                {
                                    return soundFilePath;
                                }

                                string[] paths = [];
                                if (skinConfig.BasePath != null)
                                {
                                    paths = [..paths,  skinConfig.BasePath];
                                }
                                if (swapConfig.BasePath != null)
                                {
                                    paths = [.. paths, swapConfig.BasePath];
                                }

                                paths = [configDirectoryBasePath, ..paths, soundFilePath];

                                return Path.Combine(paths);
                            })
                            .ToArray();
                    }
                }

                skinConfigs.Add(skinIndex, skinConfig);
                Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loaded vocal sfx config file for skin {skinIndex}: {configFilePath}");
            }
            catch (JsonException error)
            {
                Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not parse Vocal SFX configuration: {error}");
            }
        }

        foreach (var filePath in Directory.GetFiles(directory))
        {
            if (filePath.EndsWith(".wav"))
            {
                TryStoreWavFile(filePath);
            }
            else if (filePath.EndsWith(".hastevocalssfx.json"))
            {
                TryLoadConfigFile(filePath);
            }
        }

        foreach (var skinIndex in foundSoundFilesPerSkinPerSfx.Keys)
        {
            Dictionary<string, SfxInstanceSwapConfig> swaps = [];
            if (skinConfigs.ContainsKey(skinIndex) && skinConfigs[skinIndex].Swaps == null)
            {
                skinConfigs[skinIndex].Swaps = swaps;
            }
            else if (!skinConfigs.ContainsKey(skinIndex))
            {
                var skinConfig = new VocalSfxSwapSkinConfig()
                {
                    Swaps = swaps
                };
                skinConfigs.Add(skinIndex, skinConfig);
            }
            else
            {
                // Skin config overwrites sound files found. Ignore the sound files.
                continue;
            }

            foreach (var sfxName in foundSoundFilesPerSkinPerSfx[skinIndex].Keys)
            {
                SfxInstanceSwapConfig config;
                if (swaps.ContainsKey(sfxName))
                {
                    config = swaps[sfxName];
                    if (config.Clips != null)
                    {
                        continue;
                    }
                }
                else
                {
                    config = new();
                    swaps[sfxName] = config;
                }

                config.Clips = foundSoundFilesPerSkinPerSfx[skinIndex][sfxName].ToArray();

                Debug.Log($"[{nameof(VocalSfxSwapMod)}] Adding individual vocal sfx sound files for sfx {sfxName}, skin {skinIndex}:\n{
                    string.Join("\n",
                        foundSoundFilesPerSkinPerSfx[skinIndex][sfxName]
                            .Select((filePath) => $"- {filePath}")
                    )
                }");
            }
        }
    }

    private static async Task<AudioClip?> LoadAudioClipFromPath(string path)
    {
        using UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(path, AudioType.WAV);
        await uwr.SendWebRequest();

        if (uwr.result == UnityWebRequest.Result.ConnectionError || uwr.result == UnityWebRequest.Result.DataProcessingError)
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Could not load audio clip at \"{path}\": {uwr.error}");
            return null;
        }

        return DownloadHandlerAudioClip.GetContent(uwr);
    }

    public static async Task ReplaceVocalsMaybe(int skinIndex)
    {
        await replaceVocalsSemaphore.WaitAsync();

        try
        {
            await ReplaceVocals(skinIndex);
        }
        catch (Exception error)
        {
            Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not replace vocals: {error}");
        }
        finally
        {
            replaceVocalsSemaphore.Release();
        }
    }

    private static async Task ReplaceVocals(int skinIndex)
    {
        if (PlayerVocalSFX.Instance == null)
        {
            //Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not replace vocals for skin {skinIndex}: could not find PlayerVocalSFX instance");
            return;
        }

        if (baseZoeVocalBank == null)
        {
            baseZoeVocalBank = PlayerVocalSFX.Instance.vocalBank;
        }

        if (!skinConfigs.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default VocalBank");
            PlayerVocalSFX.Instance.vocalBank = baseZoeVocalBank;
            return;
        }

        var skinConfig = skinConfigs[skinIndex];

        VocalBank skinVocalBank;
        if (!skinVocalBankCache.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin vocal bank not found in cache. Creating a new one...");

            skinVocalBank = await GenerateSkinVocalBank(baseZoeVocalBank, skinIndex, skinConfig);
            skinVocalBankCache[skinIndex] = skinVocalBank;
        }
        else
        {
            skinVocalBank = skinVocalBankCache[skinIndex];
        }

        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Set new skin vocal bank: {skinVocalBank}");
        PlayerVocalSFX.Instance.vocalBank = skinVocalBank;
    }

    public static async Task<VocalBank> GenerateSkinVocalBank(int skinIndex, VocalSfxSwapSkinConfig config)
    {
        if (BaseZoeVocalBank == null)
        {
            throw new Exception("Missing Zoe's base vocal bank");
        }

        return await GenerateSkinVocalBank(BaseZoeVocalBank, skinIndex, config);
    }

    public static async Task<VocalBank> GenerateSkinVocalBank(VocalBank baseVocalBank, int skinIndex, VocalSfxSwapSkinConfig config)
    {
        var vocalBank = ScriptableObject.CreateInstance<VocalBank>();
        vocalBank.name = $"{baseVocalBank.name} Skin {skinIndex}";

        // Copy all SFX_Instances from Zoe's base vocal bank to the new voice bank
        foreach (var fieldInfo in typeof(VocalBank).GetFields())
        {
            if (fieldInfo.FieldType != typeof(SFX_Instance))
            {
                continue;
            }

            fieldInfo.SetValue(vocalBank, fieldInfo.GetValue(baseVocalBank));
        }

        if (config.Swaps == null)
        {
            // What's the point? lol
            return vocalBank;
        }

        // Create new SFX_Instances for replacement SFX clips
        foreach (var field in vocalBankSfxInstanceFields)
        {
            var sfxName = VocalBankFieldToSfxName(field);
            if (!config.Swaps.ContainsKey(sfxName))
            {
                //Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin {skinIndex} has no {sfxName} replacements");
                continue;
            }

            var oldSfxInstance = (SFX_Instance)field.GetValue(vocalBank);

            var newSfxInstance = ScriptableObject.CreateInstance<SFX_Instance>();
            field.SetValue(vocalBank, newSfxInstance);

            newSfxInstance.name = $"{oldSfxInstance.name} Skin {skinIndex}";
            newSfxInstance.settings = oldSfxInstance.settings;
            newSfxInstance.lastTimePlayed = oldSfxInstance.lastTimePlayed;

            var sfxInstanceConfig = config.Swaps[sfxName];

            if (sfxInstanceConfig.Settings != null)
            {
                newSfxInstance.settings = new SFX_Settings();

                foreach (var settingField in typeof(SFX_Settings).GetFields())
                {
                    settingField.SetValue(newSfxInstance.settings, settingField.GetValue(oldSfxInstance.settings));
                }

                newSfxInstance.settings.volume *= sfxInstanceConfig.Settings.VolumeMultiplier;
            }

            List<AudioClip> clips = [];
            foreach (var path in sfxInstanceConfig.Clips ?? [])
            {
                var fullPath = Path.GetFullPath(path);

                AudioClip? clip;
                if (pathToAudioClipCache.ContainsKey(fullPath))
                {
                    clip = pathToAudioClipCache[fullPath];
                }
                else
                {
                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loading new AudioClip {path}");

                    var loadedClip = await LoadAudioClipFromPath(path);
                    if (loadedClip == null)
                    {
                        continue;
                    }

                    clip = loadedClip;
                    clip.name = $"VocalSfx {Path.GetFileNameWithoutExtension(fullPath)}";

                    pathToAudioClipCache.Add(fullPath, clip);

                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Successfully loaded AudioClip {path}");

                }

                if (clip == null)
                {
                    continue;
                }

                clips.Add(clip);
            }
            newSfxInstance.clips = clips.ToArray();
        }

        return vocalBank;
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void ListVocalSfxNames()
    {
        Debug.Log(string.Join("\n", vocalBankSfxInstanceFields.Select((field) => $"- {VocalBankFieldToSfxName(field)}")));
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void StartLoggingVocalSfxPlayed()
    {
        On.PlayerVocalSFX.Play += hook_PlayerVocalsSFXPlayLog;
        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Will start logging played vocal SFX");
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void StopLoggingVocalSfxPlayed()
    {
        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Will stop logging played vocal SFX");
        On.PlayerVocalSFX.Play -= hook_PlayerVocalsSFXPlayLog;
    }

    private static void hook_PlayerVocalsSFXPlayLog(
        On.PlayerVocalSFX.orig_Play original,
        PlayerVocalSFX playerVocalSfx,
        SFX_Instance sfx,
        PlayerVocalSFX.Priority priority,
        float fadeTime
    )
    {
        Debug.Log($"[{nameof(VocalSfxSwapMod)}] [DEBUG] Playing {sfx.name}");
        original(playerVocalSfx, sfx, priority, fadeTime);
    }
}

public record SfxInstanceSwapConfig
{
    [JsonProperty("basePath")]
    public string? BasePath;
    [JsonProperty("clips")]
    public string[]? Clips;
    [JsonProperty("settings")]
    public SfxSettingsSwap? Settings;
}

public record SfxSettingsSwap
{
    [JsonProperty("volumeMultiplier")]
    public float VolumeMultiplier = 1f;
}

public record VocalSfxSwapSkinConfig
{
    [JsonProperty("basePath")]
    public string? BasePath;
    [JsonProperty("swaps")]
    public Dictionary<string, SfxInstanceSwapConfig>? Swaps;
}