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

    protected static Dictionary<int, Dictionary<string, SfxInstanceSwapConfig>> vocalBanksReplacements = [];
    protected static Dictionary<int, VocalBank> skinVocalBankCache = [];

    private static VocalBank? baseZoeVocalBank;
    public static VocalBank? BaseZoeVocalBank {
        get { return baseZoeVocalBank; }
    }

    static VocalSfxSwapMod()
    {
        FactSystem.SubscribeToFact(
            SkinManager.EquippedSkinBodyFact,
            async (skinIndex) => await ReplaceVocals((int) skinIndex)
        );

        SceneManager.sceneLoaded += async (newScene, mode) =>
        {
            if (mode != LoadSceneMode.Single)
            {
                return;
            }

            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Scene change: {newScene.name}");
            await ReplaceVocals((int) FactSystem.GetFact(SkinManager.EquippedSkinBodyFact));
        };

        foreach (var item in Modloader.LoadedItemDirectories)
        {
            LoadVocalSfxs(item.Value.directory);
        }
    }

    private static string VocalBankFieldToSfxName(FieldInfo field)
    {
        return field.Name.Remove(field.Name.IndexOf("Vocals"), "Vocals".Length);
    }

    private static void LoadVocalSfxs(string directory)
    {
        Dictionary<int, Dictionary<string, List<string>>> foundSoundFilesPerSkinPerSfx = [];

        void TryLoadWavFile(string wavFilePath)
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
                Dictionary<string, SfxInstanceSwapConfig>? configs = new JsonSerializer()
                    .Deserialize<Dictionary<string, SfxInstanceSwapConfig>>(new JsonTextReader(file));
                if (configs == null)
                {
                    // Not sure when this can happen...
                    return;
                }

                var configDirectoryBasePath = Path.GetDirectoryName(configFilePath);

                foreach (var config in configs.Values)
                {
                    if (config.Clips == null)
                    {
                        continue;
                    }

                    // Post-process: Make all paths absolute
                    config.Clips = config.Clips
                        .Select(soundFilePath => Path.IsPathFullyQualified(soundFilePath)
                            ? soundFilePath
                            : Path.Combine(configDirectoryBasePath, soundFilePath)
                        )
                        .ToArray();
                }

                vocalBanksReplacements.Add(skinIndex, configs);
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
                TryLoadWavFile(filePath);
            }
            else if (filePath.EndsWith(".hastevocalssfx.json"))
            {
                TryLoadConfigFile(filePath);
            }
        }

        foreach (var skinIndex in foundSoundFilesPerSkinPerSfx.Keys)
        {
            Dictionary<string, SfxInstanceSwapConfig> skinConfigs;
            if (vocalBanksReplacements.ContainsKey(skinIndex))
            {
                skinConfigs = vocalBanksReplacements[skinIndex];
            }
            else
            {
                skinConfigs = [];
                vocalBanksReplacements.Add(skinIndex, skinConfigs);
            }

            foreach (var sfxName in foundSoundFilesPerSkinPerSfx[skinIndex].Keys)
            {
                SfxInstanceSwapConfig config;
                if (skinConfigs.ContainsKey(sfxName))
                {
                    config = skinConfigs[sfxName];
                    if (config.Clips != null)
                    {
                        continue;
                    }
                }
                else
                {
                    config = new();
                    skinConfigs[sfxName] = config;
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

    public static async Task ReplaceVocals(int skinIndex)
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

        if (!vocalBanksReplacements.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Reset vocal bank to Zoe's default VocalBank");
            PlayerVocalSFX.Instance.vocalBank = baseZoeVocalBank;
            return;
        }

        var skinReplacements = vocalBanksReplacements[skinIndex];

        VocalBank skinVocalBank;
        if (!skinVocalBankCache.ContainsKey(skinIndex))
        {
            Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin vocal bank not found in cache. Creating a new one...");

            skinVocalBank = ScriptableObject.CreateInstance<VocalBank>();
            skinVocalBank.name = $"{baseZoeVocalBank.name} Skin {skinIndex}";

            // Copy all SFX_Instances from Zoe's base vocal bank to the new voice bank
            foreach (var fieldInfo in typeof(VocalBank).GetFields())
            {
                if (fieldInfo.FieldType != typeof(SFX_Instance))
                {
                    continue;
                }

                fieldInfo.SetValue(skinVocalBank, fieldInfo.GetValue(baseZoeVocalBank));
            }

            // Create new SFX_Instances for replacement SFX clips
            foreach (var field in vocalBankSfxInstanceFields)
            {
                var sfxName = VocalBankFieldToSfxName(field);
                if (!skinReplacements.ContainsKey(sfxName))
                {
                    //Debug.Log($"[{nameof(VocalSfxSwapMod)}] Skin {skinIndex} has no {sfxName} replacements");
                    continue;
                }

                var oldSfxInstance = (SFX_Instance)field.GetValue(skinVocalBank);

                var newSfxInstance = ScriptableObject.CreateInstance<SFX_Instance>();
                field.SetValue(skinVocalBank, newSfxInstance);

                newSfxInstance.name = $"{oldSfxInstance.name} Skin {skinIndex}";
                newSfxInstance.settings = oldSfxInstance.settings;
                newSfxInstance.lastTimePlayed = oldSfxInstance.lastTimePlayed;

                var sfxInstanceConfig = skinReplacements[sfxName];

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
                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Loading AudioClip {path}");

                    var audioClip = await LoadAudioClipFromPath(path);
                    if (audioClip == null)
                    {
                        continue;
                    }

                    clips.Add(audioClip);

                    Debug.Log($"[{nameof(VocalSfxSwapMod)}] Successfully loaded AudioClip {path}");
                }
                newSfxInstance.clips = clips.ToArray();
            }

            skinVocalBankCache[skinIndex] = skinVocalBank;
        } else
        {
            skinVocalBank = skinVocalBankCache[skinIndex];
        }

        Debug.Log($"[{nameof(VocalSfxSwapMod)}] Set new skin vocal bank: {skinVocalBank}");
        PlayerVocalSFX.Instance.vocalBank = skinVocalBank;
    }

    [Zorro.Core.CLI.ConsoleCommand]
    public static void ListVocalSfxNames()
    {
        Debug.Log(string.Join("\n", vocalBankSfxInstanceFields.Select((field) => $"- {VocalBankFieldToSfxName(field)}")));
    }
}

public record SfxInstanceSwapConfig
{
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