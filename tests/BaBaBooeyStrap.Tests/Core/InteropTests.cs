using BaBaBooeyStrap.Core.Interop;

namespace BaBaBooeyStrap.Tests.Core;

public class InteropTests : IDisposable
{
    private readonly string _tempDirectory;

    public InteropTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "BaBaBooeyStrap_InteropTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void Kernel32_CreateMutex_AcquireAndRelease()
    {
        string mutexName = "Local\\BaBaBooeyStrap_TestMutex_" + Guid.NewGuid().ToString("N");
        nint hMutex = Kernel32.CreateMutexW(nint.Zero, bInitialOwner: false, mutexName);
        hMutex.Should().NotBe(nint.Zero);

        try
        {
            uint waitRes = Kernel32.WaitForSingleObject(hMutex, 1000);
            waitRes.Should().Be(Kernel32.WAIT_OBJECT_0);

            bool released = Kernel32.ReleaseMutex(hMutex);
            released.Should().BeTrue();
        }
        finally
        {
            Kernel32.CloseHandle(hMutex);
        }
    }

    [Fact]
    public void Kernel32_CreateEvent_SetAndReset()
    {
        string eventName = "Local\\BaBaBooeyStrap_TestEvent_" + Guid.NewGuid().ToString("N");
        nint hEvent = Kernel32.CreateEventW(nint.Zero, bManualReset: true, bInitialState: false, eventName);
        hEvent.Should().NotBe(nint.Zero);

        try
        {
            // Initial wait should timeout
            uint waitInitial = Kernel32.WaitForSingleObject(hEvent, 50);
            waitInitial.Should().Be(Kernel32.WAIT_TIMEOUT);

            // Signal event
            bool setSuccess = Kernel32.SetEvent(hEvent);
            setSuccess.Should().BeTrue();

            // Should now succeed immediately
            uint waitSignaled = Kernel32.WaitForSingleObject(hEvent, 1000);
            waitSignaled.Should().Be(Kernel32.WAIT_OBJECT_0);

            // Reset event
            bool resetSuccess = Kernel32.ResetEvent(hEvent);
            resetSuccess.Should().BeTrue();

            uint waitReset = Kernel32.WaitForSingleObject(hEvent, 50);
            waitReset.Should().Be(Kernel32.WAIT_TIMEOUT);
        }
        finally
        {
            Kernel32.CloseHandle(hEvent);
        }
    }

    [Fact]
    public void WindowsShortcut_CreateShortcut_CreatesValidLnkFile()
    {
        string targetExe = Environment.ProcessPath ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string shortcutFile = Path.Combine(_tempDirectory, "SubDir", "TestShortcut.lnk");

        WindowsShortcut.CreateShortcut(
            targetPath: targetExe,
            shortcutPath: shortcutFile,
            arguments: "-test arg",
            description: "Test shortcut description"
        );

        File.Exists(shortcutFile).Should().BeTrue();
        var fileInfo = new FileInfo(shortcutFile);
        fileInfo.Length.Should().BeGreaterThan(0);
    }
}
