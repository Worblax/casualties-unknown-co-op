TransportChecks.Run();
ItemChecks.Run();
HostItemChecks.Run();
GuestItemChecks.Run();
RecoveryChecks.Run();
FluidChecks.Run();
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
