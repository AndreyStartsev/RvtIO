namespace RvtIO;

/// <summary>Configuration for RVT file reading.</summary>
public class RvtReaderOptions
{
    /// <summary>Path to an optional text dump of the model for CompoundStructure layer extraction. Null to skip.</summary>
    public string? ExternalDumpPath { get; set; }

    /// <summary>Regular expression for the prefix that class names in the external dump carry in front of the class name proper.
    /// Null uses the default shape (capital, lowercase, capital, lowercase, followed by a capital).</summary>
    public string? ExternalDumpClassPrefix { get; set; }

    /// <summary>Optional root of the resource DLLs of a Revit installation
    /// (&lt;root&gt;/&lt;RevitVersion&gt;/&lt;culture&gt;/*.dll) to read the localized system family names from.
    /// Null uses the names embedded in the library.</summary>
    public string? RevitLocalizationDirectory { get; set; }

    /// <summary>Locale code (such as "ENU" or "RUS") of the localization resources that name system families, instead
    /// of the locale the file was saved in. The current profile names them in English whatever the file's locale. Null keeps
    /// the saved locale.</summary>
    public string? SystemFamilyNameLocale { get; set; }

    /// <summary>Keep two faces that share their outline but not their openings as two faces (the decoded geometry merges faces by the
    /// corners of their outline only). The current profile has them as two faces: the cap of a window frame and the cap of the glazing bar behind it
    /// are one rectangle with different holes. Off by default, which keeps the geometry the legacy profile export is built on.</summary>
    public bool SeparateFacesWithDistinctOpenings { get; set; }

    /// <summary>Rewrite the partition streams of a file with a later release's class schema (Revit 2023 and later) into the Revit 2022
    /// layout, so that the geometry and element readers written for 2022 see them (see <see cref="Parsing.R22Transcoder"/>). Off by default.</summary>
    public bool R22Layout { get; set; }

    /// <summary>When several partition streams hold a copy of the same element, keep the current copy named by the file's document
    /// increment table and drop the superseded ones before decoding (see <see cref="Parsing.RecordRouting"/>). Off by default: the copies
    /// are then all decoded as before.</summary>
    public bool ResolveCurrentCopies { get; set; }

    /// <summary>Raise the limits that cut a stream while it is recovered from damage: the output of the element table recovery
    /// (256 MiB) and its loop guard (4096 iterations) become the platform array length and unbounded.
    /// For very large models. Off by default.</summary>
    public bool RaiseLimits { get; set; }

    /// <summary>Collects the streams read, how much of each decoded, and the limits that cut a stream (see <see cref="ExportReport"/>). Null to collect nothing.</summary>
    public RvtReadDiagnostics? Diagnostics { get; set; }

    /// <summary>Write diagnostic/progress messages to this TextWriter. Null for silent operation.</summary>
    public TextWriter? DiagnosticOutput { get; set; }
}
