using TextureStudio.Services;

namespace NBA2KCourtCreator.Studio;

internal static class StudioImageMemory
{
    internal static Func<long?> AvailableBytes { get; set; } = () => null;
    internal static void Require(long bytes) => MemoryPreflight.EnsureAllocation(bytes, AvailableBytes());
    internal static void RequirePixels(long pixels, int copies = 1) => Require(checked(pixels * 4 * copies));
}
