using BCG_RPAS_API.DTOs.Master;
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
        private readonly GetCurrentUser _getCurrentUser;

        public MasterVendorsController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken = default)
        {
            var query = from _data in _bcg.MasterVendors.AsNoTracking()
                        where _data.Active == 1
                        orderby _data.VendorCode
                        select new
                        {
                            _data.Id,
                            _data.VendorCode,
                            _data.Name,
                            _data.Active,
                            _data.ConcurrencyVersion,
                            _data.ModifyDate,
                            _data.CreateDate
                        };

            var result = await query.ToListAsync(cancellationToken);
            return Ok(result);
        }

        [HttpPost]
        [Authorize(Roles = "MANAGER,EXECUTIVE,ADMINISTRATOR")]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertVendorModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var now = DateTime.Now;

            // Soft Delete (Active = 0)
            if (request.Id > 0 && request.Active == 0)
            {
                var toDelete = await _bcg.MasterVendors
                    .SingleOrDefaultAsync(v => v.Id == request.Id && v.Active == 1, cancellationToken);

                if (toDelete == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลคู่ค้าที่ต้องการลบ" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    toDelete.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                toDelete.Active = 0;
                toDelete.ModifyBy = userId.Value;
                toDelete.ModifyDate = now;
                toDelete.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = toDelete.Id,
                    message = "ลบข้อมูลคู่ค้า (Vendor) เรียบร้อยแล้ว"
                });
            }

            var code = request.VendorCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { detail = "กรุณาระบุรหัสคู่ค้า (Vendor Code)" });
            }

            if (code.Length > 100)
            {
                return BadRequest(new { detail = "รหัสคู่ค้าต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อผู้ให้เช่า/คู่ค้า (Vendor Name)" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อผู้ให้เช่าต้องไม่เกิน 200 ตัวอักษร" });
            }

            var duplicateEntity = await _bcg.MasterVendors
                .SingleOrDefaultAsync(v => v.Id != request.Id && v.VendorCode == code, cancellationToken);

            if (duplicateEntity != null)
            {
                if (duplicateEntity.Active == 1)
                {
                    return Conflict(new { detail = $"รหัสคู่ค้า '{code}' มีอยู่ในระบบแล้ว" });
                }

                if (request.Id == 0)
                {
                    duplicateEntity.Name = name;
                    duplicateEntity.Active = 1;
                    duplicateEntity.ModifyBy = userId.Value;
                    duplicateEntity.ModifyDate = now;
                    duplicateEntity.ConcurrencyVersion += 1;

                    await _bcg.SaveChangesAsync(cancellationToken);

                    return Ok(new
                    {
                        id = duplicateEntity.Id,
                        message = "เพิ่มข้อมูลคู่ค้า (Vendor) เรียบร้อยแล้ว"
                    });
                }

                return Conflict(new { detail = $"รหัสคู่ค้า '{code}' ซ้ำกับรายการที่มีประวัติในระบบ" });
            }

            if (request.Id == 0)
            {
                var entity = new MasterVendor
                {
                    VendorCode = code,
                    Name = name,
                    Active = 1,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterVendors.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลคู่ค้า (Vendor) เรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterVendors
                    .SingleOrDefaultAsync(v => v.Id == request.Id && v.Active == 1, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลคู่ค้าที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.VendorCode = code;
                existing.Name = name;
                existing.Active = 1;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลคู่ค้าเรียบร้อยแล้ว"
                });
            }
        }
    }
}
