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
}
