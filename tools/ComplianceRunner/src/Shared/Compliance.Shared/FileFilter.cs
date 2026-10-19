/// <summary>File-extension and path filters for compliance runners.</summary>
public static class FileFilter
{
    /// <summary>Returns true when a file should be processed by the given compliance check type.</summary>
    public static bool IsRelevantFile(string file, string checkType)
    {
        if (checkType == "project")
        {
            var normalized = file.Replace("\\", "/");

            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Exclude test projects and anything under a .ci/ directory.
                // These are internal tooling and are not subject to BHoM shipping conventions
                // (target framework, PostBuildEvent, AssemblyVersion, etc.).
                if (normalized.Contains("/.ci/", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(".ci/", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (Path.GetFileName(file).EndsWith(".Tests.csproj", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }

            return Path.GetFileName(file).Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
        }

        // code, copyright, documentation all operate on .cs files.
        return file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true for .json files sitting inside a directory called "datasets"
    /// (case-insensitive), except versioning upgrade maps, which are never Dataset documents.
    /// </summary>
    /// <remarks>
    /// The directory test is anchored on a whole path segment. It used to ask whether the
    /// lowercased path contained "datasets" anywhere, which is BHoMBot's dataset
    /// filter, <c>x.ToLower().Contains("datasets") &amp;&amp; x.EndsWith(".json")</c>. That
    /// selects a file because of a project directory named *_Datasets, which holds code rather
    /// than datasets: both dataset repositories carry one beside the real Datasets directory,
    /// and the only file ever placed in one failed the check for a reason nobody could act on.
    ///
    /// This now diverges from BHoMBot on two counts rather than one, the anchoring and the
    /// upgrade-map exclusion, and the two no longer agree by design.
    ///
    /// The change is preventive rather than a change of scope: every file the substring form
    /// selects already sits under a real Datasets directory, so nothing leaves scope. A path
    /// the two forms disagree about is a signal worth reading, not a regression.
    /// </remarks>
    public static bool IsDatasetFile(string file)
    {
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsUnderDatasetsDirectory(file))
            return false;

        return !IsVersioningUpgradeMap(file);
    }

    /// <summary>
    /// True when one of the file's parent directories is called "datasets", case-insensitive.
    /// </summary>
    /// <remarks>
    /// Only the directory portion is scanned, because the rule expresses "inside a directory
    /// called Datasets". Note this is a statement of intent rather than a behavioural
    /// constraint: scanning the whole path would give the same answers, since a final segment
    /// equal to "datasets" cannot also end ".json". No test can tell the two apart, so do not
    /// write one expecting it to.
    ///
    /// Both separators are handled, because paths arrive from git with '/' and from a caller on
    /// Windows with '\'. A segment comparison is more sensitive to this than the substring test
    /// it replaces, which did not care.
    /// </remarks>
    private static bool IsUnderDatasetsDirectory(string file)
    {
        var normalized = file.Replace("\\", "/");

        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
            return false;

        foreach (var segment in normalized.Substring(0, lastSlash).Split('/'))
        {
            if (segment.Equals("datasets", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True for a versioning upgrade map, named Versioning_&lt;digits&gt;.json.
    /// </summary>
    /// <remarks>
    /// These hold <c>{"Dataset":{"ToNew":..,"ToOld":..}}</c>, carry no <c>_t</c>, and can never
    /// deserialise into a BH.oM.Data.Library.Dataset. They are caught by the path test above
    /// only when some directory supplies the "datasets" substring, which in practice is a
    /// project directory named *_Datasets. The rule lives here rather than in a per-repository
    /// pathspec because a repository carrying no pathspec must still get the right answer.
    ///
    /// Anchored on the file name, not the path, so a directory named Versioning_93 does not
    /// take every file under it out of scope.
    ///
    /// The digit is what separates a generated upgrade map from a hand-authored dataset that
    /// happens to start with the same word: Versioning_Rules.json stays in scope. Discriminating
    /// on content instead, by requiring a top-level _t, was rejected: it would also skip a real
    /// Dataset document that had lost its _t, which is a defect this check exists to catch.
    /// </remarks>
    public static bool IsVersioningUpgradeMap(string file)
    {
        // Both separators are split by hand rather than through Path.GetFileName, which treats
        // '\' as one only on Windows. Paths arrive from git with '/' and from a caller on
        // Windows with '\', and the answer must not depend on which platform is asking.
        var normalized = file.Replace("\\", "/");
        int lastSlash = normalized.LastIndexOf('/');
        var name = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;

        const string prefix = "Versioning_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return name.Length > prefix.Length && char.IsAsciiDigit(name[prefix.Length]);
    }
}
