using System.Net;
using System.Text.Json;
using BaBaBooeyStrap.Bootstrapper.FastFlags;
using BaBaBooeyStrap.Core.Models;
using BaBaBooeyStrap.Core.Roblox;
using BaBaBooeyStrap.Core.Storage;
using BaBaBooeyStrap.MultiInstance;
using BaBaBooeyStrap.Tests.Mocks;
using BaBaBooeyStrap.Tests.Tier1_FeatureCoverage.Engines;
using FluentAssertions;
using Xunit;

namespace BaBaBooeyStrap.Tests.Tier3_CrossFeatureCombinations;

public sealed class CrossSubsystem_Invariants_With_AdversarialTests : IClassFixture<MockRobloxEnvironment>
{
    private readonly MockRobloxEnvironment _env;

    public CrossSubsystem_Invariants_With_AdversarialTests(MockRobloxEnvironment env)
    {
        _env = env;
    }

    [Fact]
    public void T3_CrossSubsystem_Adversarial_AbandonedMutexDuringActiveCliLaunch_RecoversSafely()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_Adv_AbandonLaunch");
        helper.SimulateAbandonedMutex();

        using var controller = new WindowsMultiInstanceController(helper.MutexName, helper.EventName);
        var status = controller.Enable();

        status.OwnsMutex.Should().BeTrue();
        status.LastError.Should().BeNull();
        LaunchGate.VerifyLaunchReadiness(controller, multiInstanceEnabled: true).CanLaunch.Should().BeTrue();
    }

    [Fact]
    public async Task T3_CrossSubsystem_Adversarial_ConcurrentNetworkAndNamedPipeDisruptions_RecoversWithoutDeadlock()
    {
        var httpHandler = new MockHttpMessageHandler();
        httpHandler.WhenPost("auth.roblox.com/v1/authentication-ticket")
            .RespondWithStatus(HttpStatusCode.InternalServerError);

        using var httpClient = httpHandler.ToHttpClient();
        using var authService = new RobloxAuthService(httpClient);
        using var discordClient = new DiscordRpcClient($"nonexistent-pipe-{Guid.NewGuid():N}");

        var t1 = Task.Run(async () =>
        {
            try
            {
                await authService.GenerateAuthTicketAsync("some_cookie");
            }
            catch (RobloxApiException)
            {
                // Expected failure
            }
        });

        var t2 = Task.Run(async () =>
        {
            bool connected = await discordClient.ConnectAsync(TimeSpan.FromMilliseconds(200));
            connected.Should().BeFalse();
        });

        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task T3_CrossSubsystem_Adversarial_RapidParallelFileSystemMutations_MaintainsJsonIntegrity()
    {
        string settingsPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_stress_settings.json");
        string ffPath = Path.Combine(_env.BaBaBooeyStrapPath, "t3_stress_flags.json");

        var settingsMgr = new JsonManager<Settings>(settingsPath);
        var ffm = new FastFlagManager(ffPath);

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            settingsMgr.Prop.AcrylicBackgroundOpacity = (byte)(60 + i * 10);
            settingsMgr.Save();

            ffm.SetFlag($"Flag_{i}", i);
            ffm.Save();
        })).ToArray();

        await Task.WhenAll(tasks);

        Action act1 = () =>
        {
            string json = File.ReadAllText(settingsPath);
            using var doc = JsonDocument.Parse(json);
        };
        act1.Should().NotThrow();

        Action act2 = () =>
        {
            string json = File.ReadAllText(ffPath);
            using var doc = JsonDocument.Parse(json);
        };
        act2.Should().NotThrow();
    }

    [Fact]
    public void T3_CrossSubsystem_Invariants_TestHarnessMockIsolation_PreventsSystemInterference()
    {
        using var helper = new ParameterizedMutexTestHelper("T3_IsolationCheck");

        helper.MutexName.Should().StartWith(@"Local\");
        helper.EventName.Should().StartWith(@"Local\");
        _env.RootPath.Should().StartWith(Path.GetTempPath());
    }
}
