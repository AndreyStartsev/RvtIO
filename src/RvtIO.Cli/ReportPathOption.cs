namespace RvtIO.Cli;

/// <summary>The refusal of a <c>--report</c> path that would overwrite the input or another file the command line
/// names as an output of the run: the export itself, <c>--dev-external-dump</c> and the compact measurement dumps.</summary>
internal static class ReportPathOption
{
    /// <summary>The dump options of the export whose value is an output path.</summary>
    public static readonly string[] DumpOptions =
        ["--dev-candidates", "--dev-trace", "--dev-facts", "--dev-solids", "--dev-user-params", "--dev-collisions"];

    /// <summary>Every file the command line names as an output: the input never, the export and every named dump.</summary>
    public static IEnumerable<string> OutputPaths(string[] args, string rvtPath, string outputPath, string? externalDumpPath)
    {
        yield return rvtPath;
        yield return outputPath;
        if (externalDumpPath != null) yield return externalDumpPath;
        foreach (string option in DumpOptions)
            if (ValueOf(args, option) is { } path) yield return path;
    }

    private static string? ValueOf(string[] args, string option)
    {
        string? value = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == option) value = args[i + 1];
        return value;
    }
}
