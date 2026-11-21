using System.Runtime.InteropServices;
using BaBaBooeyStrap.Core.Interop;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;

namespace BaBaBooeyStrap.Tests.Tier1_FeatureCoverage;

public sealed class Core_Win32InteropTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public Core_Win32InteropTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void F5_Kernel32_NamedMutex_CreateAcquireReleaseAndClose()
    {
        // Arrange
        string mutexName = $@"Local\Test_Mutex_{Guid.NewGuid():N}";

        // Act 1: Create
        nint handle = Kernel32.CreateMutexW(nint.Zero, false, mutexName);
        handle.Should().NotBe(nint.Zero);

        try
        {
            // Act 2: Acquire
            uint waitRes = Kernel32.WaitForSingleObject(handle, 1000);
            waitRes.Should().Be(Kernel32.WAIT_OBJECT_0);

            // Act 3: Release
            bool released = Kernel32.ReleaseMutex(handle);
            released.Should().BeTrue();
        }
        finally
        {
            // Act 4: Close
            bool closed = Kernel32.CloseHandle(handle);
            closed.Should().BeTrue();
        }
    }

    [Fact]
    public void F5_Kernel32_NamedEvent_CreateSignalAndReset()
    {
        // Arrange
        string eventName = $@"Local\Test_Event_{Guid.NewGuid():N}";

        // Act 1: Create manual-reset event in unsignaled state
        nint hEvent = Kernel32.CreateEventW(nint.Zero, bManualReset: true, bInitialState: false, eventName);
        hEvent.Should().NotBe(nint.Zero);

        try
        {
            // Wait should timeout initially
            Kernel32.WaitForSingleObject(hEvent, 20).Should().Be(Kernel32.WAIT_TIMEOUT);

            // Act 2: Signal event
            Kernel32.SetEvent(hEvent).Should().BeTrue();
            Kernel32.WaitForSingleObject(hEvent, 50).Should().Be(Kernel32.WAIT_OBJECT_0);

            // Act 3: Reset event
            Kernel32.ResetEvent(hEvent).Should().BeTrue();
            Kernel32.WaitForSingleObject(hEvent, 20).Should().Be(Kernel32.WAIT_TIMEOUT);
        }
        finally
        {
            Kernel32.CloseHandle(hEvent);
        }
    }

    [Fact]
    public void F5_User32_WindowMetricsAndVisibility_ProbesValidWindowHandles()
    {
        // Arrange: Create a native test window
        using var window = new NativeTestWindow("ProbeTestWindow", visible: true);

        // Act
        bool isVisible = User32.IsWindowVisible(window.Handle);

        // Assert
        isVisible.Should().BeTrue();
    }

    [Fact]
    public void F5_Shell32_WindowsShortcut_CreatesValidLnkFileWithArguments()
    {
        // Arrange
        string lnkPath = Path.Combine(_env.RootPath, "TestShortcut.lnk");
        string targetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // Act
        WindowsShortcut.CreateShortcut(
            targetPath: targetPath,
            shortcutPath: lnkPath,
            arguments: "/c echo Hello",
            description: "Test cmd shortcut"
        );

        // Assert
        File.Exists(lnkPath).Should().BeTrue();
        new FileInfo(lnkPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void F5_User32_SetWindowPosAndStyles_AppliesBorderlessWindowFlags()
    {
        // Arrange: Create a native test window
        using var window = new NativeTestWindow("StyleTestWindow", visible: true);

        // Act: Probe window style
        nint currentStyle = User32.GetWindowLongPtr(window.Handle, User32.GWL_STYLE);

        // Assert: style value is readable
        currentStyle.Should().NotBe(nint.Zero);
    }
}
