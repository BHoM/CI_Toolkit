using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BH.Engine.Test;                               // Modify.Merge
using BH.Engine.Test.CodeCompliance.DynamicChecks;  // Query.IsValidDataset
using BH.oM.Test;                                   // TestStatus
using BH.oM.Test.Results;                           // TestResult, ITestInformation

class DatasetComplianceRunner
{
    static int Main(string[] args)
    {
        // CLI: DatasetComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                              <file1.json> [file2.json ...]
        //
        // Only processes .json files whose path contains "datasets" (case-insensitive),
        // mirroring BHoMBot's dataset filtering.
        var (outputFormat, sarifFilePath, files) = ArgParser.ParseDataset(args);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DatasetComplianceRunner [--output console|github|json|sarif] [--sarif-file PATH]");
            Console.WriteLine("                          <file1.json> [file2.json ...]");
            Console.WriteLine();
            Console.WriteLine("  --output github  = emit ::error/::warning for GitHub Actions (shows in PR).");
            Console.WriteLine("  --output json    = single JSON object to stdout.");
            Console.WriteLine("  --output sarif   = SARIF 2.1 to stdout (or --sarif-file for a file).");
            return 1;
        }

        if (outputFormat == "sarif" && !string.IsNullOrEmpty(sarifFilePath))
            outputFormat = "sarif-file";

        bool verbose = outputFormat == "console";
        if (verbose) Console.WriteLine("Running BHoM DATASET compliance...");

        var mergedResult   = new TestResult() { Status = TestStatus.Pass, Information = new List<ITestInformation>() };
        var allAnnotations = new List<Annotation>();
        var accounting     = new FileAccounting(files.Count);
        var demotedTotal   = 0;

        foreach (var file in files)
        {
            // Only .json files under a datasets/ path are in scope.
            if (!FileFilter.IsDatasetFile(file)) { accounting.CountNotRelevant(); continue; }

            if (verbose) Console.WriteLine($"\n=== Checking: {file} ===");

            if (!File.Exists(file))
            {
                accounting.CountNotOnDisk();
                Console.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            // One file must not take the run down with it, for the same reason it must not in
            // ComplianceRunner: an escaping exception kills the process before OutputEmitter
            // runs, so the check goes red with no annotation, no coverage line, and no name for
            // the file that caused it, and the findings already gathered for every other file
            // are lost. The run still fails below on accounting.Failed, so this converts a
            // silent abort into a stated one rather than making the check more permissive.
            //
            // IsValidDataset opens the file with an unguarded StreamReader before it parses
            // anything, so the throw does not need a malformed dataset to reach it. Reproduced
            // by holding the file open with no sharing, which is what an antivirus scan or a
            // concurrent step does.
            try
            {
                var resultForThisFile = file.IsValidDataset();

                if (resultForThisFile == null)
                {
                    accounting.CountNoResult();
                    Console.WriteLine($"  [SKIP] No result returned for: {file}");
                    continue;
                }

                // The Confidence rule errors on a dataset that leaves the field Undefined, which
                // is right for a dataset the repository ships and wrong for a fixture driving
                // its own tests. Demoted here rather than in the engine, and before the merge
                // rather than at the annotation, because Modify.Merge combines only the two
                // top-level statuses and the exit code reads the merged one.
                if (FileFilter.IsUnitTestFixture(file))
                {
                    resultForThisFile = DatasetSeverity.DemoteToWarning(
                        resultForThisFile, DatasetSeverity.ConfidenceRule, out int demotedHere);
                    demotedTotal += demotedHere;
                }

                if (verbose) Console.WriteLine($"  Result Status: {resultForThisFile.Status}");

                accounting.CountExamined();
                mergedResult = mergedResult.Merge(resultForThisFile);

                var information        = resultForThisFile.Information ?? Enumerable.Empty<ITestInformation>();
                var perFileAnnotations = information
                    .Select(i => i.ToAnnotationEquivalent())
                    .ToList();
                var infoList = information.ToList();

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
                    // Dataset checks don't populate Location.FilePath, so anchor to the JSON file
                    // so GitHub can produce an inline PR annotation instead of falling back to
                    // "unknown".
                    if (string.IsNullOrEmpty(a.FilePath))
                        a.FilePath = file;
                    if (a.LineStart <= 0)
                        a.LineStart = 1;

                    allAnnotations.Add(a);
                }
            }
            catch (Exception ex)
            {
                accounting.CountFailed();
                allAnnotations.Add(Annotation.ForUnanalysableFile(file, ex));

                // Stack trace to stderr, not into the annotation. GitHub truncates a long
                // annotation message, and the one thing the author needs is the file name.
                Console.Error.WriteLine($"  [FAIL] {file}: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
            }
        }

        // Guarded on the format for the same reason OutputEmitter guards the coverage line:
        // json and sarif put a payload on stdout and nothing may precede it.
        if (demotedTotal > 0 && (outputFormat == "console" || outputFormat == "github"))
        {
            string notice = DatasetSeverity.DemotionNotice(demotedTotal);
            Console.WriteLine(outputFormat == "github"
                ? $"::notice title=Dataset confidence::{notice}"
                : $"NOTICE: {notice}");
        }

        const string checkType = "dataset";
        OutputEmitter.Write(outputFormat, checkType, mergedResult.Status, allAnnotations, sarifFilePath, verbose,
            BH.Engine.Base.Query.DocumentationURL("DevOps/Code%20Compliance%20and%20CI/Compliance%20Checks/"),
            accounting);

        // Exit code mirrors BHoMBot: failure only on Error; Warning and Pass are both success.
        // A file the engine threw on also fails the run. It is not a finding about the dataset,
        // so it does not reach mergedResult, and without this a crash would exit 0 and report a
        // green check having examined less than it was asked to.
        return mergedResult.Status == TestStatus.Error || accounting.Failed > 0 ? 1 : 0;
    }

}
