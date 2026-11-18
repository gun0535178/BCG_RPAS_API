using BCG_RPAS_API.DTOs.Master;
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
        private readonly GetCurrentUser _getCurrentUser;

        public MasterRequestStatusesController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var query = from _data in _bcg.MasterRequestStatuses.AsNoTracking()
                        where includeInactive || _data.Active == 1
                        orderby _data.Id
                        select new
                        {
                            _data.Id,
                            _data.StatusCode,
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
            [FromBody] UpsertRequestStatusModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var code = request.StatusCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อสถานะคำขอ (Status Name)" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อสถานะคำขอต้องไม่เกิน 200 ตัวอักษร" });
            }

            var now = DateTime.Now;
            var activeFlag = request.Active == 0 ? 0 : 1;

            if (request.Id == 0)
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    return BadRequest(new { detail = "กรุณาระบุรหัสสถานะ (Status Code)" });
                }

                var isDuplicate = await _bcg.MasterRequestStatuses
                    .AsNoTracking()
                    .AnyAsync(s => s.StatusCode == code, cancellationToken);
                if (isDuplicate)
                {
                    return Conflict(new { detail = $"รหัสสถานะ '{code}' มีอยู่ในระบบแล้ว" });
                }

                var maxId = await _bcg.MasterRequestStatuses
                    .AsNoTracking()
                    .Select(s => (int?)s.Id)
                    .MaxAsync(cancellationToken) ?? 0;

                var entity = new MasterRequestStatus
                {
                    Id = maxId + 1,
                    StatusCode = code,
                    Name = name,
                    Active = activeFlag,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterRequestStatuses.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลสถานะคำขอเรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterRequestStatuses
                    .SingleOrDefaultAsync(s => s.Id == request.Id, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลสถานะคำขอที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                if (existing.Id > 7 && !string.IsNullOrWhiteSpace(code))
                {
                    existing.StatusCode = code;
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
                    message = "บันทึกการแก้ไขข้อมูลสถานะคำขอเรียบร้อยแล้ว"
                });
            }
        }
    }
}
