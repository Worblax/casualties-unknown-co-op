using CasualtiesCoop.Core;
using UnityEngine;

namespace CasualtiesCoop.Runtime;

internal sealed class UnityMonotonicClock : IMonotonicClock
{
    public float Now => Time.unscaledTime;
}
