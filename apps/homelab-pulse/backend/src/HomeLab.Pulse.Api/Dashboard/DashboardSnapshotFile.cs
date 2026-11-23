namespace HomeLab.Pulse.Api.Dashboard;

internal static class DashboardSnapshotFile
{
    internal static async Task ReplaceAsync(string path, byte[] json, CancellationToken cancellationToken)
    {
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
