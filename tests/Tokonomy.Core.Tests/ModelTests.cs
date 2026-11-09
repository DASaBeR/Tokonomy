using Tokonomy.Core.Config;
using Tokonomy.Core.Model;

namespace Tokonomy.Core.Tests;

public class ModelTests
{
    [Theory]
    [InlineData(GateStatus.Passed, false)]
    [InlineData(GateStatus.Failed, true)]
    [InlineData(GateStatus.PreExisting, false)]
    public void Only_new_failures_block(GateStatus status, bool blocking) =>
        Assert.Equal(blocking, new GateResult { Gate = "g", Status = status }.Blocking);

    [Fact]
    public void Gate_display_name_prefers_name_then_command()
    {
        Assert.Equal("test", new GateConfig { Name = "test", Cmd = ["pytest"] }.DisplayName);
        Assert.Equal("ruff check .", new GateConfig { Cmd = ["ruff", "check", "."] }.DisplayName);
        Assert.Equal("locked_regions", new GateConfig { Type = GateConfig.LockedRegionsType }.DisplayName);
    }

    [Fact]
    public void Policy_defaults_match_brief()
    {
        var policy = new Policy();

        Assert.Equal(2, policy.Escalation.MaxRetriesPerUnit);
        Assert.Equal(400, policy.Escalation.MaxDiffLines);
        Assert.Equal(3.00m, policy.Budget.MaxUsdPerTask);
        Assert.Equal(0.30, policy.Budget.MaxArchitectTokenShare);
    }
}
