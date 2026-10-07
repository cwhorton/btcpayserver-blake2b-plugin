using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BitcoinBlake2b;

[Area(Plugin.Area)]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIBitcoinBlake2bController : Controller
{
    [HttpGet("~/plugins/bitcoin-blake2b")]
    public IActionResult Index()
    {
        return View();
    }
}
