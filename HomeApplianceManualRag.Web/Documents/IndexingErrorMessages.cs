namespace HomeApplianceManualRag.Web.Documents;

public static class IndexingErrorMessages
{
    public const int MaxDisplayLength = 200;

    public const string UnsupportedManual =
        "This file does not appear to be a supported appliance manual.";
    public const string DocumentUnreadable =
        "We couldn't read this document. Check that it is a valid PDF with extractable text, then retry.";
    public const string ManualVerificationFailed =
        "We couldn't verify that this is an appliance manual. Check the file and retry.";
    public const string ImageAnalysisFailed =
        "Image analysis failed. Please retry processing.";
    public const string ProcessingFailed =
        "Document processing failed. Please retry. If the problem continues, contact support.";

    public static string ForException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            DocumentRejectedException => UnsupportedManual,
            DocumentParseException => DocumentUnreadable,
            ManualClassificationException => ManualVerificationFailed,
            ImageAnalysisException => ImageAnalysisFailed,
            _ => ProcessingFailed
        };
    }

    public static string? ForDisplay(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var normalized = message.Trim();
        return normalized switch
        {
            UnsupportedManual => UnsupportedManual,
            DocumentUnreadable => DocumentUnreadable,
            ManualVerificationFailed => ManualVerificationFailed,
            ImageAnalysisFailed => ImageAnalysisFailed,
            ProcessingFailed => ProcessingFailed,
            _ => ProcessingFailed
        };
    }
}
