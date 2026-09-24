using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace PlaytimeGoals;

[ApiController]
[Route("Api/PlaytimeGoals")]
public sealed class PlaytimeGoalsController :
    ControllerBase {

    [HttpGet("{botName}")]
    public async Task<IActionResult>
        GetStatus(
            string botName
        ) =>
            Ok(
                await PlaytimeGoalsPlugin
                    .GetStatusResponse(
                        botName
                    )
                    .ConfigureAwait(false)
            );

    [HttpGet("{botName}/Library")]
    public async Task<IActionResult>
        GetLibrary(
            string botName
        ) =>
            Ok(
                await PlaytimeGoalsPlugin
                    .GetLibraryResponse(
                        botName
                    )
                    .ConfigureAwait(false)
            );

    [HttpGet("{botName}/Parental")]
    public async Task<IActionResult>
        GetParental(
            string botName
        ) =>
            Ok(
                await PlaytimeGoalsPlugin
                    .GetParentalResponse(
                        botName
                    )
                    .ConfigureAwait(false)
            );

    [HttpPost("{botName}/Parental/SelfTest/{appId}")]
    public async Task<IActionResult>
        RunParentalSelfTest(
            string botName,
            uint appId
        ) =>
            Ok(
                await PlaytimeGoalsPlugin
                    .RunParentalSelfTestResponse(
                        botName,
                        appId
                    )
                    .ConfigureAwait(false)
            );

}
