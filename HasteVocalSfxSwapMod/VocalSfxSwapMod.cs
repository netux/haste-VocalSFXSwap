using Landfall.Haste;
using Landfall.Modding;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
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
    private static readonly FieldInfo[] interactionVocalBankSfxInstanceFields = typeof(InteractionVocalBank).GetFields()
        .Where((field) => field.FieldType == typeof(SFX_Instance))
        .ToArray();

    public static readonly ReadOnlyDictionary<string, SupportedAudioFormat> SupportedAudioFormats = new(
        new Dictionary<string, SupportedAudioFormat>()
        {
            [".wav"] = new(UnityFormat: AudioType.WAV),
            [".ogg"] = new(UnityFormat: AudioType.OGGVORBIS),
            [".mp3"] = new(UnityFormat: AudioType.MPEG),
        }
    );

    public static readonly Dictionary<int, VocalSfxSwapSkinConfig> skinConfigs = [];

    public static readonly Dictionary<int, VocalBank> skinVocalBankCache = [];
    public static readonly Dictionary<int, InteractionVocalBank> skinInteractionVocalBankCache = [];

    private static readonly Dictionary<string, AudioClip> pathToAudioClipCache = [];

    private static VocalBank? baseZoeVocalBank;
    public static VocalBank? BaseZoeVocalBank
    {
        get { return baseZoeVocalBank; }
    }

    private static InteractionVocalBank? baseZoeInteractionVocalBank;
    public static InteractionVocalBank? BaseZoeInteractionVocalBank
    {
        get { return baseZoeInteractionVocalBank; }
    }

    private static readonly SemaphoreSlim replaceVocalsSemaphore = new(1, 1);
    private static readonly SemaphoreSlim replaceInteractionVocalsSemaphore = new(1, 1);

    static VocalSfxSwapMod()
    {
        FactSystem.SubscribeToFact(
            SkinManager.EquippedSkinBodyFact,
            async (skinIndex) => await ReplaceAllVocals((int) skinIndex)
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
                await ReplaceAllVocals(skinIndex);
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

    private static string InteractionVocalBankFieldToSfxName(FieldInfo field)
    {
        return $"interaction{field.Name.Substring(0, 1).ToUpper()}{field.Name.Substring(1)}";
    }

    private static void LoadVocalSfxs(string directory)
    {
        Dictionary<int, Dictionary<string, List<string>>> foundSoundFilesPerSkinPerSfx = [];

        void TryStoreAudioFile(string audioFilePath)
        {
            // (name).(skin index).(sfx name).(ext)
            // (name).(skin index).(sfx name).(number).(ext)
            var split = Path.GetFileName(audioFilePath).Split(".");


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

                    clipPaths.Add(audioFilePath);
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
            if (SupportedAudioFormats.ContainsKey(Path.GetExtension(filePath)))
            {
                TryStoreAudioFile(filePath);
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

    public static async Task ReplaceAllVocals(int skinIndex)
    {
        await Task.WhenAll([
            ReplaceVocals(skinIndex),
            ReplaceInteractionVocals(skinIndex)
        ]);
    }

    public static async Task ReplaceVocals(int skinIndex)
    {
        await replaceVocalsSemaphore.WaitAsync();

        try
        {
            await ForceReplaceVocals(skinIndex);
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

    private static async Task ForceReplaceVocals(int skinIndex)
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
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default vocal bank");
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
                continue;
            }

            var swapConfig = config.Swaps[sfxName];

            var oldSfxInstance = (SFX_Instance)field.GetValue(vocalBank);

            SFX_Instance sfxInstance = await GenerateSfxInstance(oldSfxInstance, skinIndex, swapConfig);
            field.SetValue(vocalBank, sfxInstance);
        }

        UnityEngine.Object.DontDestroyOnLoad(vocalBank);

        return vocalBank;
    }


    public static async Task ReplaceInteractionVocals(int skinIndex)
    {
        await replaceInteractionVocalsSemaphore.WaitAsync();

        try
        {
            await ForceReplaceInteractionVocals(skinIndex);
        }
        catch (Exception error)
        {
            Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not replace vocals: {error}");
        }
        finally
        {
            replaceInteractionVocalsSemaphore.Release();
        }
    }

    private static async Task ForceReplaceInteractionVocals(int skinIndex)
    {
        if (baseZoeInteractionVocalBank == null)
        {
            baseZoeInteractionVocalBank = InteractionCharacterDatabase.Instance.courier.VocalBank;
        }

        if (!skinConfigs.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default interaction vocal bank");
            InteractionCharacterDatabase.Instance.courier.VocalBank = baseZoeInteractionVocalBank;
            return;
        }

        var skinConfig = skinConfigs[skinIndex];

        InteractionVocalBank skinInteractionVocalBank;
        if (!skinInteractionVocalBankCache.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin interaction vocal bank not found in cache. Creating a new one...");

            skinInteractionVocalBank = await GenerateSkinInteractionVocalBank(baseZoeInteractionVocalBank, skinIndex, skinConfig);
            skinInteractionVocalBankCache[skinIndex] = skinInteractionVocalBank;
        }
        else
        {
            skinInteractionVocalBank = skinInteractionVocalBankCache[skinIndex];
        }

        InteractionCharacterDatabase.Instance.courier.VocalBank = skinInteractionVocalBank;
    }

    public static async Task<InteractionVocalBank> GenerateSkinInteractionVocalBank(int skinIndex, VocalSfxSwapSkinConfig config)
    {
        if (BaseZoeInteractionVocalBank == null)
        {
            throw new Exception("Missing Zoe's base interaction vocal bank");
        }

        return await GenerateSkinInteractionVocalBank(BaseZoeInteractionVocalBank, skinIndex, config);
    }

    public static async Task<InteractionVocalBank> GenerateSkinInteractionVocalBank(InteractionVocalBank baseInteractionVocalBank, int skinIndex, VocalSfxSwapSkinConfig config)
    {
        var interactionVocalBank = ScriptableObject.CreateInstance<InteractionVocalBank>();
        interactionVocalBank.name = $"{baseInteractionVocalBank.name} Skin {skinIndex}";

        // Copy all SFX_Instances from Zoe's base interaction vocal bank to the new interaction voice bank
        foreach (var fieldInfo in interactionVocalBankSfxInstanceFields)
        {
            fieldInfo.SetValue(interactionVocalBank, fieldInfo.GetValue(baseInteractionVocalBank));
        }

        if (config.Swaps == null)
        {
            // What's the point? lol
            return interactionVocalBank;
        }

        // Create new SFX_Instances for replacement SFX clips
        foreach (var field in interactionVocalBankSfxInstanceFields)
        {
            var sfxName = InteractionVocalBankFieldToSfxName(field);
            if (!config.Swaps.ContainsKey(sfxName))
            {
                continue;
            }

            var swapConfig = config.Swaps[sfxName];

            var oldSfxInstance = (SFX_Instance)field.GetValue(interactionVocalBank);

            SFX_Instance sfxInstance = await GenerateSfxInstance(oldSfxInstance, skinIndex, swapConfig);
            field.SetValue(interactionVocalBank, sfxInstance);
        }

        UnityEngine.Object.DontDestroyOnLoad(interactionVocalBank);

        return interactionVocalBank;
    }

    private static async Task<AudioClip?> LoadAudioClipFromPath(string path)
    {
        if (!SupportedAudioFormats.TryGetValue(Path.GetExtension(path), out SupportedAudioFormat format))
        {
            throw new Exception("Unsupported file extension");
        }

        using UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(path, format.UnityFormat);
        await uwr.SendWebRequest();

        if (uwr.result == UnityWebRequest.Result.ConnectionError || uwr.result == UnityWebRequest.Result.DataProcessingError)
        {
            throw new Exception(uwr.error);
        }

        return DownloadHandlerAudioClip.GetContent(uwr);
    }

    private static async Task<SFX_Instance> GenerateSfxInstance(SFX_Instance baseSfxInstance, int skinIndex, SfxInstanceSwapConfig config)
    {
        var sfxInstance = ScriptableObject.CreateInstance<SFX_Instance>();

        sfxInstance.name = $"{baseSfxInstance.name} Skin {skinIndex}";
        sfxInstance.settings = baseSfxInstance.settings;
        sfxInstance.lastTimePlayed = baseSfxInstance.lastTimePlayed;

        if (config.Settings != null)
        {
            sfxInstance.settings = new SFX_Settings();

            foreach (var settingField in typeof(SFX_Settings).GetFields())
            {
                settingField.SetValue(sfxInstance.settings, settingField.GetValue(baseSfxInstance.settings));
            }

            sfxInstance.settings.volume *= config.Settings.VolumeMultiplier;
        }

        List<AudioClip> clips = [];
        foreach (var path in config.Clips ?? [])
        {
            var fullPath = Path.GetFullPath(path);

            AudioClip? clip = null;

            if (pathToAudioClipCache.ContainsKey(fullPath))
            {
                clip = pathToAudioClipCache[fullPath];
            }
            else
            {
                Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loading new AudioClip {path}");

                try
                {
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
                catch (Exception error)
                {
                    Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not load AudioClip {path}: {error}");
                }
            }

            if (clip == null)
            {
                continue;
            }

            clips.Add(clip);
        }
        sfxInstance.clips = clips.ToArray();

        return sfxInstance;
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void ListVocalSfxNames()
    {
        Debug.Log(
            string.Join("\n", vocalBankSfxInstanceFields
                .Select(VocalBankFieldToSfxName)
                .Concat(
                    interactionVocalBankSfxInstanceFields.Select(InteractionVocalBankFieldToSfxName)
                )
                .OrderBy((name) => name) // alphabetical
                .Select((name) => $"- {name}")
            )
        );
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void StartLoggingVocalSfxPlayed()
    {
        On.PlayerVocalSFX.Play += hook_PlayerVocalsSFXPlayLog;
        On.InteractionVocalPlayer.PlaySFX += hook_InteractionVocalPlayerPlaySFXLog;
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
        Debug.Log($"[{nameof(VocalSfxSwapMod)}] [DEBUG] Playing vocal SFX \"{sfx.name}\"");
        original(playerVocalSfx, sfx, priority, fadeTime);
    }

    private static void hook_InteractionVocalPlayerPlaySFXLog(
        On.InteractionVocalPlayer.orig_PlaySFX original,
        InteractionVocalPlayer interactionVocalPlayer,
        SFX_Instance sfx
    )
    {
        Debug.Log($"[{nameof(VocalSfxSwapMod)}] [DEBUG] Playing interaction vocal SFX \"{sfx.name}\"");
        original(interactionVocalPlayer, sfx);
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

public record SupportedAudioFormat(AudioType UnityFormat);