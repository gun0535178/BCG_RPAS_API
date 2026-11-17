namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/cost-center")]
    public class MasterCostCentersController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;

        public MasterCostCentersController(BCG_RPASContext bcg)
        {
            _bcg = bcg;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken)
        {
            var queryMasterCostCenters = from _data in _bcg.MasterCostCenters.AsNoTracking()
            where _data.Active == 1
            orderby _data.CostCenterCode
            select new
            {
                _data.Id,
                _data.CostCenterCode,
                _data.Name
            };

            var result = await queryMasterCostCenters.ToListAsync(cancellationToken);

            return Ok(result);
        }
    }
}
