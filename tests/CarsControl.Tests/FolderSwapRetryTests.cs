using CarsControl.Setup;

namespace CarsControl.Tests;

// Changes FolderSwap's static retry settings, so these tests must not run alongside other FolderSwap tests.
[Collection("FolderSwap statics")]
public class FolderSwapRetryTests
{
    [Fact]
    public void A_file_briefly_in_use_delays_the_move_instead_of_failing_it()
    {
        using var t = new TempTree();
        var from = t.Dir("app");
        var held = t.File(@"app\CarsControl.exe", "exe");
        var to = Path.Combine(t.Root, "app.old");
        var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read); // like antivirus reading it
        var pauses = 0;
        var (attempts, pause) = (FolderSwap.RetryAttempts, FolderSwap.Pause);
        FolderSwap.Pause = _ => { if (++pauses == 2) stream.Dispose(); };
        try
        {
            var log = new List<string>();
            FolderSwap.MoveFolder(from, to, log.Add);
            Assert.True(Directory.Exists(to));
            Assert.False(Directory.Exists(from));
            Assert.Equal(2, log.Count);
        }
        finally
        {
            stream.Dispose();
            (FolderSwap.RetryAttempts, FolderSwap.Pause) = (attempts, pause);
        }
    }

    [Fact]
    public void A_file_that_stays_in_use_still_fails_after_the_retries()
    {
        using var t = new TempTree();
        var from = t.Dir("app");
        var held = t.File(@"app\CarsControl.exe", "exe");
        var (attempts, pause) = (FolderSwap.RetryAttempts, FolderSwap.Pause);
        FolderSwap.RetryAttempts = 3;
        FolderSwap.Pause = _ => { };
        try
        {
            using var stream = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.ThrowsAny<IOException>(() => FolderSwap.MoveFolder(from, Path.Combine(t.Root, "app.old")));
            Assert.True(Directory.Exists(from));
        }
        finally { (FolderSwap.RetryAttempts, FolderSwap.Pause) = (attempts, pause); }
    }
}
