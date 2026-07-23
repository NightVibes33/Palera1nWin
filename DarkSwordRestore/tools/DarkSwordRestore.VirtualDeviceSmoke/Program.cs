namespace DarkSwordRestore.VirtualDeviceSmoke;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
        if (VirtualToolProgram.KnownToolNames.Contains(executableName))
            return await VirtualToolProgram.RunAsync(executableName, args).ConfigureAwait(false);

        var reportPath = GetOption(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "virtual-device-smoke-report.json");
        var transcriptPath = GetOption(args, "--transcript") ?? Path.Combine(Environment.CurrentDirectory, "virtual-device-smoke-transcript.txt");
        var workRoot = GetOption(args, "--work-root") ?? Path.Combine(Environment.CurrentDirectory, "virtual-device-smoke-work");

        try
        {
            var suite = new VirtualSmokeSuite(workRoot, reportPath, transcriptPath);
            return await suite.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string? GetOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
