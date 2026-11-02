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
    public void Write_AfterLogDirDisappears_DoesNotThrow()
    {
        // regression: a log write failure must never propagate into the
        // communication path (disk full / removed dir → monitoring keeps running)
        var dir = Path.Combine(Path.GetTempPath(), $"commlog-test-{Guid.NewGuid():N}");
        var log = new CommunicationLog(dir, "dev");
        log.Event("first");
        Directory.Delete(dir, true);
        for (int i = 0; i < 10; i++)
            log.Event($"write {i}"); // must not throw, then disables itself
        log.Dispose();
    }
}
