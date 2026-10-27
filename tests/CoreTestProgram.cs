using System;
internal static class CoreTestProgram
{
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--compat") { CompatibilityContract.Emit(args[1]); return; }
        RegressionTests.Run();
    }
}
