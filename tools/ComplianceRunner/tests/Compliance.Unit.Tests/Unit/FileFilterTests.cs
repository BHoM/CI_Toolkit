using NUnit.Framework;

[TestFixture]
public class FileFilterTests
{
    [TestFixture]
    public class IsRelevantFileTests
    {
        [TestCase("code",          "MyClass.cs",            ExpectedResult = true)]
        [TestCase("copyright",     "MyClass.cs",            ExpectedResult = true)]
        [TestCase("documentation", "MyClass.cs",            ExpectedResult = true)]
        [TestCase("code",          "MyClass.CS",            ExpectedResult = true)]   // extension case-insensitive
        [TestCase("code",          "MyProject.csproj",      ExpectedResult = false)]
        [TestCase("code",          "readme.md",             ExpectedResult = false)]
        [TestCase("project",       "MyProject.csproj",                          ExpectedResult = true)]
        [TestCase("project",       "MyProject.CSPROJ",                          ExpectedResult = true)]  // extension case-insensitive
        [TestCase("project",       "AssemblyInfo.cs",                           ExpectedResult = true)]
        [TestCase("project",       "assemblyinfo.cs",                           ExpectedResult = true)]  // filename case-insensitive
        [TestCase("project",       "src/AssemblyInfo.cs",                       ExpectedResult = true)]  // works with a leading path
        [TestCase("project",       "NotAssemblyInfo.cs",                        ExpectedResult = false)]
        [TestCase("project",       "MyClass.cs",                                ExpectedResult = false)]
        [TestCase("project",       ".ci/unit-tests/Foo.Tests.csproj",           ExpectedResult = false)] // under .ci/ — excluded
        [TestCase("project",       "src/Foo.Tests.csproj",                      ExpectedResult = false)] // *.Tests.csproj — excluded
        [TestCase("project",       @".ci\unit-tests\Bar.Tests.csproj",          ExpectedResult = false)] // backslash path, .ci/ excluded
        public bool IsRelevantFile(string checkType, string file)
            => FileFilter.IsRelevantFile(file, checkType);
    }

    [TestFixture]
    public class IsDatasetFileTests
    {
        [TestCase("a/datasets/foo.json",      ExpectedResult = true)]
        [TestCase("a/Datasets/foo.json",      ExpectedResult = true)]  // case-insensitive
        [TestCase("a/DATASETS/foo.json",      ExpectedResult = true)]  // case-insensitive
        [TestCase(@"a\datasets\foo.json",     ExpectedResult = true)]  // backslash separators
        [TestCase("a/datasets/foo.JSON",      ExpectedResult = true)]  // extension case-insensitive
        [TestCase("a/notdatasets/foo.json",   ExpectedResult = true)]  // bare substring match — mirrors BHoMBot
        [TestCase("a/datasets/foo.cs",        ExpectedResult = false)] // wrong extension
        [TestCase("foo.json",                 ExpectedResult = false)] // no "datasets" substring
        [TestCase("datasets/foo.json",        ExpectedResult = true)]  // root-level
        [TestCase("DataSets/foo.json",        ExpectedResult = true)]  // root-level, mixed case
        [TestCase("DataSets/LCA/deep/x.json", ExpectedResult = true)]  // root-level, nested

        // Versioning upgrade maps. A project directory supplying the "datasets" substring is
        // the only way a file named like this is selected at all, and BHoM_Datasets has that
        // layout: one .csproj in a directory whose name carries the substring.
        [TestCase("BHoM_Datasets/Versioning_93.json",        ExpectedResult = false)]
        [TestCase("BHoM_Datasets/Versioning_100.json",       ExpectedResult = false)]
        [TestCase(@"BHoM_Datasets\Versioning_93.json",       ExpectedResult = false)] // backslash separators
        [TestCase("Datasets/Versioning_9.json",              ExpectedResult = false)] // single digit
        [TestCase("a/Datasets/deep/Versioning_93.json",      ExpectedResult = false)] // at depth
        [TestCase("BHoM_Datasets/versioning_93.json",        ExpectedResult = false)] // name case-insensitive
        [TestCase("BHoM_Datasets/Versioning_93.JSON",        ExpectedResult = false)] // extension case-insensitive

        // The digit gate. A hand-authored dataset starting with the same word stays in scope,
        // which is the whole reason the rule is not a bare Versioning_* match.
        [TestCase("Datasets/Versioning_Rules.json",          ExpectedResult = true)]
        [TestCase("BHoM_Datasets/Versioning_.json",          ExpectedResult = true)]
        [TestCase("BHoM_Datasets/Versioning.json",           ExpectedResult = true)]

        // Anchored on the file name, so a directory named for a version does not take the real
        // datasets under it out of scope.
        [TestCase("Datasets/Versioning_93/RealDataset.json", ExpectedResult = true)]

        // The prefix has to start the name. A dataset merely containing the word is unaffected.
        [TestCase("Datasets/MyVersioning_93.json",           ExpectedResult = true)]

        // Pins the anchoring itself rather than the digit test. The name is contrived on
        // purpose: the prefix has to appear at an offset AND a digit has to sit at the index
        // the digit test reads, which is the only way the two can disagree. Without it, a
        // relaxed prefix match still passes every realistic fixture above, because reading a
        // fixed index lands inside the prefix whenever the prefix is not at the start.
        [TestCase("Datasets/012345678901Versioning_5.json",  ExpectedResult = true)]
        public bool IsDatasetFile(string file)
            => FileFilter.IsDatasetFile(file);

        // The upgrade-map rule on its own, so a failure says which of the two predicates moved.
        // These paths carry no "datasets" substring, so IsDatasetFile rejects them anyway and
        // could not distinguish the two.
        [TestCase("Structure_oM/Versioning_93.json",   ExpectedResult = true)]
        [TestCase("Structure_oM/Versioning_Rules.json", ExpectedResult = false)]
        [TestCase("Versioning_93.json",             ExpectedResult = true)]  // no directory at all
        [TestCase("Structure_oM/Versioning_93.txt", ExpectedResult = true)]  // extension is IsDatasetFile's job
        public bool IsVersioningUpgradeMap(string file)
            => FileFilter.IsVersioningUpgradeMap(file);
    }
}
