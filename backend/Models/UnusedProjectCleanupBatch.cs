namespace backend.Models;

public sealed class UnusedProjectCleanupBatch
{
    public int Scanned { get; set; }

    public int Protected { get; set; }

    public int Blocked { get; set; }

    public int Eligible { get; set; }

    public int Deleted { get; set; }

    public int Failed { get; set; }

    public int Pending { get; set; }
}
