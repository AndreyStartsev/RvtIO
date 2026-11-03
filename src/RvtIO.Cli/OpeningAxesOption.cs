namespace RvtIO.Cli;

/// <summary>The options that name the sides of the opening of a door or window. By default they are the Width (along the wall) and
/// Height of the door or window, as Revit has them; <c>--ifc-opening-legacy-axes</c> restores the sides named by the frame of the
/// cut loop. <c>--ifc-opening-host-axes</c> is the earlier opt-in for the default and is accepted without effect.</summary>
internal static class OpeningAxesOption
{
    public const string LegacyAxes = "--ifc-opening-legacy-axes";
    public const string HostAxes = "--ifc-opening-host-axes";

    /// <summary>Every option of this group, for the list of switches the parser accepts.</summary>
    public static readonly string[] Names = [LegacyAxes, HostAxes];

    /// <summary>Whether the legacy sides are asked for.</summary>
    public static bool Legacy(string[] args) => args.Contains(LegacyAxes);
}
