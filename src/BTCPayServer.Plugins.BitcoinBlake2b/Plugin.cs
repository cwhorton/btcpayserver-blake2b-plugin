using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BitcoinBlake2b;

public class Plugin : BaseBTCPayServerPlugin
{
    public const string Area = "BitcoinBlake2b";
    public const string ViewsDirectory = "/Plugins/" + Area + "/Views";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddUIExtension("user-nav", $"{ViewsDirectory}/NavExtension.cshtml");
    }
}
