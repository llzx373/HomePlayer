using HomePlayer.AI;

namespace HomePlayer.AI.Tests;

public sealed class LiveTranslationServiceTests
{
    [Fact]
    public async Task Failed_start_can_be_retried_instead_of_returning_the_cached_fault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer.AI.Tests." + Guid.NewGuid().ToString("N"));
        try
        {
            var models = new AiModelManager(directory, () => "");
            var service = new LiveTranslationService(models, "missing-model", null, "zh", AiComputeDevice.Cpu, () => "");
            var first = service.StartAsync(null, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first);

            var second = service.StartAsync(null, CancellationToken.None);

            Assert.NotSame(first, second);
            await Assert.ThrowsAsync<InvalidOperationException>(() => second);
            await service.DisposeAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
