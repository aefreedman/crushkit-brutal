namespace CrushKit.Brutal.Vulkan;

/// <summary>Best-effort teardown without losing the exception which caused teardown.</summary>
public static class ResourceLifetime
{
    public static void OnFailure(Action operation, Action cleanup)
    {
        try
        {
            operation();
        }
        catch (Exception original)
        {
            try
            {
                cleanup();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Operation failed and cleanup also reported errors.", original, cleanupFailure);
            }
            throw;
        }
    }

    public static void Run(Action operation, Action cleanup)
    {
        OnFailure(operation, cleanup);
        cleanup();
    }

    public static void ReleaseAll(params Action[] releases)
    {
        List<Exception>? errors = null;
        foreach (var release in releases)
        {
            try
            {
                release();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        if (errors != null)
            throw new AggregateException("Resource cleanup reported errors; all release steps were attempted.", errors);
    }
}
