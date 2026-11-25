namespace DeskCal.Core;

// The running app alone holds this handle. Windows releases the lock even after a process crash.
// A leftover empty file after power loss is harmless: probes verify the sharing lock.
public static class AppPresence
{
    public static IDisposable Register(Database database) =>
        new FileStream(database.FilePath + ".app-running", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);

    public static bool IsRunning(Database database)
    {
        try
        {
            using var probe = File.OpenHandle(database.FilePath + ".app-running", FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (IOException error) when ((error.HResult & 0xffff) == 32) { return true; } // ERROR_SHARING_VIOLATION
    }
}
