using CadAI.Core.Modal;
using CadAI.ModalAgent;
using Xunit;

namespace CadAI.Tests;

/// <summary>
/// ModalAgent 配置回归测试：确保交付的 modal-agent.json 可正常解析且包含安全阈值与共享词表。
/// </summary>
public class ModalAgentConfigTests
{
    private static string ShippedConfigPath()
        => TestPaths.RepoFile("src", "CadAI.ModalAgent", "modal-agent.json");

    [Fact]
    public void ShippedJson_LoadsWithoutFallingBackToDefaults()
    {
        AgentConfig config = AgentConfig.Load(ShippedConfigPath());

        Assert.Equal(string.Empty, AgentConfig.LastLoadError);
        Assert.True(config.EnableDialogBridge);
        Assert.Equal("immediate", config.DecisionTrigger);
        Assert.Equal("whitelist", config.Mode);
        Assert.False(config.Debug);
        Assert.NotNull(config.PipeName);
    }

    [Fact]
    public void GenericTemplate_WatchesNoHostByDefault()
    {
        AgentConfig generic = AgentConfig.Load(ShippedConfigPath());

        Assert.Empty(generic.ProcessNames);
    }

    [Fact]
    public void ShippedJson_UsesTheSharedSafetyTables()
    {
        AgentConfig shipped = AgentConfig.Load(ShippedConfigPath());

        Assert.Equal(ModalDecisionSafety.DefaultDenyWords, shipped.DecisionDenyWords);
        Assert.Equal(ModalDecisionSafety.DefaultExpanderLabels, shipped.ExpanderButtons);
    }

    [Fact]
    public void ShippedJson_CarriesTheDialogSizeCeiling()
    {
        AgentConfig shipped = AgentConfig.Load(ShippedConfigPath());

        Assert.Equal(new AgentConfig().MaxDialogChildWindows, shipped.MaxDialogChildWindows);
        Assert.True(shipped.MaxDialogChildWindows > 0);
    }

    [Fact]
    public void BuiltInDefaults_UseTheSharedSafetyTables()
    {
        var config = new AgentConfig();

        Assert.Equal(ModalDecisionSafety.DefaultDenyWords, config.DecisionDenyWords);
        Assert.Equal(ModalDecisionSafety.DefaultExpanderLabels, config.ExpanderButtons);
    }

    [Fact]
    public void MissingConfigFile_FallsBackToBuiltInDefaults()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "cadai-modal-agent-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.False(File.Exists(missing));

        AgentConfig config = AgentConfig.Load(missing);

        Assert.Equal(string.Empty, AgentConfig.LastLoadError);
        Assert.Equal(new AgentConfig().Rules.Count, config.Rules.Count);
    }

    [Fact]
    public void BrokenConfigFile_FallsBackToDefaultsAndReportsWhy()
    {
        string broken = Path.Combine(AppContext.BaseDirectory, "cadai-modal-agent-broken-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(broken, "{ \"Rules\": [ { \"Match\": \"NotARealScope\" } ] }");
        try
        {
            AgentConfig config = AgentConfig.Load(broken);

            Assert.NotEqual(string.Empty, AgentConfig.LastLoadError);
            Assert.Equal(new AgentConfig().Rules.Count, config.Rules.Count);
        }
        finally
        {
            File.Delete(broken);
        }
    }
}
