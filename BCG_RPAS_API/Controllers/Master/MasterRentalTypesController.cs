using BCG_RPAS_API.DTOs.Master;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/rental-type")]
    public class MasterRentalTypesController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public MasterRentalTypesController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            CancellationToken cancellationToken = default)
        {
            var query =
                from _data in _bcg.MasterRentalTypes.AsNoTracking()
                where _data.Active == 1
                orderby _data.Id
                select new
                {
                    _data.Id,
                    _data.RentalTypeCode,
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
            [FromBody] UpsertRentalTypeModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var now = DateTime.Now;

            // Soft Delete (Active = 0)
            if (request.Id > 0 && request.Active == 0)
            {
                var deleteQuery =
                    from _data in _bcg.MasterRentalTypes
                    where _data.Active == 1
                        && _data.Id == request.Id
                    select _data;

                var toDelete = await deleteQuery.SingleOrDefaultAsync(cancellationToken);

                if (toDelete == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลประเภทการเช่าที่ต้องการลบ" });
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
                    message = "ลบข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                });
            }

            var code = request.RentalTypeCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { detail = "กรุณาระบุรหัสประเภทการเช่า (Rental Type Code)" });
            }

            if (code.Length > 100)
            {
                return BadRequest(new { detail = "รหัสประเภทการเช่าต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อประเภทการเช่า" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อประเภทการเช่าต้องไม่เกิน 200 ตัวอักษร" });
            }

            var duplicateQuery =
                from _data in _bcg.MasterRentalTypes
                where _data.Id != request.Id
                    && _data.RentalTypeCode == code
                select _data;

            var duplicateEntity = await duplicateQuery.SingleOrDefaultAsync(cancellationToken);

            if (duplicateEntity != null)
            {
                if (duplicateEntity.Active == 1)
                {
                    return Conflict(new { detail = $"รหัสประเภทการเช่า '{code}' มีอยู่ในระบบแล้ว" });
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
                        message = "เพิ่มข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                    });
                }

                return Conflict(new { detail = $"รหัสประเภทการเช่า '{code}' ซ้ำกับรายการที่มีประวัติในระบบ" });
            }

            if (request.Id == 0)
            {
                var maxIdQuery =
                    from _data in _bcg.MasterRentalTypes.AsNoTracking()
                    select (int?)_data.Id;

                var maxId = await maxIdQuery.MaxAsync(cancellationToken) ?? 0;

                var entity = new MasterRentalType
                {
                    Id = maxId + 1,
                    RentalTypeCode = code,
                    Name = name,
                    Active = 1,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterRentalTypes.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                });
            }
            else
            {
                var existingQuery =
                    from _data in _bcg.MasterRentalTypes
                    where _data.Active == 1
                        && _data.Id == request.Id
                    select _data;

                var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลประเภทการเช่าที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.RentalTypeCode = code;
                existing.Name = name;
                existing.Active = 1;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                });
            }
        }
    }
}
