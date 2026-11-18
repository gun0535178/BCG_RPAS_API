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
            CancellationToken cancellationToken = default)
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
        [Authorize(Roles = "MANAGER,EXECUTIVE,ADMINISTRATOR")]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertContractModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var now = DateTime.Now;

            // กรณีสั่งลบแบบ Soft Delete (Active = 0) สำหรับรายการที่มีอยู่แล้ว
            if (request.Id > 0 && request.Active == 0)
            {
                var toDelete = await _bcg.MasterContracts
                    .SingleOrDefaultAsync(c => c.Id == request.Id && c.Active == 1, cancellationToken);

                if (toDelete == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลสัญญาเช่าที่ต้องการลบ" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    toDelete.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกเปลี่ยนแปลงโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                toDelete.Active = 0;
                toDelete.ModifyBy = userId.Value;
                toDelete.ModifyDate = now;
                toDelete.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = toDelete.Id,
                    message = "ลบข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
                });
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
                .AnyAsync(v => v.Id == request.VendorId && v.Active == 1, cancellationToken);
            if (!vendorExists)
            {
                return BadRequest(new { detail = "ไม่พบข้อมูลผู้ให้เช่า (Vendor) ที่เลือก" });
            }

            var rentalTypeExists = await _bcg.MasterRentalTypes
                .AsNoTracking()
                .AnyAsync(rt => rt.Id == request.RentalTypeId && rt.Active == 1, cancellationToken);
            if (!rentalTypeExists)
            {
                return BadRequest(new { detail = "ไม่พบข้อมูลประเภทการเช่า (Rental Type) ที่เลือก" });
            }

            if (request.Id == 0)
            {
                // Auto-generate เลขที่สัญญาในรูปแบบ RENT-{Year}-{NNN} เช่น RENT-2026-001
                var yearPrefix = $"RENT-{now.Year}-";
                var existingNumbers = await _bcg.MasterContracts
                    .AsNoTracking()
                    .Where(c => c.ContractNumber.StartsWith(yearPrefix))
                    .Select(c => c.ContractNumber)
                    .ToListAsync(cancellationToken);

                var maxSeq = 0;
                foreach (var num in existingNumbers)
                {
                    var suffix = num.Substring(yearPrefix.Length);
                    if (int.TryParse(suffix, out var parsed) && parsed > maxSeq)
                    {
                        maxSeq = parsed;
                    }
                }

                var generatedNumber = $"{yearPrefix}{(maxSeq + 1):D3}";

                var entity = new MasterContract
                {
                    ContractNumber = generatedNumber,
                    VendorId = request.VendorId,
                    RentalTypeId = request.RentalTypeId,
                    Active = 1,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterContracts.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    contractNumber = entity.ContractNumber,
                    message = $"เพิ่มสัญญาเช่า '{entity.ContractNumber}' เรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterContracts
                    .SingleOrDefaultAsync(c => c.Id == request.Id && c.Active == 1, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลสัญญาเช่าที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                // คงเลขที่สัญญาเดิมไว้ และอัปเดตเฉพาะ VendorId / RentalTypeId
                existing.VendorId = request.VendorId;
                existing.RentalTypeId = request.RentalTypeId;
                existing.Active = 1;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    contractNumber = existing.ContractNumber,
                    message = $"บันทึกการแก้ไขข้อมูลสัญญาเช่า '{existing.ContractNumber}' เรียบร้อยแล้ว"
                });
            }
        }
    }
}
