namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/contract")]
    public class MasterContractsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;

        public MasterContractsController(BCG_RPASContext bcg)
        {
            _bcg = bcg;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken)
        {
            var query =
                from c in _bcg.MasterContracts.AsNoTracking()
                where c.Active == 1
                orderby c.ContractNumber
                select new
                {
                    c.Id,
                    c.ContractNumber,
                    c.VendorId,
                    VendorName = c.Vendor.Name,
                    c.RentalTypeId,
                    RentalTypeName = c.RentalType.Name
                };

            var result = await query.ToListAsync(cancellationToken);

            return Ok(result);
        }
    }
}
