namespace DeskCal.Core.Tests;

public sealed class ClientSecretImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deskcal-secret-" + Guid.NewGuid().ToString("N"));

    public ClientSecretImportTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void CopiesInstalledClientFileIntoPlace()
    {
        using var db = new TestDatabase();
        var source = Path.Combine(_folder, "client_secret_123.json");
        File.WriteAllText(source, """{"installed":{"client_id":"id-1.apps.googleusercontent.com","client_secret":"s-1"}}""");
        var target = Path.Combine(_folder, "app", "client_secret.json");
        var account = new GoogleAccount(db.Database, target);

        account.ImportClientSecret(source);

        Assert.True(account.HasClientSecret);
        Assert.Equal(File.ReadAllText(source), File.ReadAllText(target));
    }

    [Fact]
    public void RejectsJsonThatIsNotAClientFile()
    {
        using var db = new TestDatabase();
        var source = Path.Combine(_folder, "other.json");
        File.WriteAllText(source, """{"name":"not a client file"}""");
        var account = new GoogleAccount(db.Database, Path.Combine(_folder, "client_secret.json"));

        Assert.ThrowsAny<Exception>(() => account.ImportClientSecret(source));
        Assert.False(account.HasClientSecret);
    }
}
