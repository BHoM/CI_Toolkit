using System;

/// <summary>Lightweight record carrying everything needed for any output format.</summary>
public class Annotation
{
    public string   FilePath         { get; set; } = "";
    public int      LineStart        { get; set; }
    public int      LineEnd          { get; set; }
    public int      ColumnStart      { get; set; }
    public int      ColumnEnd        { get; set; }
    /// <summary>"failure" (Error) or "warning" — matches GitHub's expected values.</summary>
    public string   Level            { get; set; } = "warning";
    /// <summary>
    /// Full message including the " - For more information see &lt;url&gt;" suffix,
    /// exactly matching BHoMBot's FullMessage() output.
    /// </summary>
    public string   Message          { get; set; } = "";
    /// <summary>Check method name (e.g. "HasValidCopyright") — used as the SARIF ruleId.</summary>
    public string   RuleName         { get; set; } = "";
    /// <summary>Fully-qualified documentation URL built via BH.Engine.Base.Query.DocumentationURL.</summary>
    public string   DocumentationUrl { get; set; } = "";
    /// <summary>BHoM_Guid from the engine's Error — unique identifier per finding for cross-run tracing.</summary>
    public string   BHoMGuid         { get; set; } = "";
    /// <summary>UTC timestamp at which the engine produced this finding.</summary>
    public DateTime UTCTime          { get; set; }

    /// <summary>
    /// The annotation for a file the engine threw on. Carries the exception type and message so
    /// the author can tell a malformed file from an environment problem, and says plainly that
    /// this file was not checked, because a failure annotation that looks like a finding would
    /// send them looking for a defect in their own code.
    ///
    /// Shared by both runners rather than written out in each, so a reword cannot land in one
    /// and not the other. It names no check and no engine, which is what lets it be shared.
    /// </summary>
    public static Annotation ForUnanalysableFile(string file, Exception ex) => new Annotation
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
