using HasteVocalSfxSwapMod.Model;
using Landfall.Haste;
using Landfall.Modding;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace HasteVocalSfxSwapMod;

[LandfallPlugin]
public class VocalSfxSwapMod
{
    public static readonly ReadOnlyDictionary<string, SupportedAudioFormat> SupportedAudioFormats = new(
        new Dictionary<string, SupportedAudioFormat>()
        {
            [".wav"] = new(UnityFormat: AudioType.WAV),
            [".ogg"] = new(UnityFormat: AudioType.OGGVORBIS),
            [".mp3"] = new(UnityFormat: AudioType.MPEG),
        }
    );

    private static VocalSfxSwapConfig? defaultConfig = null;
    public static VocalSfxSwapConfig? DefaultConfig
    {
        get => defaultConfig;
    }

    private static readonly Dictionary<int, VocalSfxSwapConfig> skinConfigs = [];
    public static ReadOnlyDictionary<int, VocalSfxSwapConfig> SkinConfigs
    {
        get => new ReadOnlyDictionary<int, VocalSfxSwapConfig>(skinConfigs);
    }

    protected static readonly Dictionary<int?, VocalBank> vocalBankCache = [];
    protected static readonly Dictionary<int?, InteractionVocalBank> interactionVocalBankCache = [];

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
            RegisterConfigsInDirectory(item.directory);
        }

        Modloader.OnItemLoaded += (item) =>
        {
            if (Modloader.GetDirectoryFromFileId(item, out var directory, out var _isOverride))
            {
                RegisterConfigsInDirectory(directory);
            }
        };
    }

    public static void RegisterConfigsInDirectory(string modDirectory)
    {
        HashSet<int> cacheIndicesToReload = [];
        // NOTE: Use ValueTuple to bypass Dictionary keys not liking to be nullable.
        // Its dumb, but it works: https://stackoverflow.com/a/66432902
        Dictionary<ValueTuple<int?>, Dictionary<string, List<string>>> foundSoundFilesPerSkinPerSfx = [];

        void TryStoreAudioFile(string audioFilePath)
        {
            // (friendly name).(skin index or 'default').(sfx name).(extension)
            // (friendly name).(skin index or 'default').(sfx name).(number).(extension)
            var split = Path.GetFileName(audioFilePath).Split(".");


            if (split.Length < 4 || split.Length > 5)
            {
                return;
            }

            int? skinIndex = null;
            if (split[1] != "default")
            {
                if (!int.TryParse(split[1], out int parsedSkinIndex))
                {
                    return;
                }

                skinIndex = parsedSkinIndex;
            }

            var sfxNameInFileName = split[2];

            var skinIndexKey = ValueTuple.Create(skinIndex);

            Dictionary<string, List<string>> replacements;
            if (!foundSoundFilesPerSkinPerSfx.ContainsKey(skinIndexKey))
            {
                replacements = [];
                foundSoundFilesPerSkinPerSfx.Add(skinIndexKey, replacements);
            }
            else
            {
                replacements = foundSoundFilesPerSkinPerSfx[skinIndexKey];
            }

            foreach (var field in Util.VocalBankSfxInstanceFields)
            {
                var sfxName = Util.VocalBankFieldToSfxName(field);
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
            // (friendly name).(skin index or 'default').hastevocalsfx.json
            var split = Path.GetFileName(configFilePath).Split(".");

            if (split.Length != 4)
            {
                return;
            }

            int? skinIndex = null;
            if (split[1] != "default")
            {
                if (!int.TryParse(split[1], out int parsedSkinIndex))
                {
                    return;
                }

                skinIndex = parsedSkinIndex;
            }

            using StreamReader file = File.OpenText(configFilePath);

            try
            {
                VocalSfxSwapConfig? config = new JsonSerializer()
                    .Deserialize<VocalSfxSwapConfig>(new JsonTextReader(file));
                if (config == null)
                {
                    // Not sure when this can happen...
                    return;
                }

                config.SkinIndex = skinIndex;

                var configDirectoryBasePath = Path.GetDirectoryName(configFilePath);

                if (config.Swaps != null)
                {
                    foreach (var swapConfig in config.Swaps.Values)
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
                                if (config.BasePath != null && config.BasePath != "")
                                {
                                    paths = [.. paths, config.BasePath];
                                }
                                if (swapConfig.BasePath != null && swapConfig.BasePath != "")
                                {
                                    paths = [.. paths, swapConfig.BasePath];
                                }

                                paths = [configDirectoryBasePath, .. paths, soundFilePath];
                                paths = paths.Select(Util.NormalizePathForCurrentPlatform).ToArray();

                                return Path.Combine(paths);
                            })
                            .ToArray();
                    }
                }

                if (skinIndex.HasValue)
                {
                    skinConfigs.Add(skinIndex.Value, config);
                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loaded vocal sfx config file for skin {skinIndex}: {configFilePath}");
                }
                else
                {
                    defaultConfig = config;
                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loaded default vocal sfx config file: {configFilePath}");
                }

                cacheIndicesToReload.Add(Util.SkinIndexToCacheIndex(skinIndex));
            }
            catch (JsonException error)
            {
                Debug.LogError($"[{nameof(VocalSfxSwapMod)}] Could not parse Vocal SFX configuration: {error}");
            }
        }

        foreach (var filePath in Directory.GetFiles(modDirectory))
        {
            if (SupportedAudioFormats.ContainsKey(Path.GetExtension(filePath)))
            {
                TryStoreAudioFile(filePath);
            }
            else if (filePath.EndsWith(".hastevocalsfx.json"))
            {
                TryLoadConfigFile(filePath);
            }
        }

        foreach (var skinIndexKey in foundSoundFilesPerSkinPerSfx.Keys)
        {
            var skinIndex = skinIndexKey.Item1;

            Dictionary<string, SfxInstanceSwapConfig> swaps = [];

            if (skinIndex.HasValue)
            {
                if (skinConfigs.ContainsKey(skinIndex.Value) && skinConfigs[skinIndex.Value].Swaps == null)
                {
                    skinConfigs[skinIndex.Value].Swaps = swaps;
                }
                else if (!skinConfigs.ContainsKey(skinIndex.Value))
                {
                    var skinConfig = new VocalSfxSwapConfig()
                    {
                        SkinIndex = skinIndex.Value,
                        Swaps = swaps
                    };
                    skinConfigs.Add(skinIndex.Value, skinConfig);
                }
                else
                {
                    // Skin config overwrites individual sound files found this skin. Ignore the sound files.
                    continue;
                }
            }
            else
            {
                if (defaultConfig == null)
                {
                    defaultConfig = new VocalSfxSwapConfig()
                    {
                        SkinIndex = null,
                        Swaps = swaps
                    };
                }
                else if (defaultConfig.Swaps == null)
                {
                    defaultConfig.Swaps = swaps;
                }
                else
                {
                    // Default config overwrites individual sound files found for "default". Ignore the sound files.
                    continue;
                }
            }

            foreach (var sfxName in foundSoundFilesPerSkinPerSfx[skinIndexKey].Keys)
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

                config.Clips = foundSoundFilesPerSkinPerSfx[skinIndexKey][sfxName].ToArray();

                Debug.Log($"[{nameof(VocalSfxSwapMod)}] Adding individual vocal sfx sound files for sfx {sfxName}, skin {skinIndexKey}:\n{string.Join("\n",
                        foundSoundFilesPerSkinPerSfx[skinIndexKey][sfxName]
                            .Select((filePath) => $"- {filePath}")
                    )}");
            }

            cacheIndicesToReload.Add(Util.SkinIndexToCacheIndex(skinIndex));
        }

        foreach (var skinIndex in cacheIndicesToReload)
        {
            vocalBankCache.Remove(skinIndex);
            interactionVocalBankCache.Remove(skinIndex);
        }
    }

    public static void RegisterConfig(VocalSfxSwapConfig config)
    {
        if (config.SkinIndex.HasValue)
        {
            skinConfigs[config.SkinIndex.Value] = config;

        }
        else
        {
            defaultConfig = config;
        }

        vocalBankCache.Remove(config.SkinIndex);
        interactionVocalBankCache.Remove(config.SkinIndex);

    }

    public static async Task ReplaceAllVocals(int skinIndex)
    {
        VocalSfxSwapConfig? config = skinConfigs.GetValueOrDefault(skinIndex) ?? defaultConfig;

        await ReplaceVocals(config);
        await ReplaceInteractionVocals(config);
    }

    public static async Task ReplaceAllVocals(VocalSfxSwapConfig? config)
    {
        await Task.WhenAll([
            ReplaceVocals(config),
            ReplaceInteractionVocals(config)
        ]);
    }

    public static async Task ReplaceVocals(VocalSfxSwapConfig? config)
    {
        await replaceVocalsSemaphore.WaitAsync();

        try
        {
            await ForceReplaceVocals(config);
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

    private static async Task ForceReplaceVocals(VocalSfxSwapConfig? config)
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

        if (config == null)
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default vocal bank");
            PlayerVocalSFX.Instance.vocalBank = baseZoeVocalBank;
            return;
        }

        var skinCacheIndex = Util.SkinIndexToCacheIndex(config.SkinIndex);

        VocalBank skinVocalBank;
        if (!vocalBankCache.ContainsKey(skinCacheIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin vocal bank not found in cache. Creating a new one...");

            skinVocalBank = await GenerateVocalBank(baseZoeVocalBank, config);
            vocalBankCache[skinCacheIndex] = skinVocalBank;
        }
        else
        {
            skinVocalBank = vocalBankCache[skinCacheIndex];
        }

        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Set new skin vocal bank: {skinVocalBank}");
        PlayerVocalSFX.Instance.vocalBank = skinVocalBank;
    }

    public static async Task<VocalBank> GenerateVocalBank(VocalSfxSwapConfig config)
    {
        if (BaseZoeVocalBank == null)
        {
            throw new Exception("Missing Zoe's base vocal bank");
        }

        return await GenerateVocalBank(BaseZoeVocalBank, config);
    }

    public static async Task<VocalBank> GenerateVocalBank(VocalBank baseVocalBank, VocalSfxSwapConfig config)
    {
        var vocalBank = ScriptableObject.CreateInstance<VocalBank>();
        UnityEngine.Object.DontDestroyOnLoad(vocalBank);
        vocalBank.name = $"{baseVocalBank.name} {config}";

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
        foreach (var field in Util.VocalBankSfxInstanceFields)
        {
            var sfxName = Util.VocalBankFieldToSfxName(field);
            if (!config.Swaps.ContainsKey(sfxName))
            {
                continue;
            }

            var swapConfig = config.Swaps[sfxName];

            var oldSfxInstance = (SFX_Instance)field.GetValue(vocalBank);

            SFX_Instance sfxInstance = await GenerateSfxInstance(oldSfxInstance, config.SkinIndex, swapConfig);
            field.SetValue(vocalBank, sfxInstance);
        }

        return vocalBank;
    }


    public static async Task ReplaceInteractionVocals(VocalSfxSwapConfig? config)
    {
        await replaceInteractionVocalsSemaphore.WaitAsync();

        try
        {
            await ForceReplaceInteractionVocals(config);
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

    private static async Task ForceReplaceInteractionVocals(VocalSfxSwapConfig? config)
    {
        if (baseZoeInteractionVocalBank == null)
        {
            baseZoeInteractionVocalBank = InteractionCharacterDatabase.Instance.courier.VocalBank;
        }

        if (config == null)
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default interaction vocal bank");
            InteractionCharacterDatabase.Instance.courier.VocalBank = baseZoeInteractionVocalBank;
            return;
        }

        var skinCacheIndex = Util.SkinIndexToCacheIndex(config.SkinIndex);

        InteractionVocalBank skinInteractionVocalBank;
        if (!interactionVocalBankCache.ContainsKey(skinCacheIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin interaction vocal bank not found in cache. Creating a new one...");

            skinInteractionVocalBank = await GenerateInteractionVocalBank(baseZoeInteractionVocalBank, config);
            interactionVocalBankCache[skinCacheIndex] = skinInteractionVocalBank;
        }
        else
        {
            skinInteractionVocalBank = interactionVocalBankCache[skinCacheIndex];
        }

        InteractionCharacterDatabase.Instance.courier.VocalBank = skinInteractionVocalBank;
    }

    public static async Task<InteractionVocalBank> GenerateInteractionVocalBank(VocalSfxSwapConfig config)
    {
        if (BaseZoeInteractionVocalBank == null)
        {
            throw new Exception("Missing Zoe's base interaction vocal bank");
        }

        return await GenerateInteractionVocalBank(BaseZoeInteractionVocalBank, config);
    }

    public static async Task<InteractionVocalBank> GenerateInteractionVocalBank(InteractionVocalBank baseInteractionVocalBank, VocalSfxSwapConfig config)
    {
        var interactionVocalBank = ScriptableObject.CreateInstance<InteractionVocalBank>();
        UnityEngine.Object.DontDestroyOnLoad(interactionVocalBank);
        interactionVocalBank.name = $"{baseInteractionVocalBank.name} {config}";

        // Copy all SFX_Instances from Zoe's base interaction vocal bank to the new interaction voice bank
        foreach (var fieldInfo in Util.InteractionVocalBankSfxInstanceFields)
        {
            fieldInfo.SetValue(interactionVocalBank, fieldInfo.GetValue(baseInteractionVocalBank));
        }

        if (config.Swaps == null)
        {
            // What's the point? lol
            return interactionVocalBank;
        }

        // Create new SFX_Instances for replacement SFX clips
        foreach (var field in Util.InteractionVocalBankSfxInstanceFields)
        {
            var sfxName = Util.InteractionVocalBankFieldToSfxName(field);
            if (!config.Swaps.ContainsKey(sfxName))
            {
                continue;
            }

            var swapConfig = config.Swaps[sfxName];

            var oldSfxInstance = (SFX_Instance)field.GetValue(interactionVocalBank);

            SFX_Instance sfxInstance = await GenerateSfxInstance(oldSfxInstance, config.SkinIndex, swapConfig);
            field.SetValue(interactionVocalBank, sfxInstance);
        }

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

    private static async Task<SFX_Instance> GenerateSfxInstance(SFX_Instance baseSfxInstance, int? skinIndex, SfxInstanceSwapConfig config)
    {
        var sfxInstance = ScriptableObject.CreateInstance<SFX_Instance>();

        sfxInstance.name = $"{baseSfxInstance.name} {(skinIndex.HasValue ? $"Skin {skinIndex.Value} Swap" : "Default Swap")}";
        sfxInstance.settings = baseSfxInstance.settings;
        sfxInstance.lastTimePlayed = baseSfxInstance.lastTimePlayed;

        if (config.Settings != null)
        {
            sfxInstance.settings = new SFX_Settings();

            foreach (var settingField in typeof(SFX_Settings).GetFields())
            {
                settingField.SetValue(sfxInstance.settings, settingField.GetValue(baseSfxInstance.settings));
            }

            if (config.Settings.Volume.HasValue)
            {
                sfxInstance.settings.volume = Math.Clamp(config.Settings.Volume.Value, 0, 1);
            }
            if (config.Settings.VolumeVariation.HasValue)
            {
                sfxInstance.settings.volume_Variation = Math.Clamp(config.Settings.VolumeVariation.Value, 0, 1);
            }

            if (config.Settings.Pitch.HasValue)
            {
                sfxInstance.settings.pitch = config.Settings.Pitch.Value;
            }
            if (config.Settings.PitchVariation.HasValue)
            {
                sfxInstance.settings.pitch_Variation = Math.Clamp(config.Settings.PitchVariation.Value, 0, 1);
            }

            if (config.Settings.Range.HasValue)
            {
                sfxInstance.settings.range = config.Settings.Range.Value;
            }

            if (config.Settings.CooldownSeconds.HasValue)
            {
                sfxInstance.settings.cooldown = config.Settings.CooldownSeconds.Value;
            }

            if (config.Settings.SpatialBlend.HasValue)
            {
                sfxInstance.settings.spatialBlend = Math.Clamp(config.Settings.SpatialBlend.Value, 0, 1);
            }

            if (config.Settings.DopplerLevel.HasValue)
            {
                sfxInstance.settings.dopplerLevel = Math.Clamp(config.Settings.DopplerLevel.Value, 0, 1);
            }

            if (config.Settings.HighPriority.HasValue)
            {
                sfxInstance.settings.highPrio = config.Settings.HighPriority.Value;
            }
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
            string.Join("\n", Util.VocalBankSfxInstanceFields
                .Select(Util.VocalBankFieldToSfxName)
                .Concat(
                    Util.InteractionVocalBankSfxInstanceFields.Select(Util.InteractionVocalBankFieldToSfxName)
                )
                .OrderBy((name) => name) // alphabetical
                .Select((name) => $"- {name}")
            )
        );
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void WriteExampleConfig()
    {
        var path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Example.00000.hastevocalsfx.json");

        var config = new VocalSfxSwapConfig()
        {
            BasePath = "",
            Swaps = new(
                Util.VocalBankSfxInstanceFields
                    .Select((field) =>
                    {
                        SFX_Settings baseZoeSfxSettings = ((SFX_Instance)field.GetValue(BaseZoeVocalBank)).settings;

                        return new KeyValuePair<string, SfxInstanceSwapConfig>(
Util.VocalBankFieldToSfxName(field),
                            new()
                            {
                                Clips = [],
                                Settings = SfxSettingsSwap.FromSfxSettings(baseZoeSfxSettings)
                            }
                        );
                    })
                    .Concat(
                        Util.InteractionVocalBankSfxInstanceFields.Select((field) =>
                        {
                            SFX_Settings baseZoeSfxSettings = ((SFX_Instance)field.GetValue(BaseZoeInteractionVocalBank)).settings;

                            return new KeyValuePair<string, SfxInstanceSwapConfig>(
Util.InteractionVocalBankFieldToSfxName(field),
                                new()
                                {
                                    Clips = [],
                                    Settings = SfxSettingsSwap.FromSfxSettings(baseZoeSfxSettings)
                                }
                            );
                        })
                    )
                    .OrderBy((kvp) => kvp.Key) // alphabetical
            )
        };

        var configJson = JsonConvert.SerializeObject(config, new JsonSerializerSettings()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        });

        File.WriteAllText(path, configJson);

        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Written example config to {path}");
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
