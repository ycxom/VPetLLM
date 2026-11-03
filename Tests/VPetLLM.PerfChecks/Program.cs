namespace VPetLLM.PerfChecks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--bench")
            return Bench.Run(args[1]);
        if (args.Length == 2 && args[0] == "--bench-settings")
            return SettingsBench.Run(args[1]);
        if (args.Length == 3 && args[0] == "--golden")
            return Golden.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--diff")
            return Differential.Run(args[1]) | FanOutChecks.Run();

        return Differential.Run(null) | FanOutChecks.Run();
    }
}
