using NUnit.Framework;
using System.Collections.Generic;
using BH.oM.Test;
using BH.oM.Test.Results;
using Error = BH.oM.Test.CodeCompliance.Error;
using DatasetSource = BH.oM.Test.CodeCompliance.DatasetSource;

/// <summary>
/// The demotion itself, in isolation from the engine and the runner.
///
/// These construct the TestResult shape IsValidDataset returns rather than calling it, so a
/// failure here points at the demotion and not at the rule. The engine-backed half, including
/// the pin on the rule name, is in DatasetComplianceRunnerE2ETests.
/// </summary>
[TestFixture]
public class DatasetSeverityTests
{
    private static Error Finding(string name, TestStatus status) =>
        new Error { Name = name, Status = status, Message = name };

    private static TestResult Result(TestStatus status, params ITestInformation[] information) =>
        new TestResult { Status = status, Information = new List<ITestInformation>(information) };

    [Test]
    [Description("The rule being demoted drops to Warning, and so does the top-level status the exit code reads.")]
    public void ConfidenceError_BecomesAWarning_AndTheResultStopsBeingAnError()
    {
        var result = Result(TestStatus.Error, Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error));

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demoted, Is.EqualTo(1));
            Assert.That(demotedResult.Status, Is.EqualTo(TestStatus.Warning),
                "Modify.Merge reads only the top-level status, so leaving it Error would keep the check red with no error annotation");
            Assert.That(demotedResult.Information[0].Status, Is.EqualTo(TestStatus.Warning));
        });
    }

    [Test]
    [Description("A deserialisation failure is untouched. This is the case the split must not weaken.")]
    public void ADifferentRule_IsLeftAlone()
    {
        var result = Result(TestStatus.Error, Finding("Dataset deserialisation error", TestStatus.Error));

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demoted, Is.EqualTo(0));
            Assert.That(demotedResult.Status, Is.EqualTo(TestStatus.Error));
            Assert.That(demotedResult, Is.SameAs(result), "nothing matched, so nothing should have been rebuilt");
        });
    }

    [Test]
    [Description("A fixture carrying both keeps its Error. The status is recomputed from what is left, not set to Warning.")]
    public void ConfidenceAlongsideAnotherError_StaysAnError()
    {
        var result = Result(TestStatus.Error,
            Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error),
            Finding("Dataset deserialisation error", TestStatus.Error));

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demoted, Is.EqualTo(1));
            Assert.That(demotedResult.Status, Is.EqualTo(TestStatus.Error),
                "the surviving error must still fail the run");
            Assert.That(demotedResult.Information[0].Status, Is.EqualTo(TestStatus.Warning));
            Assert.That(demotedResult.Information[1].Status, Is.EqualTo(TestStatus.Error));
        });
    }

    [Test]
    [Description("The Pass-level DatasetSource notice is carried through, not promoted or dropped.")]
    public void TheProvenanceNotice_SurvivesUntouched()
    {
        var notice = new DatasetSource { Status = TestStatus.Pass, Message = "Confidence: Undefined" };
        var result = Result(TestStatus.Error, notice, Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error));

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out _);

        Assert.Multiple(() =>
        {
            Assert.That(demotedResult.Information, Has.Count.EqualTo(2));
            Assert.That(demotedResult.Information[0], Is.SameAs(notice));
            Assert.That(demotedResult.Information[0].Status, Is.EqualTo(TestStatus.Pass));
            Assert.That(demotedResult.Status, Is.EqualTo(TestStatus.Warning));
        });
    }

    [Test]
    [Description("A finding already at Warning is not counted as demoted, so the reported figure stays honest.")]
    public void AConfidenceFindingAlreadyAtWarning_IsNotCountedAgain()
    {
        var result = Result(TestStatus.Warning, Finding(DatasetSeverity.ConfidenceRule, TestStatus.Warning));

        DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.That(demoted, Is.EqualTo(0));
    }

    [Test]
    [Description("Matching is exact. A near-miss on the rule name must not silently demote something else.")]
    [TestCase("dataset confidence error")]
    [TestCase("Dataset Confidence Error")]
    [TestCase("Dataset confidence")]
    [TestCase("Dataset confidence error ")]
    public void TheRuleNameIsMatchedExactly(string name)
    {
        var result = Result(TestStatus.Error, Finding(name, TestStatus.Error));

        DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.That(demoted, Is.EqualTo(0));
    }

    [Test]
    [Description("The caller's result is not mutated, so a later reader sees what the engine returned.")]
    public void TheInputIsNotMutated()
    {
        var original = Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error);
        var result = Result(TestStatus.Error, original);

        DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out _);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(TestStatus.Error));
            Assert.That(original.Status, Is.EqualTo(TestStatus.Error));
        });
    }

    [Test]
    [Description("Every finding demoted leaves a Warning, not a Pass, because the findings are still there.")]
    public void TwoConfidenceErrors_BothDemoted_LeaveAWarning()
    {
        var result = Result(TestStatus.Error,
            Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error),
            Finding(DatasetSeverity.ConfidenceRule, TestStatus.Error));

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demoted, Is.EqualTo(2));
            Assert.That(demotedResult.Status, Is.EqualTo(TestStatus.Warning));
        });
    }

    [Test]
    [Description("A null Information does not throw. The runner defends against it, and so does this.")]
    public void ANullInformation_IsReturnedUnchanged()
    {
        var result = new TestResult { Status = TestStatus.Pass, Information = null };

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demotedResult, Is.SameAs(result));
            Assert.That(demoted, Is.EqualTo(0));
        });
    }

    [Test]
    [Description("An empty Information does not throw and reports nothing demoted.")]
    public void AnEmptyInformation_IsReturnedUnchanged()
    {
        var result = Result(TestStatus.Pass);

        var demotedResult = DatasetSeverity.DemoteToWarning(result, DatasetSeverity.ConfidenceRule, out int demoted);

        Assert.Multiple(() =>
        {
            Assert.That(demotedResult, Is.SameAs(result));
            Assert.That(demoted, Is.EqualTo(0));
        });
    }

    [Test]
    [Description("The notice names the count, so a green check states what it downgraded.")]
    public void TheDemotionNotice_CarriesTheCount()
    {
        Assert.That(DatasetSeverity.DemotionNotice(55), Does.StartWith("55 Confidence finding"));
    }

    [Test]
    [Description("The notice describes the scope the predicate actually applies. These drifted apart once.")]
    public void TheDemotionNotice_NamesTheSameScopeAsThePredicate()
    {
        string notice = DatasetSeverity.DemotionNotice(1);

        Assert.Multiple(() =>
        {
            Assert.That(notice, Does.Contain(".ci"));
            Assert.That(notice, Does.Not.Contain(".ci/Datasets"),
                "the predicate matches any .ci descendant, so the notice must not claim the narrower pair");
        });
    }
}
