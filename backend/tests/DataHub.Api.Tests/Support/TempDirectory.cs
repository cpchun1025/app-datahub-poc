namespace DataHub.Api.Tests.Support;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        FullPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "datahub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public string File(string name) => System.IO.Path.Combine(FullPath, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
