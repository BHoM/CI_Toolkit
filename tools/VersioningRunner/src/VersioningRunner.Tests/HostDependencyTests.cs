using System.Reflection;
using System.Runtime.Versioning;
using VersioningRunner.Commands;
using Xunit;

namespace VersioningRunner.Tests
{
    // Asserts properties of the host this runner executes on rather than of the code,
    // so it is only meaningful on the platform the runner actually runs on. Same shape
    // as SerialiserRunner.Tests.HostDependencyTests, and for the same class of gap.
    public class HostDependencyTests
    {
        // The reason this project and the runner target net8.0-windows rather than net8.0.
        //
        // BHoM's Revit UI assemblies reference WPF. On a plain net8.0 host those
        // references resolve to nothing, so asm.GetTypes() throws
        // ReflectionTypeLoadException and reading Type.Namespace on the partial results
        // throws as well. BuildSubjectNamespaces counts those as unreadable and skips
        // them, which drops the namespace from the subject set. A versioning failure in
        // a dropped namespace is then not attributable to the repo that owns it, so it
        // is silently discarded: the check reports a green it has not earned, which is
        // the exact failure shape this whole check was rebuilt to remove.
        //
        // Measured on a real closure with the Revit mocks present, which isolates the
        // WPF cause from the RevitAPIUI one. Every Revit tool's UI assembly reported
        // most of its types as null on net8.0 and a further tranche as unreadable;
        // on net8.0-windows the same assemblies reported none of either and gained
        // namespaces. The project file carries the same reasoning for the TFM itself.
        //
        // Loaded by name rather than referenced in code, so this fails rather than
        // refusing to compile if the TFM is reverted.
        [Theory]
        [InlineData("System.Windows.Window, PresentationFramework")]
        [InlineData("System.Windows.Media.Brush, PresentationCore")]
        [InlineData("System.Windows.DependencyObject, WindowsBase")]
        [InlineData("System.Windows.Forms.Form, System.Windows.Forms")]
        public void DesktopFrameworkTypes_AreResolvable_SoRevitUiAssembliesCanBeReflectedOver(string typeName)
        {
            Assert.NotNull(Type.GetType(typeName, throwOnError: false));
        }

        // The tests above assert a property of the TEST host, and this project is
        // net8.0-windows, so they would keep passing if the runner alone were reverted
        // to net8.0. Reverting the TargetFramework by itself is already caught loudly
        // at build time (NETSDK1136, because UseWPF requires a Windows target), but
        // removing UseWPF/UseWindowsForms at the same time would compile and leave the
        // tests green while the runner silently lost WPF resolution again. This asserts
        // the property on the runner assembly itself, which is the thing that matters.
        [Fact]
        public void RunnerAssembly_TargetsWindows_NotPlainNet8()
        {
            var platform = typeof(RunCommand).Assembly.GetCustomAttribute<TargetPlatformAttribute>();

            Assert.NotNull(platform);
            Assert.StartsWith("Windows", platform!.PlatformName, StringComparison.OrdinalIgnoreCase);
        }

        // Carried over from the existing runner behaviour rather than added by the TFM
        // change: BHoM types reference System.Drawing.Common with the 4.0.0.1 identity,
        // and Program.cs installs a resolver that remaps it to the modern package. If
        // Bitmap cannot be resolved at all, reflection over any BHoM type whose object
        // graph touches it throws TypeLoadException during FromJsonDatasets.
        [Fact]
        public void Bitmap_IsResolvable_SoReflectionOverBHoMTypesSucceeds()
        {
            Assert.NotNull(Type.GetType("System.Drawing.Bitmap, System.Drawing.Common", throwOnError: false));
        }
    }
}
