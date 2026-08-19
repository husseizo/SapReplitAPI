namespace SapReplitAPI.Models.SoDelivery;

public static class RunStatus
{
    public const string Running   = "Running";
    public const string Completed = "Completed";
    public const string Aborted   = "Aborted";
}

public static class SoLogStatus
{
    public const string Success               = "SUCCESS";
    public const string Failed                = "FAILED";
    public const string Skipped               = "SKIPPED";
    public const string SkippedAlreadyDone    = "SKIPPED_ALREADY_PROCESSED";
    public const string Exception             = "EXCEPTION";
}

public static class LineLogStatus
{
    public const string Ok           = "OK";
    public const string Insufficient = "INSUFFICIENT";
    public const string Skipped      = "SKIPPED";
}
