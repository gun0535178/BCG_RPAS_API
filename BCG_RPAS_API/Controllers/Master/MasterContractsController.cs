using BCG_RPAS_API.DTOs.Master;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/contract")]
    public class MasterContractsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public MasterContractsController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var query =
                from c in _bcg.MasterContracts.AsNoTracking()
                where includeInactive || c.Active == 1
                orderby c.ContractNumber
                select new
                {
                    c.Id,
                    c.ContractNumber,
                    c.VendorId,
                    VendorCode = c.Vendor.VendorCode,
                    VendorName = c.Vendor.Name,
                    c.RentalTypeId,
                    RentalTypeCode = c.RentalType.RentalTypeCode,
                    RentalTypeName = c.RentalType.Name,
                    c.Active,
                    c.ConcurrencyVersion,
                    c.ModifyDate,
                    c.CreateDate
                };

            var result = await query.ToListAsync(cancellationToken);
            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertContractModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var contractNumber = request.ContractNumber?.Trim();
            if (string.IsNullOrWhiteSpace(contractNumber))
            {
                return BadRequest(new { detail = "กรุณาระบุเลขที่สัญญา (Contract Number)" });
            }

            if (contractNumber.Length > 100)
            {
                return BadRequest(new { detail = "เลขที่สัญญาต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (request.VendorId <= 0)
            {
                return BadRequest(new { detail = "กรุณาเลือกผู้ให้เช่า (Vendor)" });
            }

            if (request.RentalTypeId <= 0)
            {
                return BadRequest(new { detail = "กรุณาเลือกประเภทการเช่า (Rental Type)" });
            }

            var vendorExists = await _bcg.MasterVendors
                .AsNoTracking()
                .AnyAsync(v => v.Id == request.VendorId, cancellationToken);
            if (!vendorExists)
            {
                return BadRequest(new { detail = "ไม่พบข้อมูลผู้ให้เช่า (Vendor) ที่เลือก" });
            }

            var rentalTypeExists = await _bcg.MasterRentalTypes
                .AsNoTracking()
                .AnyAsync(rt => rt.Id == request.RentalTypeId, cancellationToken);
            if (!rentalTypeExists)
            {
                return BadRequest(new { detail = "ไม่พบข้อมูลประเภทการเช่า (Rental Type) ที่เลือก" });
            }

            var isDuplicate = await _bcg.MasterContracts
                .AsNoTracking()
                .AnyAsync(c => c.Id != request.Id && c.ContractNumber == contractNumber, cancellationToken);
            if (isDuplicate)
            {
                return Conflict(new { detail = $"เลขที่สัญญา '{contractNumber}' มีอยู่ในระบบแล้ว" });
            }

            var now = DateTime.Now;
            var activeFlag = request.Active == 0 ? 0 : 1;

            if (request.Id == 0)
            {
                var entity = new MasterContract
                {
                    ContractNumber = contractNumber,
                    VendorId = request.VendorId,
                    RentalTypeId = request.RentalTypeId,
                    Active = activeFlag,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterContracts.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterContracts
                    .SingleOrDefaultAsync(c => c.Id == request.Id, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลสัญญาเช่าที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.ContractNumber = contractNumber;
                existing.VendorId = request.VendorId;
                existing.RentalTypeId = request.RentalTypeId;
                existing.Active = activeFlag;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
                });
            }
        }
    }
}
