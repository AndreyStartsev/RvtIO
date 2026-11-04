namespace RvtIO.Cli;

/// <summary>The option that decides which copy of an element is read when several partition streams of a file saved in several
/// increments hold it. By default only the current copy is read; <c>--keep-all-copies</c> reads every copy. <c>--resolve-current-copies</c>
/// is the earlier opt-in for the default and is accepted without effect.</summary>
internal static class CurrentCopiesOption
{
    public const string KeepAll = "--keep-all-copies";
    public const string ResolveOption = "--resolve-current-copies";

    /// <summary>Every option of this group, for the list of switches the parser accepts.</summary>
    public static readonly string[] Names = [KeepAll, ResolveOption];

    /// <summary>Whether only the current copy is read (<see cref="KeepAll"/> wins when both options are given).</summary>
    public static bool Resolve(string[] args) => !args.Contains(KeepAll);
}
