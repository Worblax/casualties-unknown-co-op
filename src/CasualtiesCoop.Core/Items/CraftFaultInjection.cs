using System;

namespace CasualtiesCoop.Core.Items;

// Test-only fault seam. It is inert unless a test assembly installs a handler, which is only
// possible from the opt-in test plugin/check projects.
internal static class CraftFaultInjection
{
    internal static Action<string>? Handler;

    internal static void Hit(string point) => Handler?.Invoke(point);

    internal static void Clear() => Handler = null;
}
