TransportChecks.Run();
ItemChecks.Run();
HostItemChecks.Run();
GuestItemChecks.Run();
ItemMotionChecks.Run();
CraftChecks.Run();
P0CraftChecks.Run();
RecoveryChecks.Run();
FluidChecks.Run();
WorldChunkChecks.Run();
Console.WriteLine("Virtual transport, shared item, session recovery and terrain fluid checks passed.");

internal static class Check
{
    internal static void That(bool result, string scenario)
    {
        if (!result)
        {
            throw new InvalidOperationException(scenario);
        }
    }
}
