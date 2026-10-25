using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// End-to-end tests for DatasetComplianceRunner (dataset compliance checks).
///
/// Tests marked [Category("RequiresBHoM")] invoke the BHoM engine and need BHoM installed.
/// Tests without that category exercise filtering/argument paths that return before any BHoM
/// call is made, so they work on any machine with the solution already built.
/// </summary>
[TestFixture]
[Category("Integration")]
public class DatasetComplianceRunnerE2ETests
{
    // ── Usage / bad args ───────────────────────────────────────────────────────

    [Test]
    public void NoArgs_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("DatasetComplianceRunner");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    // ── File filtering — no BHoM call made ────────────────────────────────────

    [Test]
    [Description(".json files that do not sit inside a directory called 'datasets' (case-insensitive) are filtered before BHoM is called.")]
    public void NonDatasetJsonFile_JsonOutput_ExitsWithCode0AndPassStatus()
    {
        // No segment of this path equals "datasets", so IsDatasetFile filters it out.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "notadataset/foo.json");

        Assert.That(exitCode, Is.EqualTo(0));
        var json = JsonDocument.Parse(stdout);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("status").GetString(),         Is.EqualTo("Pass"));
            Assert.That(json.RootElement.GetProperty("annotationCount").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    [Description("Non-.json file is filtered before BHoM is called.")]
    public void NonJsonFile_ExitsWithCode0()
    {
        var (exitCode, _) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "a/datasets/file.cs");
        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    [Description("A versioning upgrade map inside a real Datasets directory is filtered before BHoM is called, and counts as not relevant rather than as an examined file.")]
    public void VersioningUpgradeMap_UnderADatasetsDirectory_IsNotExamined()
    {
        // Inside a real Datasets directory, so the file name is the only thing taking it out of
        // scope. Under a *_Datasets project directory the anchoring would filter it first and
        // this would stop testing the upgrade-map rule at all.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "Datasets/Versioning_93.json");

        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(root.GetProperty("status").GetString(),         Is.EqualTo("Pass"));
            Assert.That(root.GetProperty("annotationCount").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    [Description("The digit gate holds end to end: Versioning_Rules.json is still selected, so it reaches the BHoM call and is reported as missing on disk rather than filtered.")]
    public void VersioningRulesJson_IsStillInScope()
    {
        // Distinguishes "filtered out" from "examined": a filtered file never reaches the
        // File.Exists branch, so the [SKIP] line is what proves this one was not filtered.
        // Inside a real Datasets directory for the same reason as the test above.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "Datasets/Versioning_Rules.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("[SKIP] File not found"));
        });
    }

    [Test]
    [Description("A .json file in a *_Datasets project directory is filtered before BHoM is called, on the directory anchoring alone rather than on its name.")]
    public void FileInADatasetsProjectDirectory_IsNotExamined()
    {
        // A project directory named *_Datasets holds code, not datasets. This name carries no
        // upgrade-map signal, so the anchoring is the only thing that can take it out of scope.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "BHoM_Datasets/Rules.json");

        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(root.GetProperty("status").GetString(),         Is.EqualTo("Pass"));
            Assert.That(root.GetProperty("annotationCount").GetInt32(), Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("[SKIP] File not found"));
        });
    }

    // ── JSON output structure ─────────────────────────────────────────────────

    [Test]
    public void JsonOutput_ContainsAllExpectedTopLevelKeys()
    {
        var (_, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "notadataset/foo.json");
        var root = JsonDocument.Parse(stdout).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.TryGetProperty("status",          out _), Is.True, "status");
            Assert.That(root.TryGetProperty("checkType",       out _), Is.True, "checkType");
            Assert.That(root.TryGetProperty("title",           out _), Is.True, "title");
            Assert.That(root.TryGetProperty("summary",         out _), Is.True, "summary");
            Assert.That(root.TryGetProperty("annotationCount", out _), Is.True, "annotationCount");
            Assert.That(root.TryGetProperty("annotations",     out _), Is.True, "annotations");
        });
    }

    [Test]
    public void JsonOutput_CheckType_IsDataset()
    {
        var (_, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "json", "notadataset/foo.json");
        var checkType = JsonDocument.Parse(stdout).RootElement.GetProperty("checkType").GetString();
        Assert.That(checkType, Is.EqualTo("dataset"));
    }

    // ── GitHub Actions output format ──────────────────────────────────────────

    [Test]
    public void GitHubOutput_ForPassingRun_ProducesNoFindingAnnotations()
    {
        // This previously asserted stdout was entirely empty. That silence was the defect: a run
        // that examined nothing looked exactly like a run that examined everything. What a
        // passing run must not produce is a *finding* annotation, which is what the name means
        // and what is asserted now. The coverage output is not a finding and changes no verdict.
        var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
            "--output", "github", "notadataset/foo.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("::error"));
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined"));
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"));
        });
    }

    // ── A file the engine throws on ───────────────────────────────────────────

    /// <summary>
    /// Writes a dataset file and a sibling under a real Datasets directory, so both pass the
    /// filter and reach the engine, and returns the pair with the directory to clean up.
    ///
    /// The sibling carries a genuine finding. Without it a test cannot tell "the run continued"
    /// from "the run died quietly".
    /// </summary>
    private static (string Target, string Sibling, string Dir) WriteDatasetPair()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bhom-ci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Datasets"));

        string target = Path.Combine(dir, "Datasets", "Target.json");
        File.WriteAllText(target, "{ \"_t\" : \"BH.oM.Data.Library.Dataset\" }");

        string sibling = Path.Combine(dir, "Datasets", "Sibling.json");
        File.WriteAllText(sibling, "not a dataset");

        return (target, sibling, dir);
    }

    [Test]
    [Category("RequiresBHoM")]
    [Platform("Win", Reason = "FileShare.None only blocks another process on Windows; on Unix .NET's sharing mode is advisory, so the file would open and nothing would throw.")]
    [Description("A dataset file the engine throws on fails the run, names itself, and does not stop the other files being reported.")]
    public void UnanalysableFile_FailsTheRunAndIsNamed_WithoutLosingOtherFindings()
    {
        var (target, sibling, dir) = WriteDatasetPair();
        try
        {
            // Held open with no sharing, which is what an antivirus scan or a concurrent step
            // does. IsValidDataset opens the file with an unguarded StreamReader before it
            // parses anything, so this reaches the throw without needing a malformed dataset,
            // and the file stays a valid one so the test is not really about its content.
            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
                    "--output", "github", target, sibling);

                Assert.Multiple(() =>
                {
                    Assert.That(exitCode, Is.EqualTo(1), "a file the engine could not analyse must fail the run");
                    Assert.That(stdout, Does.Contain("Target.json"),
                        "the failing file must be named, or the author cannot act on the red");
                    Assert.That(stdout, Does.Contain("could not be analysed"));
                    Assert.That(stdout, Does.Contain("1 of 2 file(s) examined"),
                        "the coverage line must survive, which it cannot if the process died");
                    Assert.That(stdout, Does.Contain("did not deserialise"),
                        "the other file's finding must still be reported; losing them was the defect");
                });
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Platform("Win", Reason = "FileShare.None only blocks another process on Windows; on Unix .NET's sharing mode is advisory, so the file would open and nothing would throw.")]
    [Description("A run whose only dataset could not be analysed still fails, on the failure alone.")]
    public void UnanalysableFileAlone_StillFailsTheRun()
    {
        // Separate from the test above because that one cannot see this. Its sibling carries a
        // real finding, so the run exits 1 on the merged Error whether or not failures count
        // towards the exit code. With nothing else in the run the exit code here rests on the
        // failure count and nothing else.
        var (target, _, dir) = WriteDatasetPair();
        try
        {
            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner",
                    "--output", "github", target);

                Assert.Multiple(() =>
                {
                    Assert.That(exitCode, Is.EqualTo(1),
                        "a run that could analyse nothing must not report success");
                    Assert.That(stdout, Does.Contain("0 of 1 file(s) examined"));
                    Assert.That(stdout, Does.Not.Contain("reports success without having inspected anything"),
                        "that warning is for a green that examined nothing, and this run is red");
                });
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    // ── Confidence severity by dataset kind ──────────────────────────────────

    /// <summary>
    /// Writes one dataset under <paramref name="relativeDirectory"/> and returns it with the
    /// root to clean up. The content is the same every time, so the only variable across these
    /// tests is where the file sits.
    /// </summary>
    private static (string File, string Root) WriteDataset(string relativeDirectory, string name, string json)
    {
        string root = Path.Combine(Path.GetTempPath(), "bhom-ci-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);

        string file = Path.Combine(directory, name);
        File.WriteAllText(file, json);
        return (file, root);
    }

    /// <summary>A dataset that deserialises and leaves Confidence unset, which is the rule under test.</summary>
    private const string ConfidenceUnsetDataset =
        "{ \"_t\" : \"BH.oM.Data.Library.Dataset\", \"Name\" : \"Probe\", "
      + "\"SourceInformation\" : { \"_t\" : \"BH.oM.Data.Library.Source\", "
      + "\"Title\" : \"Probe title\", \"Author\" : \"Probe author\", \"Confidence\" : \"Undefined\" }, "
      + "\"Data\" : [] }";

    [Test]
    [Category("RequiresBHoM")]
    [Description("The same dataset blocks under Datasets/ and only warns under .ci/Datasets/. One behaviour, both halves.")]
    [TestCase("Datasets",     1, "failure", TestName = "ConfidenceUnset_InAProductionDataset_FailsTheRun")]
    [TestCase(".ci/Datasets", 0, "warning", TestName = "ConfidenceUnset_InAUnitTestFixture_OnlyWarns")]
    public void ConfidenceSeverity_DependsOnWhereTheDatasetSits(
        string directory, int expectedExitCode, string expectedLevel)
    {
        var (file, root) = WriteDataset(directory, "Conf.json", ConfidenceUnsetDataset);
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "json", file);
            var payload = JsonDocument.Parse(stdout).RootElement;

            var confidence = payload.GetProperty("annotations").EnumerateArray()
                .Single(a => a.GetProperty("ruleName").GetString() == "Dataset confidence error");

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(expectedExitCode));
                Assert.That(confidence.GetProperty("level").GetString(), Is.EqualTo(expectedLevel));
                Assert.That(payload.GetProperty("coverage").GetProperty("examined").GetInt32(), Is.EqualTo(1),
                    "the demotion must not change what is selected or examined");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("A fixture that does not deserialise still fails. Only the Confidence rule is demoted, and only its severity.")]
    public void AFixtureThatDoesNotDeserialise_StillFailsTheRun()
    {
        var (file, root) = WriteDataset(".ci/Datasets", "Bad.json", "not a dataset");
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "json", file);
            var payload = JsonDocument.Parse(stdout).RootElement;

            var deserialisation = payload.GetProperty("annotations").EnumerateArray()
                .Single(a => a.GetProperty("ruleName").GetString() == "Dataset deserialisation error");

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(payload.GetProperty("status").GetString(), Is.EqualTo("Error"));
                Assert.That(deserialisation.GetProperty("level").GetString(), Is.EqualTo("failure"));
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("A fixture's missing title is still a warning. The other source rules are untouched by the split.")]
    public void AFixturesOtherSourceWarnings_AreUnchanged()
    {
        const string noTitle =
            "{ \"_t\" : \"BH.oM.Data.Library.Dataset\", \"Name\" : \"Probe\", "
          + "\"SourceInformation\" : { \"_t\" : \"BH.oM.Data.Library.Source\", "
          + "\"Author\" : \"a\", \"Confidence\" : \"High\" }, \"Data\" : [] }";

        var (file, root) = WriteDataset(".ci/Datasets", "NoTitle.json", noTitle);
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "json", file);
            var payload = JsonDocument.Parse(stdout).RootElement;

            var title = payload.GetProperty("annotations").EnumerateArray()
                .Single(a => a.GetProperty("ruleName").GetString() == "Dataset source title error");

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(title.GetProperty("level").GetString(), Is.EqualTo("warning"));
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("A demoted finding and a real one in the same run: the run is red and the demotion did not swallow either.")]
    public void ADemotedFixtureAlongsideAFailingOne_StillFailsTheRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "bhom-ci-" + Guid.NewGuid().ToString("N"));
        string fixtures = Path.Combine(root, ".ci", "Datasets");
        Directory.CreateDirectory(fixtures);

        string demoted = Path.Combine(fixtures, "Conf.json");
        File.WriteAllText(demoted, ConfidenceUnsetDataset);
        string broken = Path.Combine(fixtures, "Bad.json");
        File.WriteAllText(broken, "not a dataset");

        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "json", demoted, broken);
            var payload = JsonDocument.Parse(stdout).RootElement;

            var levels = payload.GetProperty("annotations").EnumerateArray()
                .Where(a => a.GetProperty("ruleName").GetString()!.Length > 0)
                .ToDictionary(a => a.GetProperty("ruleName").GetString()!,
                              a => a.GetProperty("level").GetString());

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(levels["Dataset confidence error"], Is.EqualTo("warning"));
                Assert.That(levels["Dataset deserialisation error"], Is.EqualTo("failure"));
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("A green that demoted something says so, rather than looking like a run that found nothing.")]
    public void ADemotedRun_ReportsWhatItDowngraded()
    {
        var (file, root) = WriteDataset(".ci/Datasets", "Conf.json", ConfidenceUnsetDataset);
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "github", file);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(stdout, Does.Contain("::notice title=Dataset confidence::1 Confidence finding"));
                Assert.That(stdout, Does.Contain("The field is still expected on those datasets."));
                Assert.That(stdout, Does.Not.Contain("under .ci/Datasets"),
                    "the notice must describe the scope the predicate applies, which is any .ci descendant");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("json and sarif carry a payload on stdout, so the demotion notice must not precede it.")]
    [TestCase("json")]
    [TestCase("sarif")]
    public void TheDemotionNotice_DoesNotContaminateAMachineReadablePayload(string format)
    {
        var (file, root) = WriteDataset(".ci/Datasets", "Conf.json", ConfidenceUnsetDataset);
        try
        {
            var (_, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", format, file);

            Assert.Multiple(() =>
            {
                Assert.That(stdout, Does.Not.Contain("Confidence finding(s) in unit-test fixtures"));
                Assert.That(() => JsonDocument.Parse(stdout), Throws.Nothing,
                    "stdout must be the payload and nothing else, which is the property at risk here");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("SARIF carries the demoted finding at warning too. The level is mapped in a second place.")]
    public void ADemotedFinding_IsAWarningInSarifAsWell()
    {
        var (file, root) = WriteDataset(".ci/Datasets", "Conf.json", ConfidenceUnsetDataset);
        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "sarif", file);
            var results = JsonDocument.Parse(stdout).RootElement
                .GetProperty("runs")[0].GetProperty("results");

            var confidence = results.EnumerateArray()
                .Single(r => r.GetProperty("ruleId").GetString()!.Contains("Dataset confidence error"));

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(0));
                Assert.That(confidence.GetProperty("level").GetString(), Is.EqualTo("warning"),
                    "SarifBuilder maps the level separately from the GitHub emitter, so it needs its own assertion");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    /// <summary>
    /// Pins the rule name the demotion keys on against the engine that actually produces it.
    ///
    /// DatasetSeverity matches a string owned by Test_Toolkit. If that name is reworded upstream
    /// the demotion silently stops matching, every fixture goes back to blocking, and nothing
    /// points at the cause. This fails the build here instead. It deliberately uses the literal
    /// rather than the constant on one side, so a change to the constant cannot make it pass.
    /// </summary>
    [Test]
    [Category("RequiresBHoM")]
    [Description("The engine still calls the Confidence finding what DatasetSeverity expects.")]
    public void TheEngineStillNamesTheConfidenceRuleWhatTheDemotionExpects()
    {
        var (file, root) = WriteDataset("Datasets", "Conf.json", ConfidenceUnsetDataset);
        try
        {
            var (_, stdout) = RunnerFixture.Run("DatasetComplianceRunner", "--output", "json", file);
            var payload = JsonDocument.Parse(stdout).RootElement;

            var ruleNames = payload.GetProperty("annotations").EnumerateArray()
                .Select(a => a.GetProperty("ruleName").GetString())
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(ruleNames, Does.Contain("Dataset confidence error"),
                    "the engine's name for this finding has changed; DatasetSeverity.ConfidenceRule no longer matches it");
                Assert.That(DatasetSeverity.ConfidenceRule, Is.EqualTo("Dataset confidence error"),
                    "the constant has been edited away from the name the engine emits");
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

}
