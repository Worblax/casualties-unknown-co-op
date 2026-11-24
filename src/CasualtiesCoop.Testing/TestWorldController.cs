using System.Security.Cryptography;
using CasualtiesCoop.Sandbox;
using UnityEngine;

namespace CasualtiesCoop.Testing;

internal sealed class TestWorldController : IDisposable
{
    private readonly bool hadTutorial = PlayerPrefs.HasKey("tutorial");
    private readonly int previousTutorial = PlayerPrefs.GetInt("tutorial");
    private readonly bool hadCharacter = PlayerPrefs.HasKey("lastchar");
    private readonly int previousCharacter = PlayerPrefs.GetInt("lastchar");
    private readonly string? settingsHash = HashFile("settings.json");
    private readonly string? saveHash = HashFile("save.sv");
    private SandboxScene? scene;
    private bool entering;
    private bool restored;

    internal bool Ready
    {
        get
        {
            if (scene is null && PreRunScript.instance && SandboxScene.CanEnter(PreRunScript.instance))
            {
                entering = true;
                scene = SandboxScene.Enter(PreRunScript.instance, character: 0, "normal");
            }

            scene?.Tick();
            if (scene?.CourseLoaded != true)
            {
                return false;
            }

            RestorePreferences();
            return true;
        }
    }

    internal Body Body => PlayerCamera.main.body;

    private void RestorePreferences()
    {
        if (restored || !entering)
        {
            return;
        }
        restored = true;
        Restore("tutorial", hadTutorial, previousTutorial);
        Restore("lastchar", hadCharacter, previousCharacter);
    }

    internal bool VerifyIsolation(out object evidence)
    {
        RestorePreferences();
        bool settingsUnchanged = settingsHash == HashFile("settings.json");
        bool saveUnchanged = saveHash == HashFile("save.sv");
        bool prefsRestored = Same("tutorial", hadTutorial, previousTutorial) && Same("lastchar", hadCharacter, previousCharacter);
        evidence = new { settingsUnchanged, saveUnchanged, prefsRestored };
        return settingsUnchanged && saveUnchanged && prefsRestored;
    }

    private static bool Same(string key, bool present, int value) =>
        PlayerPrefs.HasKey(key) == present && (!present || PlayerPrefs.GetInt(key) == value);

    private static string? HashFile(string name)
    {
        string path = Path.Combine(Application.persistentDataPath, name);
        if (!File.Exists(path))
        {
            return null;
        }
        using var file = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(file));
    }

    private static void Restore(string key, bool present, int value)
    {
        if (present) PlayerPrefs.SetInt(key, value);
        else PlayerPrefs.DeleteKey(key);
    }

    public void Dispose()
    {
        RestorePreferences();
        scene?.Dispose();
    }
}
