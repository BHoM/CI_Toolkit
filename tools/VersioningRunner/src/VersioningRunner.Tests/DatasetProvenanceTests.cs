using System.Text.Json;
using VersioningRunner.Commands;
using Xunit;

namespace VersioningRunner.Tests
{
    // The map from a dataset record's type name to the assembly that declared it.
    //
    // Every fixture here names either a public BHoM assembly or a placeholder that resolves
    // to nothing. Real records name the oM assemblies of private Revit tools, and a test
    // fixture carries such a name into a public repository as readily as a comment does.
    public class DatasetProvenanceTests
    {
        // A closed generic. Its name carries commas and brackets, and it is the shape that the
        // earlier message-based route silently lost 23 of in the 9.2 dataset.
        private const string ClosedGeneric =
            "BH.oM.Structure.Results.ResultEnvelope`1[[BH.oM.Structure.Results.ConnectionForce, StructuralEngineering_oM, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null]]";

        // ------------------------------------------------------------------
        // The union across versions
        // ------------------------------------------------------------------

        [Fact]
        public void AgreedAcrossVersions_MapsTheType()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"))
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal("Acoustic_oM", map.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
            Assert.Empty(map.Disputed);
            Assert.Equal(2, map.VersionsRead);
        }

        // The reason this reads every version rather than the newest. Two of the 205
        // object-record findings across the 15 saved run artefacts resolve only against 9.2,
        // and both are NoMethodEvent findings carrying no declaring assembly, which is exactly
        // the population the field exists to serve. A newest-only read loses them and falls
        // back to the namespace guess without saying so.
        [Fact]
        public void PresentInOneVersionOnly_IsStillMapped()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Environment.Elements.Panel", "Environment_oM"))
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal("Environment_oM", map.DeclaringAssemblyFor("BH.oM.Environment.Elements.Panel"));
            Assert.Equal("Acoustic_oM", map.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
        }

        [Fact]
        public void DifferentFamiliesAcrossVersions_AreDisputedAndNotMapped()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Structure.Elements.Panel", "Structure_oM"))
                .WithObjects("9.3", Record("BH.oM.Structure.Elements.Panel", "StructuralEngineering_oM"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Null(map.DeclaringAssemblyFor("BH.oM.Structure.Elements.Panel"));
            Assert.False(map.ByTypeName.ContainsKey("BH.oM.Structure.Elements.Panel"));
            Assert.Equal(0, map.TypesMapped);
        }

        [Fact]
        public void ADisputedTypeCarriesEveryAssemblyThatClaimedIt()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Structure.Elements.Panel", "Structure_oM"))
                .WithObjects("9.3", Record("BH.oM.Structure.Elements.Panel", "StructuralEngineering_oM"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal(
                ["StructuralEngineering_oM", "Structure_oM"],
                map.Disputed["BH.oM.Structure.Elements.Panel"]);
        }

        // Ordinal-sorted rather than enumeration-ordered, so the warning text and the artefact
        // row do not change between two runs over the same datasets.
        [Fact]
        public void DisputedCandidatesAreOrderedIndependentlyOfTheVersionTheyCameFrom()
        {
            using var ab = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Acoustic.Panel", "Alpha_oM"))
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Zulu_oM"));
            using var ba = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Acoustic.Panel", "Zulu_oM"))
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Alpha_oM"));

            Assert.Equal(
                DatasetProvenance.Build(ab.Root).Disputed["BH.oM.Acoustic.Panel"],
                DatasetProvenance.Build(ba.Root).Disputed["BH.oM.Acoustic.Panel"]);
        }

        // Two years of one repository are not two answers. Attribution strips the year, so
        // calling this a disagreement would drop the finding to the namespace guess for no
        // gain, which is the failure "compare year-insensitively" exists to prevent.
        [Fact]
        public void YearVariantsAcrossVersions_AreNotADisagreement()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.2", Record("BH.oM.Adapters.Revit.Elements.ModelInstance", "Revit_X_oM_2022"))
                .WithObjects("9.3", Record("BH.oM.Adapters.Revit.Elements.ModelInstance", "Revit_X_oM_2023"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Empty(map.Disputed);
            Assert.Equal("Revit_X_oM_2022", map.DeclaringAssemblyFor("BH.oM.Adapters.Revit.Elements.ModelInstance"));
        }

        [Fact]
        public void AClosedGenericKeyRoundTripsWhole()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record(ClosedGeneric, "StructuralEngineering_oM"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal("StructuralEngineering_oM", map.DeclaringAssemblyFor(ClosedGeneric));
        }

        // ------------------------------------------------------------------
        // Inertness. The state of the tracked dataset before the backfill lands.
        // ------------------------------------------------------------------

        [Fact]
        public void RecordsCarryingNoField_LeaveTheMapEmptyWithoutFailing()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3",
                    Record("BH.oM.Acoustic.Panel", assembly: null),
                    Record("BH.oM.Structure.Elements.Panel", assembly: null));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Empty(map.ByTypeName);
            Assert.Empty(map.Disputed);
            Assert.Null(map.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
        }

        // The denominator. Without it an empty map reads the same whether the dataset carries
        // no field or the runner read nothing at all, and those need different action.
        [Fact]
        public void RecordsCarryingNoField_AreStillCountedAsRead()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3",
                    Record("BH.oM.Acoustic.Panel", assembly: null),
                    Record("BH.oM.Structure.Elements.Panel", assembly: null));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal(2, map.RecordsRead);
            Assert.Equal(0, map.TypesMapped);
        }

        [Fact]
        public void BlankLinesAreNotRecords()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"), "", "   ");

            Assert.Equal(1, DatasetProvenance.Build(datasets.Root).RecordsRead);
        }

        // ------------------------------------------------------------------
        // A field that is present but unusable reads as absent
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("{ \"_t\" : \"BH.oM.Acoustic.Panel\", \"_asm\" : 7, \"_bhomVersion\" : \"9.3\" }")]
        [InlineData("{ \"_t\" : \"BH.oM.Acoustic.Panel\", \"_asm\" : null, \"_bhomVersion\" : \"9.3\" }")]
        [InlineData("{ \"_t\" : \"BH.oM.Acoustic.Panel\", \"_asm\" : \"\", \"_bhomVersion\" : \"9.3\" }")]
        [InlineData("{ \"_t\" : \"BH.oM.Acoustic.Panel\", \"_asm\" : \"   \", \"_bhomVersion\" : \"9.3\" }")]
        [InlineData("{ \"_asm\" : \"Acoustic_oM\", \"_bhomVersion\" : \"9.3\" }")]
        [InlineData("{ \"_t\" : \"\", \"_asm\" : \"Acoustic_oM\", \"_bhomVersion\" : \"9.3\" }")]
        public void AnUnusableRecordMapsNothingAndIsNotAnError(string line)
        {
            using var datasets = new TempDatasets().WithObjects("9.3", line);

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Empty(map.ByTypeName);
            Assert.Equal(1, map.RecordsRead);
            Assert.Equal(0, map.LinesUnparseable);
        }

        // Counted and reported rather than thrown. Losing the whole map over one bad line
        // would convert one dataset defect into a fleet-wide fallback to the namespace guess.
        [Theory]
        [InlineData("{ \"_t\" : \"BH.oM.Acoustic.Panel\" ")]
        [InlineData("not json at all")]
        [InlineData("[ \"an array, not a record\" ]")]
        public void AMalformedLineIsCountedAndSkipped(string line)
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"), line);

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal(1, map.LinesUnparseable);
            Assert.Equal(2, map.RecordsRead);
            Assert.Equal("Acoustic_oM", map.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
        }

        // ------------------------------------------------------------------
        // What is and is not read
        // ------------------------------------------------------------------

        // Both files carry `_t: System.Reflection.MethodBase` on every record, so the key
        // identifies nothing, and their leaves already reach the runner with a declaring
        // assembly from the Method event.
        [Fact]
        public void MethodsAndAdaptersAreNotRead()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"))
                .WithFile("9.3", "Methods.json", Record("System.Reflection.MethodBase", "Acoustic_Engine"))
                .WithFile("9.3", "Adapters.json", Record("System.Reflection.MethodBase", "Acoustic_Adapter"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Null(map.DeclaringAssemblyFor("System.Reflection.MethodBase"));
            Assert.Equal(1, map.RecordsRead);
        }

        [Fact]
        public void AVersionWithoutAnObjectsJsonIsSkippedRatherThanFatal()
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"))
                .WithFile("9.2", "Methods.json", Record("System.Reflection.MethodBase", "Acoustic_Engine"));

            var map = DatasetProvenance.Build(datasets.Root);

            Assert.Equal(1, map.VersionsRead);
            Assert.Equal("Acoustic_oM", map.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
        }

        // ------------------------------------------------------------------
        // The two states that are a broken precondition rather than an empty result
        // ------------------------------------------------------------------

        [Fact]
        public void AnAbsentRootThrows()
        {
            string missing = Path.Combine(Path.GetTempPath(), "versioning-runner-no-such-datasets-" + Guid.NewGuid());

            var thrown = Assert.Throws<DatasetProvenanceException>(() => DatasetProvenance.Build(missing));
            Assert.Contains(missing, thrown.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnUnsuppliedRootThrows(string? root)
        {
            Assert.Throws<DatasetProvenanceException>(() => DatasetProvenance.Build(root));
        }

        [Fact]
        public void ARootWithNoObjectsJsonAnywhereThrows()
        {
            using var datasets = new TempDatasets()
                .WithFile("9.2", "Methods.json", Record("System.Reflection.MethodBase", "Acoustic_Engine"))
                .WithFile("9.3", "Adapters.json", Record("System.Reflection.MethodBase", "Acoustic_Adapter"));

            Assert.Throws<DatasetProvenanceException>(() => DatasetProvenance.Build(datasets.Root));
        }

        [Fact]
        public void ARootWithNoVersionDirectoriesThrows()
        {
            using var datasets = new TempDatasets();

            Assert.Throws<DatasetProvenanceException>(() => DatasetProvenance.Build(datasets.Root));
        }

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("No.Such.Type")]
        public void LookupOfSomethingNotMappedYieldsNull(string? typeName)
        {
            using var datasets = new TempDatasets()
                .WithObjects("9.3", Record("BH.oM.Acoustic.Panel", "Acoustic_oM"));

            Assert.Null(DatasetProvenance.Build(datasets.Root).DeclaringAssemblyFor(typeName));
        }

        [Fact]
        public void TheEmptyMapAnswersNullRatherThanThrowing()
        {
            Assert.Null(DeclaringAssemblyMap.Empty.DeclaringAssemblyFor("BH.oM.Acoustic.Panel"));
        }

        /*************************************/
        /**** Fixtures                    ****/
        /*************************************/

        // Field order matches a real record: `_t` first, `_asm` immediately before the last
        // `_bhomVersion`, which is where the backfill and the capture both put it.
        private static string Record(string type, string? assembly, string version = "9.3")
        {
            string field = assembly is null
                ? string.Empty
                : $"\"_asm\" : {JsonSerializer.Serialize(assembly)}, ";

            return $"{{ \"_t\" : {JsonSerializer.Serialize(type)}, \"Name\" : \"fixture\", "
                 + $"{field}\"_bhomVersion\" : \"{version}\" }}";
        }

        private sealed class TempDatasets : IDisposable
        {
            public string Root { get; }

            public TempDatasets()
            {
                Root = Path.Combine(Path.GetTempPath(), "versioning-runner-datasets-" + Guid.NewGuid());
                Directory.CreateDirectory(Root);
            }

            public TempDatasets WithObjects(string version, params string[] lines)
                => WithFile(version, "Objects.json", lines);

            public TempDatasets WithFile(string version, string fileName, params string[] lines)
            {
                string directory = Path.Combine(Root, version);
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, fileName), lines);
                return this;
            }

            public void Dispose()
            {
                // A locked or already-removed temp tree must not fail the test that produced it;
                // the leftover is a few kilobytes under TEMP. Anything else is a real fault and
                // is left to propagate.
                try { Directory.Delete(Root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
