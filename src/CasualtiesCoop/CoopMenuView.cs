using UnityEngine;

namespace CasualtiesCoop;

public sealed class CoopMenuView : MonoBehaviour
{
    private void Update()
    {
        if (CoopPlugin.CanInitialize)
        {
            CoopPlugin.TickMenu();
        }
    }

    private void OnGUI()
    {
        CoopPlugin.DrawMenu();
    }
}
