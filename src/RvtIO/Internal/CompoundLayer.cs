namespace RvtIO.Internal;

/// <summary>
/// A single layer in a CompoundStructure (wall/floor/ceiling type).
/// Extracted from 0xFFFF0200/0xFFFF1026 blocks after marker 0x1003.
/// Per layer: 29 bytes = [thickness:f64][function:i32][-1:i32][materialId:i32][-1:i32][layerIndex:i32][wrapping:u8]
/// </summary>
internal record CompoundLayer
{
    /// <summary>Layer thickness in feet (Revit internal units).</summary>
    public double ThicknessFt { get; init; }

    /// <summary>Stored layer function: 0=None, 1=Structure, 2=Substrate, 3=Insulation, 4=Finish1, 5=Finish2, 100=Membrane, 200=StructuralDeck.</summary>
    public int Function { get; init; }

    /// <summary>Material element ID (0 or -1 if none).</summary>
    public int MaterialId { get; init; }

    /// <summary>Layer index (0-based, from exterior to interior).</summary>
    public int LayerIndex { get; init; }

    /// <summary>True for the stored function values seen in files; other values are no layer array (the 0x1003 marker also occurs in unrelated blocks).</summary>
    public static bool IsStoredFunction(int function) => function is (>= 0 and <= 5) or 100 or 200;

    public static string FunctionName(int function) => function switch
    {
        0 => "None", 1 => "Structure", 2 => "Substrate", 3 => "Insulation", 4 => "Finish1", 5 => "Finish2",
        100 => "Membrane", 200 => "StructuralDeck", _ => $"Function{function}",
    };
}
