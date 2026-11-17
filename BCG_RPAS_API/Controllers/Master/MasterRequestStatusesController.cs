using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/request-status")]
    public class MasterRequestStatusesController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;

        public MasterRequestStatusesController(BCG_RPASContext bcg)
        {
            _bcg = bcg;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken)
        {
            var query = from _data in _bcg.MasterRequestStatuses.AsNoTracking()
                        where _data.Active == 1
                        select new
                        {
                            _data.Id,
                            _data.StatusCode,
                            _data.Name
                        };

            var result = await query.ToListAsync(cancellationToken);

            return Ok(result);
        }
    }
}
