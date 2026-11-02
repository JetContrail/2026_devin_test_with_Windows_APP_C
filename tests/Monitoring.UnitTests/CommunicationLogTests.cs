using Monitoring.Net;
using Xunit;

namespace Monitoring.UnitTests;

public class CommunicationLogTests
{
    [Fact]
    public void Write_WithLabel_IncludesDeviceName()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"commlog-test-{Guid.NewGuid():N}");
        try
        {
            using (var log = new CommunicationLog(dir, "FrontDoor"))
            {
                log.Frame("tx", 0x01, 7, 9, true);
                log.Event("connect 127.0.0.1:50001");
            }
            var file = Directory.GetFiles(dir).Single();
            Assert.Contains("comm-FrontDoor-", Path.GetFileName(file));
            var content = File.ReadAllText(file);
            Assert.Contains("\"dev\":\"FrontDoor\"", content);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Write_LabelsCollidingAfterSanitize_GetDistinctFiles()
    {
        // regression: "Front Door" and "Front_Door" both sanitize to
        // "Front_Door" — without a raw-name tag they'd share one file;
        // and a crafted "Front_Door-<TAG>" must not collide either
        var dir = Path.Combine(Path.GetTempPath(), $"commlog-test-{Guid.NewGuid():N}");
        try
        {
            var frontDoorTag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("Front Door")))[..8];
            using (var a = new CommunicationLog(dir, "Front Door"))
                a.Event("a");
            using (var b = new CommunicationLog(dir, "Front_Door"))
                b.Event("b");
            using (var c = new CommunicationLog(dir, $"Front_Door-{frontDoorTag}"))
                c.Event("c");
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            Assert.Equal(3, files.Distinct().Count());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Write_WhenLogFileCannotBeCreated_DoesNotThrow()
    {
        // regression: a log write failure must never propagate into the
        // communication path (disk full / read-only FS → monitoring keeps running).
        // Occupy the log path with a *directory* so FileStream creation fails
        // on every OS (deleting the file itself would still succeed on Linux).
        var dir = Path.Combine(Path.GetTempPath(), $"commlog-test-{Guid.NewGuid():N}");
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("dev")))[..8];
        var blocker = Path.Combine(dir, $"comm-dev-{tag}-{DateTime.Now:yyyyMMdd}.log");
        Directory.CreateDirectory(blocker);
        try
        {
            var log = new CommunicationLog(dir, "dev");
            for (int i = 0; i < 10; i++)
                log.Event($"write {i}"); // must not throw, then disables itself
            log.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
