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

            // ตรวจสอบว่ามีเลขที่สัญญานี้ในระบบหรือไม่ (รวมถึงรายการที่เคย Soft Delete Active = 0 เนื่องจาก Unique Index ใน DB)
            var duplicateEntity = await _bcg.MasterContracts
                .SingleOrDefaultAsync(c => c.Id != request.Id && c.ContractNumber == contractNumber, cancellationToken);

            if (duplicateEntity != null)
            {
                if (duplicateEntity.Active == 1)
                {
                    return Conflict(new { detail = $"เลขที่สัญญา '{contractNumber}' มีอยู่ในระบบแล้ว" });
                }

                // กรณีเป็นการสร้างใหม่ (Id == 0) แต่เลขที่สัญญานี้เคยถูก Soft Delete (Active == 0) ไว้ ให้ทำการเปิดใช้งานกลับมา (Reactivate) แทนการสร้างแถวซ้ำ
                if (request.Id == 0)
                {
                    duplicateEntity.VendorId = request.VendorId;
                    duplicateEntity.RentalTypeId = request.RentalTypeId;
                    duplicateEntity.Active = 1;
                    duplicateEntity.ModifyBy = userId.Value;
                    duplicateEntity.ModifyDate = now;
                    duplicateEntity.ConcurrencyVersion += 1;

                    await _bcg.SaveChangesAsync(cancellationToken);

                    return Ok(new
                    {
                        id = duplicateEntity.Id,
                        message = "เพิ่มข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
                    });
                }

                return Conflict(new { detail = $"เลขที่สัญญา '{contractNumber}' ซ้ำกับรายการที่มีประวัติในระบบ" });
            }

            if (request.Id == 0)
            {
                var entity = new MasterContract
                {
                    ContractNumber = contractNumber,
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
                    message = "เพิ่มข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
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

                existing.ContractNumber = contractNumber;
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
                    message = "บันทึกการแก้ไขข้อมูลสัญญาเช่าเรียบร้อยแล้ว"
                });
            }
        }
    }
}
