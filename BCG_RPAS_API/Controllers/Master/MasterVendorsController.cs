using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/vendor")]
    public class MasterVendorsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;

        public MasterVendorsController(BCG_RPASContext bcg)
        {
            _bcg = bcg;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken)
        {
            var query = from _data in _bcg.MasterVendors.AsNoTracking()
                        where _data.Active == 1
                        select new
                        {
                            _data.Id,
                            _data.VendorCode,
                            _data.Name
                        };

            var result = await query.ToListAsync(cancellationToken);

            return Ok(result);
        }
    }
}
