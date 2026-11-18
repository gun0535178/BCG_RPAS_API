using BCG_RPAS_API.DTOs.Master;
using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/role")]
    public class MasterRolesController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public MasterRolesController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var query = from _data in _bcg.MasterRoles.AsNoTracking()
                        where includeInactive || _data.Active == 1
                        orderby _data.Id
                        select new
                        {
                            _data.Id,
                            _data.RoleCode,
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
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertRoleModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var code = request.RoleCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อบทบาท (Role Name)" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อบทบาทต้องไม่เกิน 200 ตัวอักษร" });
            }

            var now = DateTime.Now;
            var activeFlag = request.Active == 0 ? 0 : 1;

            if (request.Id == 0)
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return BadRequest(new { detail = "กรุณาระบุรหัสบทบาท (Role Code)" });
                }

                var isDuplicate = await _bcg.MasterRoles
                    .AsNoTracking()
                    .AnyAsync(r => r.RoleCode == code, cancellationToken);
                if (isDuplicate)
                {
                    return Conflict(new { detail = $"รหัสบทบาท '{code}' มีอยู่ในระบบแล้ว" });
                }

                var maxId = await _bcg.MasterRoles
                    .AsNoTracking()
                    .Select(r => (int?)r.Id)
                    .MaxAsync(cancellationToken) ?? 0;

                var entity = new MasterRole
                {
                    Id = maxId + 1,
                    RoleCode = code,
                    Name = name,
                    Active = activeFlag,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterRoles.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลบทบาท (Role) เรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterRoles
                    .SingleOrDefaultAsync(r => r.Id == request.Id, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลบทบาทที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                // ถ้าระบุ RoleCode ใหม่สำหรับ Role ที่ไม่ใช่ Core (Id > 5) จึงยอมให้แก้ RoleCode
                if (existing.Id > 5 && !string.IsNullOrWhiteSpace(code))
                {
                    existing.RoleCode = code;
                }

                existing.Name = name;
                existing.Active = activeFlag;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลบทบาท (Role) เรียบร้อยแล้ว"
                });
            }
        }
    }
}
