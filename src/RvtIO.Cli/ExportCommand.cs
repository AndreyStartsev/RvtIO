using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO;
using RvtIO.Compact;
using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Model;

internal static partial class Program
{
    /// <summary>The values of the command line that decide the export.</summary>
    private sealed record ExportArguments(string[] Args, string RvtPath, string OutputPath, string? ExternalDumpPath, string? ExternalDumpClassPrefix,
        IfcProfile Profile, bool IsIfcOutput, string? ExportPhaseOption, string? RoomPhaseOption, bool IncludeIfcRevitParameters, bool RevitParameterGroups, bool BaseQuantities,
        string JsonClassPrefix, bool CompactJson);

    // The export: reads the file and writes the IFC, the compact JSON or the full JSON dump.
    private static int Export(ExportArguments arguments)
    {
        var (args, rvtPath, outputPath, externalDumpPath, externalDumpClassPrefix, profile, isIfcOutput, exportPhaseOption, roomPhaseOption, includeIfcRevitParameters, revitParameterGroups, baseQuantities, jsonClassPrefix, compactJson) = arguments;
        var options = new RvtReaderOptions
        {
            DiagnosticOutput = Console.Error,
            ExternalDumpPath = externalDumpPath,
            ExternalDumpClassPrefix = externalDumpClassPrefix,
            RevitLocalizationDirectory = FindRevitLocalizationDirectory(args),
            SystemFamilyNameLocale = profile == RvtIO.Ifc.IfcProfile.Current ? "ENU" : null,
            SeparateFacesWithDistinctOpenings = profile == RvtIO.Ifc.IfcProfile.Current,
            // IFC export reads the geometry with the Revit 2022 readers; --no-r22-layout keeps the 64-bit schema path alone
            R22Layout = isIfcOutput && !args.Contains("--no-r22-layout") || args.Contains("--r22-layout"),
            ResolveCurrentCopies = args.Contains("--resolve-current-copies"),
        };

        var reader = new RvtFileReader(options);
        var swRead = System.Diagnostics.Stopwatch.StartNew();
        var rvtInfo = reader.Read(rvtPath);
        Console.Error.WriteLine($"Timing: read {swRead.Elapsed.TotalSeconds:F1}s");

        if (outputPath.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
        {
            // Without the option the current profile exports the rooms of the last project phase that exists as a phase element (the
            // exporter resolves it); the legacy profile target exports every room. "all" keeps every room, "last" and an ID are explicit.
            int? roomPhase = roomPhaseOption is "last" ? rvtInfo.SerializedDefaultExportPhaseId
                : roomPhaseOption is not (null or "all") ? int.Parse(roomPhaseOption, System.Globalization.CultureInfo.InvariantCulture) : null;
            if (rvtInfo.SerializedProjectPhaseIds is { } authoredPhases)
                Console.Error.WriteLine($"Project phases (authored order): {string.Join(",", authoredPhases)}");
            bool roomPhaseValidated = roomPhase != null && rvtInfo.SerializedProjectPhaseIds?.Contains(roomPhase.Value) == true;
            if (roomPhaseOption is not (null or "all") && !roomPhaseValidated)
            {
                Console.Error.WriteLine("Room export phase cannot be resolved from the validated project phase order.");
                return 1;
            }
            if (!roomPhaseValidated) roomPhase = null;
            // The phase of the whole export (elements and rooms). Without the option the current profile exports the last phase and the legacy profile all of them.
            int? exportPhase = exportPhaseOption is "last" ? rvtInfo.SerializedDefaultExportPhaseId
                : exportPhaseOption is not (null or "all") ? int.Parse(exportPhaseOption, System.Globalization.CultureInfo.InvariantCulture) : null;
            if (exportPhaseOption is not (null or "all") && (exportPhase == null || rvtInfo.SerializedProjectPhaseIds?.Contains(exportPhase.Value) != true))
            {
                Console.Error.WriteLine("Export phase cannot be resolved from the validated project phase order.");
                return 1;
            }
            if (exportPhase != null) Console.Error.WriteLine($"IFC export phase: {exportPhase}");
            if (roomPhase != null) Console.Error.WriteLine($"IFC room export phase: {roomPhase}");
            // A 64-bit-id file has no scanned fallback names: the exporter reads the validated ones, as the JSON dump does.
            if (rvtInfo.GeometryUndecoded)
                RvtIO.Parsing.ValidatedElementNames.Apply(rvtInfo,
                    reader.LastParsingState ?? throw new InvalidOperationException("The reader kept no parsing state."));
            var exporter = new IfcExporter(Console.Error, includeIfcRevitParameters) { ExportPhaseId = exportPhase, ExportAllPhases = exportPhaseOption == "all", RoomExportPhaseId = roomPhase, ExportRoomsOfAllPhases = roomPhaseOption == "all", RevitParameterGroups = revitParameterGroups, Profile = profile, BaseQuantities = baseQuantities };
            var swExport = System.Diagnostics.Stopwatch.StartNew();
            exporter.Export(rvtInfo, outputPath);
            Console.Error.WriteLine($"Timing: export {swExport.Elapsed.TotalSeconds:F1}s");
            Console.Error.WriteLine($"IFC written to {outputPath}");
        }
        else if (compactJson)
        {
            // The compact export reads Element.Name only where it is validated (the IFC branch above keeps the fallback names).
            RvtIO.Parsing.ValidatedElementNames.Apply(rvtInfo,
                reader.LastParsingState ?? throw new InvalidOperationException("The reader kept no parsing state."));
            var compactIdentity = new CompactIdentity(CompactUniverse.Select(rvtInfo));
            // --dev-candidates <tsv>: measurement run without the value gate; writes (id, parameter, stored text, derived text, class) of every
            // listed parameter for the value gate. --dev-trace <tsv>: writes (id, class, parameter, reason) of every listed parameter
            // (reason empty: written) and of every element whose list is unknown (parameter 0). --dev-facts <tsv>: writes the facts that decide
            // the phase parameters of every element and, in further columns, the facts named by --dev-fact-names (comma separated).
            string? candidatesPath = null, tracePath = null, factsPath = null, solidsPath = null, userParamsPath = null, collisionsPath = null;
            string[] factNames = [];
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--dev-candidates") candidatesPath = args[i + 1];
                if (args[i] == "--dev-trace") tracePath = args[i + 1];
                if (args[i] == "--dev-facts") factsPath = args[i + 1];
                if (args[i] == "--dev-user-params") userParamsPath = args[i + 1];
                if (args[i] == "--dev-collisions") collisionsPath = args[i + 1];
                if (args[i] == "--dev-fact-names") factNames = args[i + 1].Split(',');
                if (args[i] == "--dev-solids") solidsPath = args[i + 1];
            }
            // --dev-solids <tsv>: measurement dump of the solid quantities of every element read in the Revit 2022 layout;
            // --dev-solids-only stops after it
            if (solidsPath != null)
            {
                var measured = RvtIO.Compact.CompactSolidQuantities.Read(rvtPath, options, null);
                using var solidsWriter = new StreamWriter(solidsPath, false, new System.Text.UTF8Encoding(false));
                RvtIO.Compact.CompactSolidFacts.Dump(measured, rvtInfo.SerializedFormatSettings, solidsWriter);
                if (args.Contains("--dev-solids-only")) return 0;
            }
            // A wide layout does not decode geometry or curves: their quantities come from a second read in the Revit 2022 layout.
            var compactGate = RvtIO.Compact.CompactValueGate.ForLayout(rvtInfo.GeometryUndecoded);
            var compactQuantities = rvtInfo.GeometryUndecoded ? RvtIO.Compact.CompactSolidQuantities.Read(rvtPath, options, compactGate.HasSolidRules) : null;
            // A project (.rvt) is a document without an owner family; an .rfa is not decided.
            var compactSource = CompactPropertySources.Create(rvtInfo, reader.LastParsingState,
                rvtPath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? true : null, gated: candidatesPath == null, identity: compactIdentity, quantities: compactQuantities);
            using var candidatesWriter = candidatesPath == null ? null : new StreamWriter(candidatesPath, false, new System.Text.UTF8Encoding(false));
            using var traceWriter = tracePath == null ? null : new StreamWriter(tracePath, false, new System.Text.UTF8Encoding(false));
            using var factsWriter = factsPath == null ? null : new StreamWriter(factsPath, false, new System.Text.UTF8Encoding(false));
            static string TsvText(string? text) => text == null ? "\\N"
                : text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");
            candidatesWriter?.Write("id\tpid\tstored\tderived\tcls\n");
            if (candidatesWriter != null)
                compactSource.Builtin.Candidates = (element, id, stored, derived) => candidatesWriter.Write(
                    $"{element.Id}\t{id}\t{TsvText(stored)}\t{TsvText(derived)}\t{RvtIO.Compact.ElementListFacts.RecordClassName(element)}\n");
            traceWriter?.Write("id\tcls\tpid\treason\n");
            if (traceWriter != null)
                compactSource.Builtin.Trace = (element, id, reason) => traceWriter.Write(
                    $"{element.Id}\t{RvtIO.Compact.ElementListFacts.RecordClassName(element)}\t{id}\t{reason}\n");
            factsWriter?.Write("id\tcls\theader\tfamId\tunplaced\townerView\tcreatedPhase\ttypeBodyCategory\theaderDesignOption\tstoredIds\tfields\tcells\t" + string.Join("\t", factNames) + "\n");
            if (factsWriter != null)
                compactSource.Builtin.FactsTrace = (element, facts) => factsWriter.Write(
                    $"{element.Id}\t{RvtIO.Compact.ElementListFacts.RecordClassName(element)}\t{facts.Get("headerCategory")}\t{facts.Get("famId")}\t{facts.Get("unplacedOwnerId")}\t{facts.Get("ownerViewId")}\t{facts.Get("createdPhaseId")}\t{facts.Get("typeBodyCategory")}\t{element.NativeHeaderDesignOptionId}\t{(facts.StoredBuiltinIds is { } storedIds ? string.Join(",", storedIds.Where(id => id < 0)) : "?")}\t{(element.NativeListFields is { } listFields ? string.Join(";", listFields.Select(pair => pair.Key + "=" + pair.Value + (pair.Key.Contains(":m_p") || pair.Key.Contains(":m_o") || pair.Key.EndsWith(":m_data", StringComparison.Ordinal) ? "{" + (RvtIO.Parsing.TfBlockDeserializer.Schemas.TryGetValue(pair.Value, out var pointee) ? pointee.Name : "") + "}" : ""))) : "")}\t{(element.NativeCellListTag == 0 ? "-" : element.NativeCellClasses is { } cellClasses ? string.Join(",", cellClasses.Select(cell => RvtIO.Parsing.TfBlockDeserializer.Schemas.TryGetValue(cell, out var cellSchema) ? cellSchema.Name : "#" + cell)) : "?")}\t{string.Join("\t", factNames.Select(name => facts.Get(name)))}\n");
            // --dev-collisions <tsv>: writes (id, class, group, label, earlier value, user parameter value) of every user parameter that shares
            // its (group, label) with an earlier entry of the list
            using var collisionsWriter = collisionsPath == null ? null : new StreamWriter(collisionsPath, false, new System.Text.UTF8Encoding(false));
            collisionsWriter?.Write("id\tcls\tgroup\tlabel\tfirst\tuser\n");
            if (collisionsWriter != null)
                compactSource.Collision = (element, first, user) => collisionsWriter.Write(
                    $"{element.Id}\t{RvtIO.Compact.ElementListFacts.RecordClassName(element)}\t{TsvText(first.Group)}\t{TsvText(first.Label)}\t{TsvText(first.Value)}\t{TsvText(user.Value)}\n");
            // --dev-user-params <tsv>: the parameter definitions of the file (family, shared, project parameters) and their group
            if (userParamsPath != null && rvtInfo.UserParameters is { } userCatalog)
                File.WriteAllLines(userParamsPath,
                    new[] { "id\telemClass\tdefClass\tcaption\tgroupId\tgroupTypeId\ttypeId\tspec\tinstance\tbindings(category + i|t)" }.Concat(
                        userCatalog.Definitions.Values.OrderBy(d => d.Id).Select(d =>
                            $"{d.Id}\t{d.ElementClass}\t{d.DefinitionClass}\t{TsvText(d.Caption)}\t{d.GroupId}\t{d.GroupTypeId}\t{d.TypeId}\t{d.SpecTypeId}\t{d.Instance}\t{string.Join(",", userCatalog.BindingsOf(d.Id).Select(b => b.CategoryId + (b.Instance ? "i" : "t")))}")),
                    new System.Text.UTF8Encoding(false));
            // --dev-native <ids>: diagnostic dump of the native record (fields, parameter stores) the reader holds for the listed elements
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] != "--dev-native") continue;
                foreach (uint nativeId in args[i + 1].Split(',').Select(uint.Parse))
                {
                    if (reader.LastParsingState is not { } nativeState || !nativeState.NativeParams.TryGetValue(nativeId, out var native)) { Console.Error.WriteLine($"native {nativeId}: none"); continue; }
                    Console.Error.WriteLine($"native {nativeId}: storesComplete={native.RootParameterStoresComplete} tail={native.TailWalkComplete}");
                    foreach (var (key, value) in native.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Console.Error.WriteLine($"  f {key} = {value}");
                    foreach (var (key, value) in native.StringFields.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Console.Error.WriteLine($"  s {key} = {value}");
                    foreach (var (pid, value) in native.Doubles) Console.Error.WriteLine($"  D {pid} = {value}");
                    foreach (var (pid, value) in native.Ints) Console.Error.WriteLine($"  I {pid} = {value}");
                    foreach (var (pid, value) in native.Strings) Console.Error.WriteLine($"  S {pid} = {value}");
                    foreach (var (pid, value) in native.ElementIds) Console.Error.WriteLine($"  E {pid} = {value}");
                }
            }
            CompactJsonExporter.Export(rvtInfo, outputPath, compactSource, Console.Error, compactIdentity,
                new CompactJsonExportOptions { JsonClassNamePrefix = jsonClassPrefix });
            compactSource.Builtin.WriteReport(Console.Error);
        }
        else
        {
            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new JsonStringEnumConverter(), new RvtIO.Model.ParameterListConverter() },
            };
        #if !NET9_0_OR_GREATER
            jsonOpts.Converters.Add(new RvtIO.Cli.ShortestDoubleConverter());
        #endif

            // "Name" is the Revit API element name: keep the validated names and omit the rest (null is not written).
            // Element.Name is a fallback value that scanned strings can fill with another element's name; the IFC branch above
            // keeps reading it, so only the JSON branch replaces it.
            int validatedNames = RvtIO.Parsing.ValidatedElementNames.Apply(rvtInfo,
                reader.LastParsingState ?? throw new InvalidOperationException("The reader kept no parsing state."));
            Console.Error.WriteLine($"JSON names: {validatedNames} of {rvtInfo.Elements.Count} elements have a validated name");

            // De-duplicate parameter definitions into a shared table so each element parameter only stores its value (keyed by id).
            rvtInfo.BuildParameterDefinitionTable();

            // Stream directly to the file (avoids building one ~GB string in memory, which runs out of memory on large files).
            using (var fs = File.Create(outputPath))
                JsonSerializer.Serialize(fs, rvtInfo, jsonOpts);
            Console.Error.WriteLine($"JSON written to {outputPath} ({rvtInfo.Elements.Count} elements, " +
                $"{rvtInfo.ParameterDefinitions?.Count ?? 0} param defs)");
        }
        return 0;
    }

    // --revit-localization <dir> reads the system family names from the resource DLLs of a Revit installation
    // (<dir>/<version>/<culture>/*.dll) instead of the names embedded in the library.
    private static string? FindRevitLocalizationDirectory(string[] arguments)
    {
        int index = Array.IndexOf(arguments, "--revit-localization");
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
}
