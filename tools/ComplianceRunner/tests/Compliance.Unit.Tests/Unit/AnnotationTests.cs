using NUnit.Framework;
using System;
using System.IO;

namespace Compliance.Tests
{
    /// <summary>
    /// The annotation both runners emit for a file the engine threw on, tested without a BHoM
    /// install.
    ///
    /// It was a private method on ComplianceRunner until DatasetComplianceRunner needed the same
    /// thing. Moving it here rather than writing it out twice is what these tests hold in place:
    /// the wording is the whole deliverable, it is the only thing an author sees when a file
    /// cannot be checked, and two copies of it would drift.
    /// </summary>
    [TestFixture]
    public class AnnotationTests
    {
        [Test]
        public void ItNamesTheFileAndSaysTheFileWasNotChecked()
        {
            var a = Annotation.ForUnanalysableFile(
                "Datasets/Regional.json", new IOException("The process cannot access the file."));

            Assert.Multiple(() =>
            {
                Assert.That(a.FilePath, Is.EqualTo("Datasets/Regional.json"),
                    "without the file name the author cannot act on the red");
                Assert.That(a.Message, Does.Contain("could not be analysed and was not checked"));
                Assert.That(a.Message, Does.Contain("Every other file in this run was checked normally."),
                    "says the run continued, so the author does not assume the rest went unchecked");
            });
        }

        [Test]
        public void ItCarriesTheExceptionTypeAndMessage()
        {
            // The distinction the author needs is between their file being malformed and the
            // environment being wrong. The type alone makes that call: an IOException is not a
            // defect in the dataset.
            var a = Annotation.ForUnanalysableFile(
                "Datasets/Regional.json", new IOException("used by another process"));

            Assert.Multiple(() =>
            {
                Assert.That(a.Message, Does.Contain("IOException"));
                Assert.That(a.Message, Does.Contain("used by another process"));
            });
        }

        [Test]
        public void ItIsAFailureAndNotAFinding()
        {
            // Level drives the GitHub annotation level, so a warning here would let a run that
            // could not read a file look like one that read it and had a minor comment.
            // RuleName is the SARIF ruleId and is deliberately not a check name: no check ran.
            var a = Annotation.ForUnanalysableFile("Datasets/Regional.json", new Exception("x"));

            Assert.Multiple(() =>
            {
                Assert.That(a.Level, Is.EqualTo("failure"));
                Assert.That(a.RuleName, Is.EqualTo("FileCouldNotBeAnalysed"));
            });
        }

        [Test]
        public void ItAnchorsToLineOne()
        {
            // GitHub drops the "#L<line>" suffix when LineStart is 0 and renders the annotation
            // against the file rather than the diff. There is no meaningful line for a file that
            // was never parsed, and line 1 is what puts it in the pull request's file view.
            var a = Annotation.ForUnanalysableFile("Datasets/Regional.json", new Exception("x"));

            Assert.Multiple(() =>
            {
                Assert.That(a.LineStart, Is.EqualTo(1));
                Assert.That(a.LineEnd, Is.EqualTo(1));
                Assert.That(a.ColumnStart, Is.EqualTo(1));
                Assert.That(a.ColumnEnd, Is.EqualTo(1));
            });
        }
    }
}
