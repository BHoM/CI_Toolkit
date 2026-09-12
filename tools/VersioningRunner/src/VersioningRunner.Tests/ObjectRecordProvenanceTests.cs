using System.Reflection;
using VersioningRunner.Commands;
using VersioningRunner.Models;
using VersioningRunner.Tests.Fixtures;
using Xunit;

namespace VersioningRunner.Tests
{
    // Object records carry a type name and nothing else, so attribution has always guessed from
    // the namespace. The dataset's `_asm` field closes that, and these pin the runner half.
    //
    // Every test here drives a synthetic TestResult, because the runner has no other view of the
    // dataset and Versioning_Toolkit does not emit the event yet. That is the limit of this
    // coverage: it proves the runner handles the format it defines, not that anything produces it.
    public class ObjectRecordProvenanceTests
    {
        // The contract Versioning_Toolkit's FromJson.cs is bound to emit.
        private static string ObjectEvent(string type, string assembly) =>
            $"Object {type} declared in \"{type}, {assembly}\"";

        private const string MethodEvent =
            "Method ApplyDuctInsulation from { \"_t\" : \"System.Type\", \"Name\" : \"BH.Revit.Engine.MechanicalPlumbing.Compute, Revit_MechanicalPlumbing_Engine_2022, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null\", \"_bhomVersion\" : \"9.2\" } failed to deserialise.";

        private static FakeTestResult Tree(string description, params string[] events)
        {
            var leaf = new FakeTestInfo
            {
                Status = "Error",
                Description = description,
                Message = "Error: Returned null from json.",
                Information = events.Select(m => (object)new FakeEventMessage { Message = m }).ToList()
            };
            return new FakeTestResult
            {
                Status = "Error",
                Information = [new FakeTestResult { Status = "Error", Information = [leaf] }]
            };
        }

        // Subject names arrive as file names with extension, exactly as ReadSubjectAssemblyListFrom
        // produces them, so the extension handling is exercised rather than assumed away.
        private static ClosureContext ClosureForSubject(params string[] subjectFileNames)
        {
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            return new ClosureContext(
                loaded,
                new HashSet<string>(loaded.Select(RunCommand.StripConfigSuffix), StringComparer.Ordinal),
                new HashSet<string>(
                    RunCommand.ReadSubjectAssemblyListFrom(subjectFileNames)
                              .Select(f => RunCommand.StripConfigSuffix(Path.GetFileNameWithoutExtension(f))),
                    StringComparer.Ordinal));
        }

        // ------------------------------------------------------------------
        // Reading the field
        // ------------------------------------------------------------------

        [Fact]
        public void ObjectEvent_YieldsTheDeclaringAssemblyAndType()
        {
            var (type, assembly) = RunCommand.ParseObjectEventAssembly(
                ObjectEvent("BH.oM.Acoustic.Panel", "Acoustic_oM"));

            Assert.Equal("BH.oM.Acoustic.Panel", type);
            Assert.Equal("Acoustic_oM", assembly);
        }

        [Fact]
        public void DeclaringAssemblyFromAnObjectEvent_ReachesTheDiagnostic()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", ObjectEvent("BH.oM.Acoustic.Panel", "Acoustic_oM")),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics);

            Assert.Equal("Acoustic_oM", Assert.Single(diagnostics).DeclaringAssembly);
        }

        // The whole point of the field: this record used to be attributable only by prefix.
        [Fact]
        public void ObjectRecordNamingTheSubjectsAssembly_IsAttributedByDeclaringAssembly()
        {
            var closure = ClosureForSubject("Acoustic_oM.dll");

            var (attributable, basis) = RunCommand.AttributeToSubject(
                "BH.oM.Acoustic.Panel", "Acoustic_oM", new HashSet<string>(StringComparer.Ordinal), closure);

            Assert.True(attributable);
            Assert.Equal(AttributionBasis.DeclaringAssembly, basis);
        }

        [Fact]
        public void ObjectRecordNamingAnotherRepositorysAssembly_IsNotAttributed()
        {
            var closure = ClosureForSubject("Acoustic_oM.dll");

            var (attributable, basis) = RunCommand.AttributeToSubject(
                "BH.oM.Structure.Elements.Panel", "Structure_oM", new HashSet<string>(StringComparer.Ordinal), closure);

            Assert.False(attributable);
            Assert.Equal(AttributionBasis.DeclaringAssembly, basis);
        }

        // ------------------------------------------------------------------
        // Inertness. These are the tests that say this PR does nothing on its own.
        // ------------------------------------------------------------------

        [Fact]
        public void NoObjectEvent_LeavesTheDeclaringAssemblyNull()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", "Failed to convert the string into a type: BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics);

            Assert.Null(Assert.Single(diagnostics).DeclaringAssembly);
        }

        // A record cannot be both, and the method path must not change. If both events are
        // present the Method event still wins, because it is read first and the object read is
        // only consulted when it yielded nothing.
        [Fact]
        public void MethodEventStillWins_WhenBothArePresent()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.Revit.Engine.MechanicalPlumbing.Compute. }",
                     MethodEvent,
                     ObjectEvent("BH.oM.Acoustic.Panel", "Acoustic_oM")),
                (_, _) => (true, AttributionBasis.NotRecorded), null,
                (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()), diagnostics);

            Assert.Equal("Revit_MechanicalPlumbing_Engine_2022", Assert.Single(diagnostics).DeclaringAssembly);
        }

        // Whole closure discards the declaring assembly for attribution: Execute wires
        // `(d, _) =>` and supplies no ClosureContext. So on a run with no subject list this
        // change cannot alter which findings are reported, only what the artefact records.
        // The backfill's own validation runs were whole-closure, so they could not have
        // exercised any of the attribution behaviour above.
        [Fact]
        public void WholeClosure_IgnoresTheDeclaringAssemblyForAttribution()
        {
            var nsPrefixes = new HashSet<string>(["BH.oM.Acoustic"], StringComparer.Ordinal);
            Func<string, string?, (bool, AttributionBasis)> wholeClosure =
                (d, _) => (RunCommand.IsFromLoadedNamespace(d, nsPrefixes), AttributionBasis.NotRecorded);

            var withAsm = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", ObjectEvent("BH.oM.Acoustic.Panel", "Somebody_Elses_oM")),
                wholeClosure, null, null, withAsm);

            Assert.Equal(AttributionBasis.NotRecorded, Assert.Single(withAsm).AttributedBy);
            Assert.True(withAsm[0].CountedAsReal);
        }

        // ------------------------------------------------------------------
        // The Revit year. The backfill wrote an arbitrary lowest year on 158 records, and this
        // is what makes that harmless rather than a silent misattribution.
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Revit_ModelQA_oM_2022")]
        [InlineData("Revit_ModelQA_oM_2023")]
        [InlineData("Revit_ModelQA_oM_2024")]
        [InlineData("Revit_ModelQA_oM_2025")]
        [InlineData("Revit_ModelQA_oM_2026")]
        public void AnyYearAttributesToASubjectBuildingAnyOtherYear(string recordedAssembly)
        {
            // The subject built Release2024, so infer-verification-config staged exactly one year.
            var closure = ClosureForSubject("Revit_ModelQA_oM_2024.dll");

            Assert.True(RunCommand.IsFromSubjectAssembly(recordedAssembly, closure),
                $"{recordedAssembly} must attribute to a Release2024 build: all five year variants are " +
                "one repository, so the year carries no information about ownership");
        }

        // "More precise" would be wrong here, and this is the shape of the regression it would
        // cause: an exact-year match fails in four configurations out of five and the finding is
        // dropped as another repository's. Recorded as a test so nobody reintroduces it.
        [Fact]
        public void TheYearIsNotComparedExactly()
        {
            var closure = ClosureForSubject("Revit_ModelQA_oM_2024.dll");

            Assert.True(RunCommand.IsFromSubjectAssembly("Revit_ModelQA_oM_2022", closure));
            Assert.False(RunCommand.IsFromSubjectAssembly("Revit_Tagging_oM_2022", closure));
        }

        // StripConfigSuffix is anchored `_20\d{2}$`, so an `_asm` carrying a file extension does
        // not match the anchor, passes through unchanged, and attributes to nobody. This is why
        // the backfill wrote bare simple names, matching how method records already express a
        // declaring assembly. Pinned so the dataset convention cannot drift without a red test.
        [Fact]
        public void AnAssemblyNameCarryingAnExtensionDoesNotAttribute()
        {
            var closure = ClosureForSubject("Acoustic_oM.dll");

            Assert.True(RunCommand.IsFromSubjectAssembly("Acoustic_oM", closure));
            Assert.False(RunCommand.IsFromSubjectAssembly("Acoustic_oM.dll", closure));
        }

        // SubjectBaseNames is built with StringComparer.Ordinal while the file-name set upstream
        // is OrdinalIgnoreCase, so the base-name comparison is case-sensitive where the name
        // comparison is not. Not a defect today, because both sides come from the same build
        // output, but it is a real edge and it should fail visibly if it ever starts mattering.
        [Fact]
        public void BaseNameComparisonIsCaseSensitive()
        {
            var closure = ClosureForSubject("Acoustic_oM.dll");

            Assert.True(RunCommand.IsFromSubjectAssembly("Acoustic_oM", closure));
            Assert.False(RunCommand.IsFromSubjectAssembly("acoustic_om", closure));
        }

        // ------------------------------------------------------------------
        // Ambiguity on object records
        // ------------------------------------------------------------------

        [Fact]
        public void MoreThanOneAssemblyDeclaringTheType_IsRecordedAsCandidates()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", ObjectEvent("BH.oM.Acoustic.Panel", "Revit_ModelQA_oM_2022")),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => ["Revit_ModelQA_oM_2022", "Revit_ModelQA_oM_2023"]);

            var only = Assert.Single(diagnostics);
            Assert.NotNull(only.DeclaringTypeCandidates);
            Assert.Equal(2, only.DeclaringTypeCandidates!.Count);
        }

        // One candidate is the ordinary case; carrying it would add a noise row to every finding.
        [Fact]
        public void ASingleDeclaringAssembly_LeavesCandidatesNull()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", ObjectEvent("BH.oM.Acoustic.Panel", "Acoustic_oM")),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => ["Acoustic_oM"]);

            Assert.Null(Assert.Single(diagnostics).DeclaringTypeCandidates);
        }

        // Without the object event there is no type to scan, so the probe is never called and
        // the pre-existing behaviour stands. This is the other half of inertness.
        [Fact]
        public void NoObjectEvent_DoesNotScanForCandidates()
        {
            bool called = false;
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", "Failed to convert the string into a type: BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => { called = true; return []; });

            Assert.False(called, "a record with no object event has no type to scan and must not be probed");
            Assert.Null(Assert.Single(diagnostics).DeclaringTypeCandidates);
        }

        [Fact]
        public void ProbeTypeCandidates_ReturnsEveryAssemblyDeclaringTheType()
        {
            var loaded = new List<Assembly> { typeof(RunCommand).Assembly, typeof(object).Assembly };

            Assert.Equal(["VersioningRunner"],
                RunCommand.ProbeTypeCandidates(loaded, typeof(RunCommand).FullName!));
            Assert.Empty(RunCommand.ProbeTypeCandidates(loaded, "No.Such.Type"));
        }

        // ------------------------------------------------------------------
        // Malformed input fails to parse rather than capturing something wrong
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Object BH.oM.Acoustic.Panel declared in \"BH.oM.Acoustic.Panel, Acoustic_oM")]
        [InlineData("Object BH.oM.Acoustic.Panel declared in \"Acoustic_oM\"")]
        [InlineData("Object declared in \"BH.oM.Acoustic.Panel, Acoustic_oM\"")]
        [InlineData("BH.oM.Acoustic.Panel declared in \"BH.oM.Acoustic.Panel, Acoustic_oM\"")]
        [InlineData("")]
        public void AMalformedObjectEventYieldsNothing(string message)
        {
            var (type, assembly) = RunCommand.ParseObjectEventAssembly(message);

            Assert.Null(type);
            Assert.Null(assembly);
        }
    }
}
