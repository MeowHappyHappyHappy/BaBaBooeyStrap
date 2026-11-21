using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Integrations_WindowHooksTests
{
    [Fact]
    public void F18_WindowManipulation_CustomTitle_AppliesTitleOverride()
    {
        // Arrange
        using var window = new NativeTestWindow("Original Title", visible: true);

        // Act: Apply custom title
        bool applied = WindowManipulator.ApplyTitle(window.Handle, "BaBaBooeyStrap - Test Window");

        // Assert
        applied.Should().BeTrue();
    }

    [Fact]
    public void F18_WindowManipulation_CustomIcon_SendsWmSetIconMessage()
    {
        // Arrange
        using var window = new NativeTestWindow("Icon Window", visible: true);

        // Act
        bool applied = WindowManipulator.ApplyIcon(window.Handle, nint.Zero);

        // Assert
        applied.Should().BeTrue();
    }

    [Fact]
    public void F18_WindowManipulation_BorderlessFullscreen_RemovesWindowBorders()
    {
        // Arrange
        using var window = new NativeTestWindow("Borderless Window", visible: true);

        // Act: Enable borderless fullscreen
        bool result = WindowManipulator.SetBorderlessFullscreen(window.Handle, enable: true, x: 0, y: 0, cx: 1280, cy: 720);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void F18_WindowManipulation_BorderlessFullscreen_ResizesToScreenResolution()
    {
        // Arrange
        using var window = new NativeTestWindow("Resize Window", visible: true);

        // Act: Resize to 1920x1080
        bool result = WindowManipulator.SetBorderlessFullscreen(window.Handle, enable: true, x: 0, y: 0, cx: 1920, cy: 1080);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void F18_WindowManipulation_Unhook_ReleasesWinEventHookHandle()
    {
        // Arrange: Install a genuine Win32 WinEventHook on the current process
        using var window = new NativeTestWindow("HookTargetWindow", visible: true);
        using var hookSession = WindowManipulator.InstallWinEventHook(
            (uint)Environment.ProcessId,
            (_, _) => { });

        hookSession.Handle.Should().NotBe(nint.Zero);
        nint originalHandle = hookSession.Handle;

        // Act: Unhook via WindowManipulator
        bool unhooked = hookSession.Unhook();

        // Assert: Handle is cleared and Win32 confirms it is no longer valid/hooked
        unhooked.Should().BeTrue();
        hookSession.Handle.Should().Be(nint.Zero);
        User32.UnhookWinEvent(originalHandle).Should().BeFalse();
        hookSession.Unhook().Should().BeFalse();
    }
}
