using VersioningRunner.Commands;
using VersioningRunner.Models;
using VersioningRunner.Tests.Fixtures;
using Xunit;

namespace VersioningRunner.Tests
{
    // Dataset findings are shaped unlike every other finding the runner sees, and attribution was
    // written for the others. Versioning_Toolkit's dataset leg sets Description to a library path
    // ("Planning\Labels\IssueLabels") or, for the absent-file case, to a filesystem path. Both
    // attribution modes key on a namespace-qualified type name, so neither can evaluate either
    // one, and "if (!attributable) return" dropped the leaf into neither bucket: no failure, no
    // unverified entry, no diagnostic row.
    //
    // These pin the new outcome. The two that assert reporting REPLACE tests that asserted the
    // drop; inverting them is the point of this change, not an accident.
    public class UnattributableLeafTests
    {
        private static FakeTestResult Tree(string description, string status, params string[] events) =>
            new()
            {
                Status = status,
                Information =
                [
                    new FakeTestResult
                    {
                        Status = status,
                        Information =
                        [
                            new FakeTestInfo
                            {
                                Status = status,
                                Description = description,
                                Message = "UNVERIFIED: could not be resolved against the installed library set.",
                                Information = events.Select(m => (object)new FakeEventMessage { Message = m }).ToList()
                            }
                        ]
                    }
                ]
            };

        private static readonly HashSet<string> SubjectNamespaces =
            new(["BH.oM.Versioning", "BH.Engine.Versioning"], StringComparer.Ordinal);

        private static ClosureContext Closure() =>
            new(new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(["Versioning_Engine", "Versioning_oM"], StringComparer.Ordinal));

        private static (VersioningResult Result, List<FailureDiagnostic> Diags, List<RunCommand.UnverifiedFailure> Unverified)
            Run(FakeTestResult tree)
        {
            var diagnostics = new List<FailureDiagnostic>();
            var unverified = new List<RunCommand.UnverifiedFailure>();
            var result = RunCommand.ExtractFilteredResult(
                tree,
                (d, a) => RunCommand.AttributeToSubject(d, a, SubjectNamespaces, Closure()),
                unverified,
                (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()),
                diagnostics, Closure());
            return (result, diagnostics, unverified);
        }

        // ------------------------------------------------------------------
        // Why this change has to carry its own cause.
        // ------------------------------------------------------------------

        // THE LEAF'S STATUS DOES NOT REACH THE VERDICT, AND THIS IS THE WHOLE SAFETY CASE.
        //
        // CollectLeafFailures reads Status once, as an entry filter: anything that is not Error or
        // Warning returns early. After that the real-or-unverified decision is driven entirely by
        // the cause, and Status is never read again. So a Warning leaf with no cause is counted as
        // a REAL, GATING failure exactly like an Error one.
        //
        // That matters because Versioning_Toolkit's part-2 change (internal-tickets#36) turns the
        // unresolvable-entry outcome from Error into Warning, and was described in the sequencing
        // plan as disarming this gate ahead of this change arming it. It does not: the runner
        // cannot see that Warning. This change must therefore make an unattributable finding
        // non-gating BY CONSTRUCTION, rather than relying on the producer having downgraded it.
        [Theory]
        [InlineData("Error")]
        [InlineData("Warning")]
        public void AttributableLeafWithNoCause_IsCountedReal_WhateverItsStatus(string status)
        {
            var diagnostics = new List<FailureDiagnostic>();
            var result = RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Versioning.Datasets.Thing", status),
                (_, _) => (true, AttributionBasis.NotRecorded),
                null, (_, _, _) => (null, ClassificationPath.NoOverloadFound, Array.Empty<string>()), diagnostics);

            Assert.Equal(1, result.FailureCount);
            Assert.True(Assert.Single(diagnostics).CountedAsReal,
                "the producer's Status is invisible here, so a downgrade upstream cannot disarm this gate");
        }

        // ------------------------------------------------------------------
        // The inversion: what used to be dropped is now reported, and cannot gate.
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Warning")]
        [InlineData("Error")]
        public void DatasetEntryLeaf_IsReportedAsNotApplicable_AndNeverGates(string status)
        {
            var (result, diags, unverified) = Run(Tree(@"Planning\Labels\IssueLabels", status));

            Assert.Equal(0, result.FailureCount);
            var only = Assert.Single(diags);
            Assert.False(only.CountedAsReal);
            Assert.Equal(AttributionBasis.NotApplicable, only.AttributedBy);
            Assert.NotNull(only.Cause);
            Assert.Single(unverified);
        }

        [Fact]
        public void MissingDatasetsFileLeaf_IsReportedAsNotApplicable_AndNeverGates()
        {
            var (result, diags, unverified) = Run(
                Tree(@"C:\ProgramData\BHoM\Datasets\TestSets\Versioning\9.2\Datasets.txt", "Error"));

            Assert.Equal(0, result.FailureCount);
            var only = Assert.Single(diags);
            Assert.False(only.CountedAsReal);
            Assert.Equal(AttributionBasis.NotApplicable, only.AttributedBy);
            Assert.Single(unverified);
        }

        // Structurally incapable of gating, not incidentally non-gating. Even handed a probe that
        // reports no cause and an empty type index, a not-applicable leaf stays unverified.
        [Fact]
        public void NotApplicableCannotGate_EvenWhenNothingElseSuppliesACause()
        {
            var diagnostics = new List<FailureDiagnostic>();
            var result = RunCommand.ExtractFilteredResult(
                Tree(@"Planning\Labels\IssueLabels", "Error"),
                (d, a) => RunCommand.AttributeToSubject(d, a, SubjectNamespaces, Closure()),
                null, (_, _, _) => (null, ClassificationPath.NoOverloadFound, Array.Empty<string>()),
                diagnostics, Closure(), LoadedTypeIndex.Empty);

            Assert.Equal(0, result.FailureCount);
            Assert.False(Assert.Single(diagnostics).CountedAsReal);
        }

        // The forward-slash half of the invariant. No current producer emits one, but a path
        // written the other way must not silently regress to the namespace guess.
        [Fact]
        public void AForwardSlashPathIsAlsoNotApplicable()
        {
            var (_, diags, _) = Run(Tree("Planning/Labels/IssueLabels", "Error"));

            Assert.Equal(AttributionBasis.NotApplicable, Assert.Single(diags).AttributedBy);
        }

        // ------------------------------------------------------------------
        // Whole-closure mode, which is the path the full-history workflow takes.
        //
        // That workflow runs --test-all across all 24 staged versions with no subject list, and
        // 27 of 28 versions ship a Datasets.txt, so it is where dataset findings actually occur.
        // Covered explicitly because a mutation removing the applicability check from this path
        // originally failed no test at all.
        // ------------------------------------------------------------------

        private static readonly HashSet<string> LoadedPrefixes =
            new(["BH.oM.Versioning", "BH.oM.Structure"], StringComparer.Ordinal);

        [Fact]
        public void WholeClosure_ReportsADatasetPathAsNotApplicable()
        {
            var (attributable, basis) = RunCommand.AttributeWholeClosure(@"Planning\Labels\IssueLabels", LoadedPrefixes);

            Assert.True(attributable);
            Assert.Equal(AttributionBasis.NotApplicable, basis);
        }

        [Fact]
        public void WholeClosure_StillAnswersNormallyForATypeName()
        {
            var inClosure = RunCommand.AttributeWholeClosure("BH.oM.Structure.Elements.Bar", LoadedPrefixes);
            Assert.True(inClosure.Attributable);
            Assert.Equal(AttributionBasis.NotRecorded, inClosure.Basis);

            var outside = RunCommand.AttributeWholeClosure("BH.oM.Adapters.GSA.Thing", LoadedPrefixes);
            Assert.False(outside.Attributable);
            Assert.Equal(AttributionBasis.NotRecorded, outside.Basis);
        }

        [Fact]
        public void WholeClosure_DatasetLeafIsReportedAndNeverGates()
        {
            var diagnostics = new List<FailureDiagnostic>();
            var result = RunCommand.ExtractFilteredResult(
                Tree(@"Planning\Labels\IssueLabels", "Error"),
                (d, _) => RunCommand.AttributeWholeClosure(d, LoadedPrefixes),
                null, null, diagnostics);

            Assert.Equal(0, result.FailureCount);
            var only = Assert.Single(diagnostics);
            Assert.False(only.CountedAsReal);
            Assert.Equal(AttributionBasis.NotApplicable, only.AttributedBy);
        }

        // ------------------------------------------------------------------
        // The guard: type-shaped attribution is untouched.
        // ------------------------------------------------------------------

        // Same absence of events, same status, but a type-shaped description inside the subject's
        // namespace. This must keep taking the namespace fallback, not the new outcome, or the
        // rule has widened past the class it was written for.
        [Fact]
        public void TypeShapedLeafStillTakesTheNamespaceFallback()
        {
            var (result, diags, unverified) = Run(Tree("BH.oM.Versioning.Datasets.Thing", "Error"));

            Assert.Equal(0, result.FailureCount);
            Assert.Single(unverified);
            Assert.Equal(AttributionBasis.NamespaceFallback, Assert.Single(diags).AttributedBy);
        }

        // A type-shaped description that is genuinely another repository's is still DROPPED, not
        // reported. "Not applicable" must not become a way of keeping findings that attribution
        // answered correctly and negatively.
        [Fact]
        public void ForeignTypeShapedLeafIsStillDropped()
        {
            var (result, diags, unverified) = Run(Tree("BH.oM.Structure.Elements.Bar", "Error"));

            Assert.Equal(0, result.FailureCount);
            Assert.Empty(diags);
            Assert.Empty(unverified);
        }
    }
}
