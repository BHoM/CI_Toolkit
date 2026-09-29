using System.Text.Json;

namespace VersioningRunner.Commands;

// The dataset root cannot supply a map at all.
//
// Not thrown for an empty map. A root that exists and whose records carry no `_asm` is the
// state before the backfill lands, and it has to read as inert rather than broken.
public sealed class DatasetProvenanceException : Exception
{
    public DatasetProvenanceException(string message) : base(message) { }
}

// One object record's declaring assembly, keyed by the type name the runner sees on a leaf.
//
// `TypesMapped` counts agreed entries only. `RecordsRead` counts every non-blank line,
// including those carrying no `_asm`, because it is the denominator that lets a run say
// "0 of 40375 records carry the field" rather than reporting an empty map with no context.
public sealed record DeclaringAssemblyMap(
    IReadOnlyDictionary<string, string> ByTypeName,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Disputed,
    int VersionsRead,
    int RecordsRead,
    int TypesMapped,
    int LinesUnparseable)
{
    public static DeclaringAssemblyMap Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        0, 0, 0, 0);

    // Null when the dataset names no assembly for the type, and null when versions disagree
    // about which. A disagreement is not resolved by picking one: the caller falls back to the
    // namespace guess, which is at least recorded as a guess.
    public string? DeclaringAssemblyFor(string? typeFullName)
        => !string.IsNullOrEmpty(typeFullName) && ByTypeName.TryGetValue(typeFullName, out string? assembly)
            ? assembly
            : null;
}

// Where a failing object record's declaring assembly comes from.
//
// The record carries it, in the `_asm` field, and ci-versioning stages the dataset to a known
// path on the same machine in the same job: the action asserts that directory exists and
// enumerates its version folders in a step before the runner runs. So this reads the same file
// the versioning test read, rather than depending on the test to restate the field in a message
// the runner then parses back out.
//
// Only Objects.json is read. Methods.json and Adapters.json carry
// `_t: System.Reflection.MethodBase` on every record, so `_t` identifies nothing there, and
// their leaves already reach the runner with a declaring assembly from the Method event.
public static class DatasetProvenance
{
    /*************************************/
    /**** Public Methods              ****/
    /*************************************/

    // Every version directory present, not the newest.
    //
    // A pull request reads one version, but which one is a property of Versioning_Toolkit's
    // source rather than of anything the runner can see, and deriving it would mean regexing a
    // version list out of FromJson.cs. Reading the union costs nothing and is what makes the
    // answer independent of that. Measured on the two versions carrying the field: 1711 shared
    // type names, 0 disagreements. Measured on the 15 saved run artefacts: 203 of 205
    // object-record findings resolve against 9.3 and the other 2 only against 9.2, so a
    // newest-only read would have missed both, and both are NoMethodEvent findings with no
    // declaring assembly, which is precisely the population this field exists to serve.
    public static DeclaringAssemblyMap Build(string? datasetsRoot)
    {
        if (string.IsNullOrWhiteSpace(datasetsRoot))
            throw new DatasetProvenanceException("No versioning dataset path was supplied.");

        if (!Directory.Exists(datasetsRoot))
            throw new DatasetProvenanceException(
                $"Versioning dataset directory not found at {datasetsRoot}. The build of " +
                "Verification.sln stages it through Versioning_Test.csproj's PostBuild step, so its " +
                "absence means that build did not run or did not stage.");

        List<string> objectFiles = Directory.EnumerateDirectories(datasetsRoot)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "Objects.json"))
            .Where(File.Exists)
            .ToList();

        if (objectFiles.Count == 0)
            throw new DatasetProvenanceException(
                $"No version directory under {datasetsRoot} contains an Objects.json. The directory " +
                "exists but holds no object records, so no finding could be attributed by declaring " +
                "assembly and every one would fall back to the namespace guess.");

        // Ordinal-sorted so a disputed entry's candidate list, and the artefact it lands in, do
        // not depend on directory enumeration order.
        var seen = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        int records = 0;
        int unparseable = 0;

        foreach (string file in objectFiles)
        {
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                records++;

                string? type;
                string? assembly;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        unparseable++;
                        continue;
                    }

                    type = StringProperty(document.RootElement, "_t");
                    assembly = StringProperty(document.RootElement, "_asm");
                }
                catch (JsonException)
                {
                    // Counted and reported by the caller rather than thrown. A record the
                    // dataset cannot express is the dataset's problem, and the versioning test
                    // reading the same line produces its own finding for it; losing the whole
                    // map over one line would convert that into a fleet-wide fallback.
                    unparseable++;
                    continue;
                }

                if (type is null || assembly is null)
                    continue;

                if (!seen.TryGetValue(type, out SortedSet<string>? assemblies))
                    seen[type] = assemblies = new SortedSet<string>(StringComparer.Ordinal);

                assemblies.Add(assembly);
            }
        }

        var agreed = new Dictionary<string, string>(StringComparer.Ordinal);
        var disputed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach ((string type, SortedSet<string> assemblies) in seen)
        {
            if (IsOneAssemblyFamily(assemblies))
                agreed[type] = assemblies.First();
            else
                disputed[type] = assemblies.ToArray();
        }

        return new DeclaringAssemblyMap(agreed, disputed, objectFiles.Count, records, agreed.Count, unparseable);
    }

    /*************************************/
    /**** Private Methods             ****/
    /*************************************/

    // Two versions naming Revit_X_oM_2022 and Revit_X_oM_2023 are not disagreeing.
    // They are the same repository under two build configurations, attribution strips the year
    // anyway, and treating the pair as disputed would drop the finding to the namespace guess
    // for no gain. That is the failure "compare year-insensitively" exists to prevent, applied
    // one layer earlier. Different families are a real disagreement and stay disputed.
    private static bool IsOneAssemblyFamily(SortedSet<string> assemblies)
        => assemblies.Count == 1
        || assemblies.Select(RunCommand.StripConfigSuffix).Distinct(StringComparer.Ordinal).Count() == 1;

    // A field present but not a string, or present and blank, reads the same as absent. Both
    // leave the caller on its existing path rather than mapping the type to nothing.
    private static string? StringProperty(JsonElement record, string name)
    {
        if (!record.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /*************************************/
}
