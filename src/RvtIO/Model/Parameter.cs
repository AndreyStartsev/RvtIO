using System.Text.Json.Serialization;

namespace RvtIO.Model
{
    /// <summary>
    /// An element parameter - mirrors the Revit API Parameter class.
    /// Value is typed according to StorageType: double, int, string, or int (ElementId).
    /// </summary>
    public class Parameter
    {
        /// <summary>A copy of this parameter with another value.</summary>
        internal Parameter ShallowCopy(object? value)
        {
            var copy = (Parameter)MemberwiseClone();
            copy._value = value;
            return copy;
        }

        private object? _value;

        /// <summary>GUID of shared/external parameters (parsed from ForgeId). Null for built-in params.
        /// Invariant per definition id - serialized once in the document's ParameterDefinitions table.</summary>
        [JsonIgnore]
        public Guid? Guid { get; set; }

        /// <summary>
        /// For built-in params: the BIP enum int value (same as Definition.Id, negative).
        /// For user-defined params: the param definition element ID (positive).
        /// This is the key into the document's ParameterDefinitions table.
        /// </summary>
        public int Id { get; set; }

        /// <summary>Definition (name/BIP). Invariant per <see cref="Id"/>; hoisted into the
        /// document-level ParameterDefinitions table for JSON, so it is not serialized inline.</summary>
        [JsonIgnore]
        public Definition Definition { get; set; } = new();

        /// <summary>Storage type. Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public StorageType StorageType { get; set; }

        /// <summary>
        /// Typed value: double, int, string, or int (ElementId).
        /// Null when the value could not be resolved from the binary data.
        /// This is the only per-element-instance field of a parameter.
        /// </summary>
        public object? Value
        {
            get => _value;
            set
            {
                _value = value;
                HasNoRevitValue = false;
            }
        }

        /// <summary>True if the parameter is read-only (computed, system-generated, etc.).
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public bool IsReadOnly { get; set; }

        /// <summary>Forge TypeId string for user-defined/shared params (e.g. "autodesk.parameter.aec.revit.external.-1:GUID-1.0.0").
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? ForgeId { get; set; }

        /// <summary>Measurement spec type (e.g. "autodesk.spec.aec:length-2.0.0").
        /// Resolved from ForgeId via 0x0B3A blocks, or from BIP name heuristics.</summary>
        [JsonIgnore]
        public string? SpecType { get; set; }

        /// <summary>Display unit symbol (e.g. "mm", "m^2", "degC").
        /// Resolved from SpecType via 0x0FBD ForgeTypeId spec registry.
        /// Invariant per definition id - lives in ParameterDefinitions.</summary>
        [JsonIgnore]
        public string? UnitSymbol { get; set; }

        /// <summary>Conversion factor from Revit internal units to display units.
        /// E.g. for length: feet -> mm = 304.8.</summary>
        [JsonIgnore]
        public double? ConversionFactor { get; set; }

        /// <summary>True when this parameter's value came from an approximate fallback
        /// (e.g. dominant bounding-box dimension) rather than an exact decoded source (e.g.
        /// the element's serialized location curve). Per-instance, like <see cref="Value"/> - not invariant
        /// per definition id. Defaults to false (exact).</summary>
        [JsonIgnore]
        public bool IsApproximate { get; set; }

        /// <summary>The value was set by <c>ApplyNativeParams</c>: a parameter store of the element or a rule over decoded fields.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool FromNativeFields { get; set; }

        /// <summary>True when the parameter slot exists on the element with a synthesized default or
        /// unset sentinel, rather than a value stored or computed in Revit.</summary>
        [JsonIgnore]
        internal bool HasNoRevitValue { get; set; }

        /// <summary>True when <paramref name="elementId"/> is a resolved reference or valid option value rather than
        /// an invalid (<c>-1</c>), uninitialized (<c>0</c>), or unhosted host-pointer sentinel.</summary>
        internal static bool IsSetElementId(int parameterId, int elementId) =>
            parameterId is -1002108 or -1010303
                ? elementId > 0
                : elementId != -1 && elementId != 0;

        /// <summary>
        /// Whether this parameter carries a value stored or computed in Revit rather than an unset sentinel,
        /// synthesized reader placeholder, or approximate fallback (<see cref="IsApproximate"/>).
        /// Unlike <see cref="HasValue"/> (which checks <c>Value != null</c> on the raw decoded slot), this is
        /// <c>false</c> for <see cref="StorageType.None"/>, non-finite or <c>|v| &gt;= 1e29</c> doubles,
        /// unresolved element ids (<c>-1</c> and <c>0</c>, or non-positive pointers on host-id parameters),
        /// <c>-1</c> placeholders on non-integer built-in parameters, and unstored default slots synthesized by
        /// the reader, while remaining <c>true</c> for stored <c>0</c>, <c>0.0</c>, <c>false</c>, <c>""</c>,
        /// and valid negative <see cref="StorageType.ElementId"/> option or category values.
        /// </summary>
        [JsonIgnore]
        public bool HasRevitValue
        {
            get
            {
                if (HasNoRevitValue || IsApproximate || _value == null || StorageType == StorageType.None)
                    return false;
                return StorageType switch
                {
                    StorageType.Double => _value is double d && double.IsFinite(d) && Math.Abs(d) < 1e29,
                    StorageType.ElementId => _value is int eid && IsSetElementId(Id, eid),
                    StorageType.Integer => _value is int iv
                        ? !(iv == -1 && (Id is >= -1019017 and <= -1019012
                            || (Id < 0 && Parsing.ApiParameterDefinitions.TryGetStorage(Id, out var apiSt) && apiSt != StorageType.Integer)))
                        : _value is bool,
                    StorageType.String => _value is string,
                    _ => false,
                };
            }
        }

        /// <summary>Whether this parameter has a non-null value.</summary>
        [JsonIgnore]
        public bool HasValue => Value != null;

        /// <summary>Get the value as double. Returns 0 if not a Double parameter or value is null.</summary>
        public double AsDouble()
        {
            if (Value is double d) return d;
            if (Value is int i) return i;
            return 0;
        }

        /// <summary>Get the value as integer. Returns 0 if not an Integer parameter or value is null.</summary>
        public int AsInteger()
        {
            if (Value is int i) return i;
            if (Value is double d) return (int)d;
            return 0;
        }

        /// <summary>Get the value as string. Returns null if not a String parameter.</summary>
        public string? AsString()
        {
            if (Value is string s) return s;
            return Value?.ToString();
        }

        /// <summary>Get the value as ElementId (int). Returns -1 (InvalidElementId) if not an ElementId parameter.</summary>
        public int AsElementId()
        {
            if (Value is int i) return i;
            return -1;
        }

        /// <summary>
        /// Get a formatted display string for the value.
        /// Uses ForgeTypeId spec registry for precise unit conversion when available,
        /// falls back to parameter name heuristics.
        /// </summary>
        public string? AsValueString()
        {
            if (Value == null) return null;
            if (Value is double d)
            {
                // Use ForgeTypeId-derived unit info if available
                if (UnitSymbol != null && ConversionFactor.HasValue && ConversionFactor.Value != 0)
                {
                    double displayed = d * ConversionFactor.Value;
                    return $"{displayed:G6} {UnitSymbol}";
                }

                // Fallback: heuristic based on parameter name
                string? name = Definition?.Name;
                if (name != null && (name.Contains("WIDTH") || name.Contains("HEIGHT")
                    || name.Contains("LENGTH") || name.Contains("OFFSET")
                    || name.Contains("THICKNESS") || name.Contains("DEPTH")))
                {
                    return $"{d * 304.8:F1} mm";
                }
                if (name != null && name.Contains("AREA"))
                    return $"{d * 0.09290304:F3} m²";
                if (name != null && name.Contains("VOLUME"))
                    return $"{d * 0.0283168466:F4} m³";
                if (name != null && name.Contains("ANGLE"))
                    return $"{d * 180 / Math.PI:F2}°";
                return d.ToString("G");
            }
            return Value.ToString();
        }
    }
}
