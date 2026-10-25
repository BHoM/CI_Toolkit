using System;
using System.Collections.Generic;
using BH.Engine.Base;                       // ShallowClone
using BH.oM.Test;                           // TestStatus
using BH.oM.Test.Results;                   // TestResult, ITestInformation
using Error = BH.oM.Test.CodeCompliance.Error;

/// <summary>
/// Reports one dataset rule at a lower severity for one kind of file, without changing what the
/// rule looks for or whether it runs.
///
/// The Confidence rule errors on any dataset whose source information leaves Confidence
/// Undefined. That is the right answer for a dataset a repository distributes and the wrong one
/// for a fixture that exists to drive the repository's own tests, where the field says nothing
/// useful and blocking a merge on it costs more than it buys. The field is still expected on
/// both, which is why this demotes the finding rather than suppressing it or dropping the file.
///
/// It lives here and not in the compliance engine deliberately. The engine judges a dataset; it
/// has no concept of a unit-test fixture and should not acquire one for this. The convention
/// that a .ci directory holds test data belongs to CI, so the severity decision belongs to CI too.
/// </summary>
public static class DatasetSeverity
{
    /// <summary>
    /// The engine's name for the Confidence finding, as it appears on the Error and therefore on
    /// Annotation.RuleName.
    /// </summary>
    /// <remarks>
    /// This is a string owned by another repository, which is the one fragile part of this. The
    /// name is still the most stable handle available: matching on message text breaks on the
    /// first reword, and matching on position in the information list breaks on the first
    /// reorder. The guard is a test that runs the real engine over a dataset with Confidence
    /// unset and asserts this exact value comes back, so a rename upstream fails a build here
    /// instead of silently restoring the error for every fixture.
    /// </remarks>
    public const string ConfidenceRule = "Dataset confidence error";

    /// <summary>
    /// Returns a result in which every Error carrying <paramref name="ruleName"/> is reported as
    /// a Warning, with the top-level status recomputed from what is left. Returns the input
    /// unchanged, by reference, when nothing matches.
    /// </summary>
    /// <param name="demoted">How many findings were demoted, for the caller to report.</param>
    /// <remarks>
    /// The top-level status has to be recomputed, not just the finding's. Modify.Merge combines
    /// the two top-level statuses and never looks at Information, and the runner's exit code
    /// reads the merged top-level status. Demoting only the finding would leave the check red
    /// with no error annotation to account for it, which is worse than leaving it alone.
    ///
    /// Recomputed as the most severe status remaining rather than set to Warning, so a fixture
    /// that also fails to deserialise stays an Error. Those two cannot co-occur today, because
    /// IsValidDataset returns early on a deserialisation failure, but that is the engine's
    /// current shape rather than a guarantee, and assuming it here would be assuming it twice.
    ///
    /// The input is cloned rather than mutated. Nothing in the runner reads the original
    /// afterwards, so this buys nothing today; it costs one allocation per fixture and means a
    /// caller that does read it later is not surprised.
    /// </remarks>
    public static TestResult DemoteToWarning(TestResult result, string ruleName, out int demoted)
    {
        demoted = 0;

        // Not null-tolerant on the result itself. The only caller returns early on a null
        // result and counts it, so accepting one here would hide that path rather than guard
        // it. A null Information is different: the runner defends against it two lines later
        // and so does this.
        if (result.Information == null)
            return result;

        var rewritten = new List<ITestInformation>(result.Information.Count);

        foreach (var information in result.Information)
        {
            if (information is Error error
                && error.Status == TestStatus.Error
                && string.Equals(error.Name, ruleName, StringComparison.Ordinal))
            {
                var demotedError = error.ShallowClone();
                demotedError.Status = TestStatus.Warning;
                rewritten.Add(demotedError);
                demoted++;
            }
            else
            {
                rewritten.Add(information);
            }
        }

        if (demoted == 0)
            return result;

        var updated = result.ShallowClone();
        updated.Information = rewritten;
        updated.Status = MostSevere(rewritten);
        return updated;
    }

    /// <summary>The most severe status across a set of findings, Pass when there are none.</summary>
    private static TestStatus MostSevere(IEnumerable<ITestInformation> information)
    {
        var worst = TestStatus.Pass;

        foreach (var entry in information)
        {
            if (entry.Status == TestStatus.Error)
                return TestStatus.Error;

            if (entry.Status == TestStatus.Warning)
                worst = TestStatus.Warning;
        }

        return worst;
    }

    /// <summary>
    /// The line reported when findings were demoted, so a green check states that it downgraded
    /// something rather than implying there was nothing to downgrade.
    /// </summary>
    /// <remarks>
    /// Same argument as the self-skip summary line: a check that goes green for a reason the
    /// reader cannot see is indistinguishable from one that found nothing, and this change makes
    /// two repositories go green at once.
    ///
    /// The scope named here must match <c>FileFilter.IsUnitTestFixture</c>. They were allowed to
    /// drift apart once, when the predicate broadened from the pair .ci/Datasets to any .ci
    /// descendant and this string did not, which left the log describing a narrower rule than the
    /// one that ran. A test asserts the wording for that reason.
    /// </remarks>
    public static string DemotionNotice(int demoted) =>
        $"{demoted} Confidence finding(s) in unit-test fixtures under a .ci directory reported as "
      + "warnings rather than errors. The field is still expected on those datasets.";
}
