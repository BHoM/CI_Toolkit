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
    /// Returns true for .json files whose path contains "datasets" (case-insensitive),
    /// except versioning upgrade maps, which are never Dataset documents.
    /// </summary>
    /// <remarks>
    /// The path test is BHoMBot's DatasetCompliance filter,
    /// <c>x.ToLower().Contains("datasets") &amp;&amp; x.EndsWith(".json")</c>. The upgrade-map
    /// exclusion is a deliberate divergence from it, so the two no longer agree.
    /// </remarks>
    public static bool IsDatasetFile(string file)
    {
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!file.Contains("datasets", StringComparison.OrdinalIgnoreCase))
            return false;

        return !IsVersioningUpgradeMap(file);
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
