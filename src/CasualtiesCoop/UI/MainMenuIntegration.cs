using System;
using BepInEx.Logging;
using CasualtiesCoop.Runtime;
using UnityEngine;

namespace CasualtiesCoop.UI;

internal sealed class MainMenuIntegration
{
    private readonly CoopRuntime runtime;
    private readonly ManualLogSource log;
    private CoopMenuView? view;
    private float nextProbe;

    internal bool IsOpen => view && view!.PanelOpen;
    internal bool IsAvailable => view && view!.MenuAvailable;

    internal MainMenuIntegration(CoopRuntime runtime, ManualLogSource log)
    {
        this.runtime = runtime;
        this.log = log;
    }

    internal void Tick()
    {
        if (!view && Time.unscaledTime >= nextProbe)
        {
            Attach();
        }
    }

    internal void Attach()
    {
        nextProbe = Time.unscaledTime + 0.35f;
        PreRunScript owner = PreRunScript.instance;
        if (view || !owner || !owner.mainCanvas)
        {
            return;
        }

        try
        {
            view = owner.gameObject.AddComponent<CoopMenuView>();
            view.Install(owner, runtime);
            log.LogInfo("Native Co-op button attached to the main menu canvas.");
        }
        catch (Exception exception)
        {
            if (view)
            {
                UnityEngine.Object.Destroy(view);
            }
            log.LogError($"Co-op menu attachment failed: {exception}");
        }
    }
}
