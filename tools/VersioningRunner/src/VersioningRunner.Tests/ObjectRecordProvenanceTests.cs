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
    // Where the map comes from is DatasetProvenanceTests' subject. These drive it in as a value,
    // because what they pin is how a leaf consumes it: the join key, the Method event's
    // precedence over it, and what a disagreement does. The join key is the leaf description,
    // which for an object record is the `_t` value verbatim.
    //
    // The limit of this coverage: it proves the runner reads a map correctly, not that a live
    // FromJsonDatasets tree produces leaves that hit one. Only a real run shows that.
    public class ObjectRecordProvenanceTests
    {
        // What the staged Objects.json resolved, as the collector receives it.
        private static DeclaringAssemblyMap Map(params (string Type, string Assembly)[] entries)
            => new(
                entries.ToDictionary(e => e.Type, e => e.Assembly, StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                VersionsRead: 1, RecordsRead: entries.Length, TypesMapped: entries.Length, LinesUnparseable: 0);

        // A type two dataset versions named different assembly families for.
        private static DeclaringAssemblyMap Disputed(string type, params string[] assemblies)
            => new(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [type] = assemblies },
                VersionsRead: 2, RecordsRead: assemblies.Length, TypesMapped: 0, LinesUnparseable: 0);

        // A closed generic. Its name contains commas, which is why the quoted part holds the
        // assembly alone: a comma-delimited form silently lost all 23 of these in the 9.2
        // dataset. This one is the case that mattered most, because its declaring assembly is
        // another repository's while its namespace is the subject's, so losing the field left
        // the original misattribution in place.
        private const string ClosedGeneric =
            "BH.oM.Structure.Results.ResultEnvelope`1[[BH.oM.Structure.Results.ConnectionForce, StructuralEngineering_oM, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null]]";

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

        // The join. The leaf's description is the record's `_t` value, so the map is keyed on
        // exactly what arrives here. Measured on the 15 saved run artefacts: 205 of 205
        // object-record findings match a dataset record on this key, with no fuzzy matching.
        [Fact]
        public void TheLeafDescriptionIsTheKeyIntoTheMap()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                provenance: Map(("BH.oM.Acoustic.Panel", "Acoustic_oM")));

            Assert.Equal("Acoustic_oM", Assert.Single(diagnostics).DeclaringAssembly);
        }

        // A map that answers for other types but not this one leaves the leaf where it was.
        [Fact]
        public void ATypeTheDatasetDoesNotNameIsNotGivenAnAssembly()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                provenance: Map(("BH.oM.Structure.Elements.Panel", "Structure_oM")));

            Assert.Null(Assert.Single(diagnostics).DeclaringAssembly);
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

        // No map supplied at all, which is what every caller that does not pass one gets and
        // what the runner does without --datasets. Identical to the behaviour before this change.
        [Fact]
        public void NoMap_LeavesTheDeclaringAssemblyNull()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", "Failed to convert the string into a type: BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics);

            Assert.Null(Assert.Single(diagnostics).DeclaringAssembly);
        }

        // The dataset read before the backfill lands: records present, none carrying the field.
        [Fact]
        public void AnEmptyMap_LeavesTheDeclaringAssemblyNull()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                provenance: DeclaringAssemblyMap.Empty);

            Assert.Null(Assert.Single(diagnostics).DeclaringAssembly);
        }

        // A record cannot be both, and the method path must not change. If the map happens to
        // answer for a method leaf's description, the Method event still wins, because it is
        // read first and the dataset is only consulted when it yielded nothing.
        [Fact]
        public void MethodEventStillWins_WhenTheMapAlsoAnswers()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.Revit.Engine.MechanicalPlumbing.Compute. }", MethodEvent),
                (_, _) => (true, AttributionBasis.NotRecorded), null,
                (_, _, _) => (null, ClassificationPath.DeclaringTypeNotLoaded, Array.Empty<string>()), diagnostics,
                provenance: Map(("BH.Revit.Engine.MechanicalPlumbing.Compute. }", "Acoustic_oM")));

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
                Tree("BH.oM.Acoustic.Panel"),
                wholeClosure, null, null, withAsm,
                provenance: Map(("BH.oM.Acoustic.Panel", "Somebody_Elses_oM")));

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
                Tree("BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => ["Revit_ModelQA_oM_2022", "Revit_ModelQA_oM_2023"],
                provenance: Map(("BH.oM.Acoustic.Panel", "Revit_ModelQA_oM_2022")));

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
                Tree("BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => ["Acoustic_oM"],
                provenance: Map(("BH.oM.Acoustic.Panel", "Acoustic_oM")));

            Assert.Null(Assert.Single(diagnostics).DeclaringTypeCandidates);
        }

        // Nothing resolved, so there is no type the dataset vouched for and scanning the closure
        // would answer a question nobody asked. This is the other half of inertness.
        [Fact]
        public void NoMapEntry_DoesNotScanForCandidates()
        {
            bool called = false;
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Acoustic.Panel", "Failed to convert the string into a type: BH.oM.Acoustic.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => { called = true; return []; },
                provenance: DeclaringAssemblyMap.Empty);

            Assert.False(called, "a record the dataset does not answer for must not be probed");
            Assert.Null(Assert.Single(diagnostics).DeclaringTypeCandidates);
        }

        // Versions disagreeing is not resolved by picking one. The finding stays unattributed
        // and carries what the dataset claimed, so the ambiguity is counted rather than
        // normalised away. Measured 0 across the 1711 type names the two backfilled versions
        // share, so this path is expected to stay empty and is pinned so it stays honest if not.
        [Fact]
        public void ADisputedTypeIsLeftUnattributedAndCarriesTheClaims()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Structure.Elements.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                provenance: Disputed("BH.oM.Structure.Elements.Panel", "StructuralEngineering_oM", "Structure_oM"));

            var only = Assert.Single(diagnostics);
            Assert.Null(only.DeclaringAssembly);
            Assert.Equal(["StructuralEngineering_oM", "Structure_oM"], only.DeclaringTypeCandidates!);
        }

        // The dataset's disagreement is the answer, not the closure's. Probing would replace a
        // statement about which assembly declared the type with a statement about which
        // assemblies could have, and they are different questions.
        [Fact]
        public void ADisputedTypeDoesNotFallBackToTheClosureScan()
        {
            bool called = false;
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Structure.Elements.Panel"),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                probeTypeCandidates: _ => { called = true; return ["Something_Else_oM"]; },
                provenance: Disputed("BH.oM.Structure.Elements.Panel", "StructuralEngineering_oM", "Structure_oM"));

            Assert.False(called);
            Assert.Equal(["StructuralEngineering_oM", "Structure_oM"], Assert.Single(diagnostics).DeclaringTypeCandidates!);
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
        // Attribution and reclassification driven together, through the real
        // AttributeToSubject rather than a stub.
        //
        // No test supplied both dataset provenance and a closure until this point, which is
        // why the case below went unnoticed: with a stubbed isAttributable the coupling
        // between the two is never exercised.
        // ------------------------------------------------------------------

        // A closure as a real run has one: the subject built one Revit year, and that year is
        // loaded. ClosureForSubject leaves both loaded sets empty, which cannot reach the
        // reclassification block at all.
        private static ClosureContext ClosureBuilding(string loadedAssembly)
        {
            var loaded = new HashSet<string>([loadedAssembly], StringComparer.Ordinal);
            var bases = new HashSet<string>(loaded.Select(RunCommand.StripConfigSuffix), StringComparer.Ordinal);
            return new ClosureContext(loaded, bases, bases);
        }

        private static Func<string, string?, (bool, AttributionBasis)> RealAttribution(
            ClosureContext closure, params string[] subjectNamespaces)
        {
            var ns = new HashSet<string>(subjectNamespaces, StringComparer.Ordinal);
            return (d, asm) => RunCommand.AttributeToSubject(d, asm, ns, closure);
        }

        // The dataset records the lowest Revit year present at capture, which is 2022 on every
        // one of the 162 year-suffixed records in 9.3. A repository that has moved on builds a
        // later year. That is a difference in how the field was written, not a statement that a
        // configuration was skipped, and the finding is real: the record was deserialised
        // against the assemblies that are loaded, and it failed against them. There is no
        // per-assembly probe here to have been unable to run.
        [Fact]
        public void AnObjectRecordNamingAnUnbuiltYear_IsStillARealFailure()
        {
            var closure = ClosureBuilding("Revit_Tagging_oM_2024");
            var diagnostics = new List<FailureDiagnostic>();

            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Tagging.Settings.TagSettings"),
                RealAttribution(closure, "BH.oM.Tagging.Settings"), null, null, diagnostics,
                closure: closure,
                probeTypeCandidates: _ => ["Revit_Tagging_oM_2024"],
                provenance: Map(("BH.oM.Tagging.Settings.TagSettings", "Revit_Tagging_oM_2022")));

            var only = Assert.Single(diagnostics);
            Assert.Equal("Revit_Tagging_oM_2022", only.DeclaringAssembly);
            Assert.Equal(AttributionBasis.DeclaringAssembly, only.AttributedBy);
            Assert.Null(only.Cause);
            Assert.Equal(ClassificationPath.NoMethodEvent, only.Path);
            Assert.True(only.CountedAsReal,
                "an object record is deserialised against what is loaded, so a failure is real: "
                + "the recorded Revit year is how the field was written, not a configuration that was skipped");
        }

        // The same record where the repository does still build the recorded year. This one
        // never reached the reclassification block, because the exact name is loaded, and it is
        // here so the fix is not credited with behaviour that already worked.
        [Fact]
        public void AnObjectRecordNamingABuiltYear_IsAlsoARealFailure()
        {
            var closure = ClosureBuilding("Revit_Tagging_oM_2022");
            var diagnostics = new List<FailureDiagnostic>();

            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Tagging.Settings.TagSettings"),
                RealAttribution(closure, "BH.oM.Tagging.Settings"), null, null, diagnostics,
                closure: closure,
                probeTypeCandidates: _ => ["Revit_Tagging_oM_2022"],
                provenance: Map(("BH.oM.Tagging.Settings.TagSettings", "Revit_Tagging_oM_2022")));

            Assert.True(Assert.Single(diagnostics).CountedAsReal);
        }

        // Attribution, not reclassification, is what drops another repository's record, and it
        // drops it before the block is reached. Pinned because it is the outcome the block's
        // foreign branch looks like it provides and does not.
        [Fact]
        public void AnObjectRecordDeclaredByAnotherRepository_IsDroppedAtAttribution()
        {
            var closure = ClosureBuilding("Revit_Tagging_oM_2024");
            var diagnostics = new List<FailureDiagnostic>();

            RunCommand.ExtractFilteredResult(
                Tree("BH.oM.Structure.Elements.Panel"),
                RealAttribution(closure, "BH.oM.Structure.Elements"), null, null, diagnostics,
                closure: closure,
                probeTypeCandidates: _ => ["Structure_oM"],
                provenance: Map(("BH.oM.Structure.Elements.Panel", "Structure_oM")));

            Assert.Empty(diagnostics);
        }

        // ------------------------------------------------------------------
        // Closed generics. The class the earlier message-based route silently lost.
        // ------------------------------------------------------------------

        // 23 of the 9.2 dataset's 1,713 records are closed generics, whose names carry commas
        // and brackets. Reaching them through a formatted message lost all 23 to the delimiter,
        // with no diagnostic, and one of them was attributed to the wrong repository as a
        // result. A dictionary key has no delimiter to lose them to, and the description arrives
        // as the record wrote it. DatasetProvenanceTests pins the same property at map level.
        [Fact]
        public void AClosedGenericIsAWholeKey()
        {
            var diagnostics = new List<FailureDiagnostic>();
            RunCommand.ExtractFilteredResult(
                Tree(ClosedGeneric),
                (_, _) => (true, AttributionBasis.NotRecorded), null, null, diagnostics,
                provenance: Map((ClosedGeneric, "StructuralEngineering_oM")));

            Assert.Equal("StructuralEngineering_oM", Assert.Single(diagnostics).DeclaringAssembly);
        }

        // The case that mattered: the type's namespace is the subject's, so the namespace guess
        // claims it, while the declaring assembly says it is another repository's. Under the old
        // format this record fell back and stayed misattributed.
        [Fact]
        public void AClosedGenericDeclaredElsewhereIsNotAttributedToTheSubject()
        {
            var closure = ClosureForSubject("Structure_oM.dll");
            var subjectNs = new HashSet<string>(["BH.oM.Structure.Results"], StringComparer.Ordinal);

            var withField = RunCommand.AttributeToSubject(
                ClosedGeneric, "StructuralEngineering_oM", subjectNs, closure);
            Assert.False(withField.Attributable);
            Assert.Equal(AttributionBasis.DeclaringAssembly, withField.Basis);

            // What the fallback does when the dataset does not answer for the record, which is
            // every object record until the backfill lands.
            var withoutField = RunCommand.AttributeToSubject(ClosedGeneric, null, subjectNs, closure);
            Assert.True(withoutField.Attributable);
            Assert.Equal(AttributionBasis.NamespaceFallback, withoutField.Basis);
        }
    }
}
