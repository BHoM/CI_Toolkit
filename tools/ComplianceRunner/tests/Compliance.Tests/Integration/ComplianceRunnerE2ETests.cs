using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// End-to-end tests for ComplianceRunner (code / copyright / documentation / project checks).
///
/// Tests marked [Category("RequiresBHoM")] invoke the BHoM engine and need BHoM installed.
/// Tests without that category exercise filtering/argument paths that return before any BHoM
/// call is made, so they work on any machine with the solution already built.
/// </summary>
[TestFixture]
[Category("Integration")]
public class ComplianceRunnerE2ETests
{
    // ── Usage / bad args ───────────────────────────────────────────────────────

    [Test]
    public void NoArgs_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void MissingFiles_ExitsWithCode1()
    {
        // Only a check type provided, no files — runner should print usage and exit 1.
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner", "code");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public void UnknownCheckType_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner", "unknown", "file.cs");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    // ── File filtering — no BHoM call made ────────────────────────────────────

    [Test]
    [Description("Non-.cs files given to a code check are filtered out before the BHoM engine is called.")]
    public void NonRelevantFiles_JsonOutput_ExitsWithCode0AndPassStatus()
    {
        // Pass a file whose extension is not relevant for "code" compliance.
        // The runner filters by extension first, so File.Exists is never checked
        // and BHoM is never invoked.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "nonexistent.md");

        Assert.That(exitCode, Is.EqualTo(0));
        var json = JsonDocument.Parse(stdout);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("status").GetString(),          Is.EqualTo("Pass"));
            Assert.That(json.RootElement.GetProperty("annotationCount").GetInt32(),  Is.EqualTo(0));
        });
    }

    [Test]
    [Description("Non-.csproj / non-AssemblyInfo files for project check are filtered before BHoM is called.")]
    public void NonRelevantFiles_ProjectCheck_ExitsWithCode0()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner",
            "project", "--output", "json", "irrelevant.cs");
        Assert.That(exitCode, Is.EqualTo(0));
    }

    // ── JSON output structure ─────────────────────────────────────────────────

    [Test]
    public void JsonOutput_ContainsAllExpectedTopLevelKeys()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "nonexistent.md");
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
    public void JsonOutput_CheckType_MatchesInput()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "copyright", "--output", "json", "nonexistent.md");
        var checkType = JsonDocument.Parse(stdout).RootElement.GetProperty("checkType").GetString();
        Assert.That(checkType, Is.EqualTo("copyright"));
    }

    // ── GitHub Actions output format ──────────────────────────────────────────

    [Test]
    public void GitHubOutput_ForPassingRun_ProducesNoFindingAnnotations()
    {
        // This previously asserted stdout was entirely empty. That silence was the defect: a run
        // that examined nothing looked exactly like a run that examined everything. What a
        // passing run must not produce is a *finding* annotation, which is what the name means
        // and what is asserted now. The coverage output is not a finding and changes no verdict.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "nonexistent.md");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Not.Contain("::error"), "no findings, so no error annotations");
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined"));
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"),
                "the one annotation a passing run may produce is the report that it examined nothing");
        });
    }

    // ── A file the engine throws on ───────────────────────────────────────────

    /// <summary>
    /// Writes a .cs file the compliance engine cannot analyse, under a directory name the code
    /// rules' path gate accepts, and returns its full path along with the directory to clean up.
    ///
    /// The content is malformed C#. Parsed, it leaves a parameter with no identifier, and the
    /// first rule to index into a parameter name throws. Note it carries no preprocessor
    /// directive: a directive is one way to reach this state and not the only one, so the test
    /// does not depend on how the file got here.
    /// </summary>
    private static (string File, string Dir) WriteUnanalysableFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bhom-ci-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Probe_Adapter"));
        string file = Path.Combine(dir, "Probe_Adapter", "ProbeAdapter.cs");
        File.WriteAllText(file,
            "namespace BH.Adapter.Probe\n" +
            "                m_value = new FileInfo(filePath).Directory.FullName;\n");
        return (file, dir);
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("A file the engine throws on fails the run, names itself, and does not stop the other files being reported.")]
    public void UnanalysableFile_FailsTheRunAndIsNamed_WithoutLosingOtherFindings()
    {
        var (bad, dir) = WriteUnanalysableFile();

        // A second file with a real violation, so the test can tell "the run continued" from
        // "the run died quietly". Capital parameter name breaks InputParameterStartsLower.
        string good = Path.Combine(dir, "Probe_Adapter", "ProbeHelper.cs");
        File.WriteAllText(good,
            "namespace BH.Adapter.Probe\n{\n    public static class ProbeHelper\n    {\n" +
            "        public static int ProbeHelperMethod(int BadParam) { return BadParam; }\n" +
            "    }\n}\n");

        try
        {
            var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
                "code", "--output", "github", bad, good);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1), "a file the engine could not analyse must fail the run");
                Assert.That(stdout, Does.Contain("ProbeAdapter.cs"),
                    "the failing file must be named, or the author cannot act on the red");
                Assert.That(stdout, Does.Contain("could not be analysed"));
                Assert.That(stdout, Does.Contain("1 of 2 file(s) examined"),
                    "the coverage line must survive, which it cannot if the process died");
                Assert.That(stdout, Does.Contain("InputParameterStartsLower"),
                    "the other file's findings must still be reported; losing them was the defect");
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }
}
