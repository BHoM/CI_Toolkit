using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using BH.oM.Test;
using BH.oM.Test.Results;

/// <summary>
/// Covers the IFullMessage branch of AnnotationConvert.ToAnnotationEquivalent, which the
/// sibling AnnotationConvertTests deliberately avoids. A dataset-test result carries one inner
/// result per TestData, so a fixture with hundreds of entries produces a message padded with
/// hundreds of lines saying nothing failed. Downstream that message is truncated before the
/// failing entry is reached, which left four fixtures red with no recorded cause.
/// </summary>
[TestFixture]
public class AnnotationInnerResultsTests
{
    // Models the shape of BHoM_Engine's PlaneIntersections fixture: 501 TestData entries, all
    // passing except one. The failing entry is placed last, which is the worst case and the one
    // that actually bit.
    private static TestResult FixtureShapedResult(int passing = 500)
    {
        var result = new TestResult
        {
            Status      = TestStatus.Error,
            Description = "UnitTest: Method: PlaneIntersections(Ellipse curve, Plane plane, Double tolerance).",
            Message     = "The UnitTest did not pass.",
            Information = new List<ITestInformation>(),
        };

        for (int i = 0; i < passing; i++)
        {
            result.Information.Add(new TestResult
            {
                Status      = TestStatus.Pass,
                Description = $"TestData: index: {i}",
                Message     = "Execution of the method returned the expected outputs.",
            });
        }

        result.Information.Add(new TestResult
        {
            Status      = TestStatus.Error,
            Description = $"TestData: index: {passing}",
            Message     = "Execution of the method did not return the expected outputs.",
        });

        return result;
    }

    [Test]
    public void InnerResults_PassingEntriesAreNotRendered()
    {
        var ann = FixtureShapedResult().ToAnnotationEquivalent();

        Assert.Multiple(() =>
        {
            Assert.That(Regex.Matches(ann.Message, "returned the expected outputs").Count, Is.EqualTo(0),
                "passing inner results must not be rendered into the annotation message");
            Assert.That(ann.Message, Does.Contain("did not return the expected outputs"),
                "the failing inner result must survive");
            Assert.That(ann.Message, Does.Contain("TestData: index: 500"),
                "the failing entry must be identifiable");
        });
    }

    // The padding is what causes the downstream truncation, so its absence is the thing worth
    // pinning. A local run does not truncate; only the emitted message size is observable here.
    [Test]
    public void InnerResults_MessageStaysSmallOnALargeFixture()
    {
        var ann = FixtureShapedResult().ToAnnotationEquivalent();

        Assert.That(ann.Message.Length, Is.LessThan(500),
            "a 501-entry fixture with one failure must not produce a multi-kilobyte message");
    }

    // A Pass-status finding still has to render its own message. The dataset check surfaces
    // dataset provenance that way, and FullMessage returns "" for a result less severe than the
    // floor it is given, so the floor cannot be raised unconditionally.
    [Test]
    public void PassLevelFinding_StillRendersItsOwnMessage()
    {
        var notice = new TestResult
        {
            Status      = TestStatus.Pass,
            Description = "Title: EmbodiedCarbonFactors_UK",
            Message     = "Dataset read successfully.",
            Information = new List<ITestInformation>(),
        };

        var ann = notice.ToAnnotationEquivalent();

        Assert.Multiple(() =>
        {
            Assert.That(ann.Level, Is.EqualTo("notice"));
            Assert.That(ann.Message, Does.Contain("Dataset read successfully."));
        });
    }
}
