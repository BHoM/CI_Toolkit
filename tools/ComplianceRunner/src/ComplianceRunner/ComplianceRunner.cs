using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BH.Engine.Test;                       // Modify.Merge
using BH.Engine.Test.CodeCompliance;        // Compute.RunChecks
using BH.oM.Test;                           // TestStatus
using BH.oM.Test.Results;                   // TestResult, ITestInformation

class ComplianceRunner
{
    static int Main(string[] args)
    {
        // CLI: ComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                       [--org-url URL]
        //                       <code|copyright|documentation|project> <file1> [file2 ...]
        var (outputFormat, sarifFilePath, checkType, files, orgUrl) = ArgParser.ParseCompliance(args);
        if (checkType == null || files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  ComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]");
            Console.WriteLine("                   [--org-url REPO_URL]");
            Console.WriteLine("                   <code|copyright|documentation|project> <file1> [file2 ...]");
            Console.WriteLine();
            Console.WriteLine("  --output github  = emit ::error/::warning for GitHub Actions (shows in PR).");
            Console.WriteLine("  --output json    = single JSON object to stdout.");
            Console.WriteLine("  --output sarif   = SARIF 2.1 to stdout (or --sarif-file for a file).");
            Console.WriteLine("  --org-url URL    = repository URL required for 'project' checks");
            Console.WriteLine("                     e.g. https://github.com/BHoM/BHoM_Engine");
            return 1;
        }

        if (outputFormat == "sarif" && !string.IsNullOrEmpty(sarifFilePath))
            outputFormat = "sarif-file";

        bool verbose = outputFormat == "console";
        if (verbose) Console.WriteLine($"Running BHoM {checkType.ToUpper()} compliance...");

        var mergedResult   = new TestResult() { Status = TestStatus.Pass, Information = new List<ITestInformation>() };
        var allAnnotations = new List<Annotation>();
        var accounting     = new FileAccounting(files.Count);

        foreach (var file in files)
        {
            // Each check type is only relevant to certain file extensions.
            if (!FileFilter.IsRelevantFile(file, checkType)) { accounting.CountNotRelevant(); continue; }

            if (verbose) Console.WriteLine($"\n=== Checking: {file} ===");

            if (!File.Exists(file))
            {
                accounting.CountNotOnDisk();
                Console.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            // One file must not take the run down with it. A rule that throws used to escape
            // Main, which killed the process before OutputEmitter ran: the check went red with
            // no annotation, no coverage line, and no name for the file that caused it, and the
            // findings already gathered for every other file were lost. The run still fails
            // below on accounting.Failed, so this converts a silent abort into a stated one
            // rather than making the check more permissive.
            try
            {
                TestResult resultForThisFile;

                if (checkType == "project")
                {
                    if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        resultForThisFile = BH.Engine.Test.CodeCompliance.Compute.CheckProjectFile(file, orgUrl);
                    else
                        resultForThisFile = BH.Engine.Test.CodeCompliance.Compute.CheckAssemblyInfo(file, orgUrl);

                    // Remap absolute location paths back to the relative file path so
                    // annotations point to the correct diff line — mirrors BHoMBot ProjectCompliance.cs.
                    // Note: .OfType<Error>() silently drops any ITestInformation that is not exactly
                    // BH.oM.Test.CodeCompliance.Error. Currently safe — the project engine only emits
                    // Error-typed findings — but revisit if new finding sub-types are introduced.
                    if (resultForThisFile?.Information != null)
                    {
                        resultForThisFile.Information = resultForThisFile.Information
                            .OfType<BH.oM.Test.CodeCompliance.Error>()
                            .Select(e => (ITestInformation)new BH.oM.Test.CodeCompliance.Error
                            {
                                Status            = e.Status,
                                Message           = e.Message,
                                Name              = e.Name,
                                BHoM_Guid         = e.BHoM_Guid,
                                UTCTime           = e.UTCTime,
                                DocumentationLink = e.DocumentationLink,
                                Location          = new BH.oM.Test.CodeCompliance.Location
                                {
                                    FilePath = file,
                                    Line     = e.Location?.Line
                                }
                            })
                            .ToList();
                    }
                }
                else
                {
                    // Test_Toolkit's [Path(@"..._Engine\\..")] rules match a literal backslash;
                    // flip git's forward-slash paths or every rule silently no-ops.
                    resultForThisFile = BH.Engine.Test.CodeCompliance.Compute.RunChecks(file.Replace('/', '\\'), checkType);

                    // Group findings one-per-line and remap the path to the diff-relative file,
                    // mirroring BHoMBot's GroupInformation -> GroupErrors pipeline (code/copyright/
                    // documentation only; project has its own remap above). GroupErrors concatenates
                    // each finding's FullMessage() into the grouped Error.Message, so downstream these
                    // annotations are built from that message directly (see the preformatted branch in
                    // AnnotationConvert) rather than via IFullMessage, which would append the
                    // documentation suffix a second time.
                    if (resultForThisFile?.Information != null)
                    {
                        var errors = resultForThisFile.Information
                            .OfType<BH.oM.Test.CodeCompliance.Error>()
                            .ToList();
                        resultForThisFile.Information = BH.Engine.Test.CodeCompliance.Modify
                            .GroupErrors(errors, file)
                            .Select(e => (ITestInformation)e)
                            .ToList();
                    }
                }

                if (resultForThisFile == null)
                {
                    accounting.CountNoResult();
                    Console.WriteLine($"  [SKIP] No result returned for: {file}");
                    continue;
                }

                if (verbose) Console.WriteLine($"  Result Status: {resultForThisFile.Status}");

                accounting.CountExamined();
                mergedResult = mergedResult.Merge(resultForThisFile);

                // Code/copyright/documentation findings have been through GroupErrors, whose
                // Error.Message is already the concatenated FullMessage(); take it verbatim.
                //
                // Project findings use IFullMessage, which appends the " - For more information
                // see <url>" documentation suffix. This is a deliberate, documented divergence from
                // BHoMBot: BHoMBot's ProjectCompliance emitted the bare Error.Message with no suffix,
                // even though it carried the DocumentationLink. Every other compliance check surfaces
                // that link (via GroupErrors -> FullMessage), and the project engine attaches a
                // DocumentationLink to every finding specifically so it can be shown, so BHoMBot's
                // omission was an inconsistency. Keeping the suffix makes all five checks uniform and
                // points authors at the remediation guidance. Nothing downstream parses this text
                // (pass/fail is status-driven; SARIF carries the link separately).
                bool preformatted      = checkType != "project";
                var information        = resultForThisFile.Information ?? Enumerable.Empty<ITestInformation>();
                var perFileAnnotations = information.Select(i => i.ToAnnotationEquivalent(preformatted)).ToList();
                var infoList           = information.ToList();

                for (int i = 0; i < perFileAnnotations.Count; i++)
                {
                    var a           = perFileAnnotations[i];
                    var displayPath = string.IsNullOrEmpty(a.FilePath) ? file : a.FilePath;
                    if (verbose)
                    {
                        Console.WriteLine($"  - [{a.Level}] {displayPath}:{a.LineStart}:{a.ColumnStart}" +
                                          $"-{a.LineEnd}:{a.ColumnEnd} [{a.RuleName}]");
                        Console.WriteLine($"    {a.Message}");
                        if (i < infoList.Count)
                            AnnotationConvert.LogDetailedFinding(infoList[i]);
                    }
                    allAnnotations.Add(a);
                }
            }
            catch (Exception ex)
            {
                accounting.CountFailed();
                allAnnotations.Add(FailureAnnotation(file, ex));

                // Stack trace to stderr, not into the annotation. GitHub truncates a long
                // annotation message, and the one thing the author needs is the file name.
                Console.Error.WriteLine($"  [FAIL] {file}: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
            }
        }

        OutputEmitter.Write(outputFormat, checkType, mergedResult.Status, allAnnotations, sarifFilePath, verbose,
            BH.Engine.Base.Query.DocumentationURL("DevOps/Code%20Compliance%20and%20CI/Compliance%20Checks/"),
            accounting);

        // Exit code mirrors BHoMBot: failure only on Error; Warning and Pass are both success.
        // A file the engine threw on also fails the run. It is not a finding about the code, so
        // it does not reach mergedResult, and without this a crash would exit 0 and report a
        // green check having examined less than it was asked to.
        return mergedResult.Status == TestStatus.Error || accounting.Failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// The annotation for a file the engine threw on. Carries the exception type and message so
    /// the author can tell a malformed file from an environment problem, and says plainly that
    /// this file was not checked, because a failure annotation that looks like a finding would
    /// send them looking for a defect in their own code.
    /// </summary>
    static Annotation FailureAnnotation(string file, Exception ex) => new Annotation
    {
        FilePath    = file,
        LineStart   = 1,
        LineEnd     = 1,
        ColumnStart = 1,
        ColumnEnd   = 1,
        Level       = "failure",
        RuleName    = "FileCouldNotBeAnalysed",
        Message     = $"This file could not be analysed and was not checked. "
                    + $"{ex.GetType().Name}: {ex.Message} "
                    + $"Every other file in this run was checked normally.",
        UTCTime     = DateTime.UtcNow,
    };
}
