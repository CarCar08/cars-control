using CarsControl.Core.Health;
using CarsControl.Core.Help;

namespace CarsControl.Tests;

public class PersonalExtrasTests
{
    [Fact]
    public void Salad_is_found_by_its_inventory_entry_or_its_folder()
    {
        Assert.True(PersonalExtras.SaladInstalled(["Salad"], _ => false));
        Assert.True(PersonalExtras.SaladInstalled([], f => f.EndsWith(@"Programs\Salad", StringComparison.OrdinalIgnoreCase)));
        Assert.False(PersonalExtras.SaladInstalled(["Steam", "Discord"], _ => false));
    }

    [Fact]
    public void Proton_VPN_is_found_by_its_adapter_or_its_app()
    {
        Assert.True(PersonalExtras.ProtonVpnInstalled([], ["Proton VPN"]));
        Assert.True(PersonalExtras.ProtonVpnInstalled([new NetAdapter("ProtonVPN", "ProtonVPN Tunnel", false, 0, 7, false)], []));
        Assert.False(PersonalExtras.ProtonVpnInstalled([new NetAdapter("Ethernet", "Realtek PCIe 2.5GbE", true, 0, 3, true)], ["Proton Drive", "Proton Mail"]));
    }

    [Fact]
    public void Without_Salad_no_explainer_mentions_it_and_every_sentence_still_reads()
    {
        foreach (var topic in HelpTopics.All.Select(t => t.ForPc(saladInstalled: false)))
            foreach (var text in new[] { topic.What, topic.Does, topic.ToDo, topic.Safety })
                Assert.DoesNotContain("Salad", text);
        Assert.Same(HelpTopics.All[0], HelpTopics.All[0].ForPc(saladInstalled: true));
    }
}
