using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace CasualtiesCoop.Testing;

// Test processes share the Windows user profile. Redirect Unity's file root and PlayerPrefs to a
// per-process directory so two clients cannot read or overwrite each other's data.
internal static class MultiProcessDataIsolation
{
    private static string? root;
    private static Dictionary<string, string> prefs = new(StringComparer.Ordinal);

    internal static void Initialize(string path)
    {
        root = Path.GetFullPath(path);
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "playerprefs.json");
        if (File.Exists(file))
        {
            try
            {
                prefs = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)) ??
                    new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception)
            {
                prefs = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    internal static void Patch(Harmony harmony)
    {
        PatchMethod(harmony, typeof(Application), "persistentDataPath", new[] { typeof(string) },
            nameof(PersistentDataPath), isGetter: true);
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), new[] { typeof(string) }, nameof(GetIntDefault));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetInt), new[] { typeof(string), typeof(int) }, nameof(GetInt));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.SetInt), new[] { typeof(string), typeof(int) }, nameof(SetInt));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetFloat), new[] { typeof(string) }, nameof(GetFloatDefault));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetFloat), new[] { typeof(string), typeof(float) }, nameof(GetFloat));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.SetFloat), new[] { typeof(string), typeof(float) }, nameof(SetFloat));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), new[] { typeof(string) }, nameof(GetStringDefault));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.GetString), new[] { typeof(string), typeof(string) }, nameof(GetString));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.SetString), new[] { typeof(string), typeof(string) }, nameof(SetString));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.HasKey), new[] { typeof(string) }, nameof(HasKey));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.DeleteKey), new[] { typeof(string) }, nameof(DeleteKey));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.DeleteAll), Type.EmptyTypes, nameof(DeleteAll));
        PatchMethod(harmony, typeof(PlayerPrefs), nameof(PlayerPrefs.Save), Type.EmptyTypes, nameof(Save));
    }

    private static void PatchMethod(Harmony harmony, Type type, string name, Type[] arguments, string prefix,
        bool isGetter = false)
    {
        System.Reflection.MethodInfo? method = isGetter
            ? AccessTools.PropertyGetter(type, name)
            : type.GetMethod(name, arguments);
        if (method is null) return;
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(MultiProcessDataIsolation), prefix));
    }

    internal static bool PersistentDataPath(ref string __result)
    {
        if (root is null) return true;
        __result = root;
        return false;
    }

    internal static bool GetInt(string key, int defaultValue, ref int __result)
    {
        if (root is null) return true;
        __result = prefs.TryGetValue(key, out string? value) && value.StartsWith("i:", StringComparison.Ordinal) &&
            int.TryParse(value.Substring(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed : defaultValue;
        return false;
    }

    internal static bool GetIntDefault(string key, ref int __result) => GetInt(key, 0, ref __result);

    internal static bool SetInt(string key, int value)
    {
        if (root is null) return true;
        prefs[key] = "i:" + value.ToString(CultureInfo.InvariantCulture);
        Save();
        return false;
    }

    internal static bool GetFloat(string key, float defaultValue, ref float __result)
    {
        if (root is null) return true;
        __result = prefs.TryGetValue(key, out string? value) && value.StartsWith("f:", StringComparison.Ordinal) &&
            float.TryParse(value.Substring(2), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed : defaultValue;
        return false;
    }

    internal static bool GetFloatDefault(string key, ref float __result) => GetFloat(key, 0f, ref __result);

    internal static bool SetFloat(string key, float value)
    {
        if (root is null) return true;
        prefs[key] = "f:" + value.ToString("R", CultureInfo.InvariantCulture);
        Save();
        return false;
    }

    internal static bool GetString(string key, string defaultValue, ref string __result)
    {
        if (root is null) return true;
        __result = prefs.TryGetValue(key, out string? value) && value.StartsWith("s:", StringComparison.Ordinal)
            ? value.Substring(2) : defaultValue;
        return false;
    }

    internal static bool GetStringDefault(string key, ref string __result) => GetString(key, string.Empty, ref __result);

    internal static bool SetString(string key, string value)
    {
        if (root is null) return true;
        prefs[key] = "s:" + value;
        Save();
        return false;
    }

    internal static bool HasKey(string key, ref bool __result)
    {
        if (root is null) return true;
        __result = prefs.ContainsKey(key);
        return false;
    }

    internal static bool DeleteKey(string key)
    {
        if (root is null) return true;
        prefs.Remove(key);
        Save();
        return false;
    }

    internal static bool DeleteAll()
    {
        if (root is null) return true;
        prefs.Clear();
        Save();
        return false;
    }

    internal static bool Save()
    {
        if (root is null) return true;
        File.WriteAllText(Path.Combine(root, "playerprefs.json"), JsonConvert.SerializeObject(prefs));
        return false;
    }
}
