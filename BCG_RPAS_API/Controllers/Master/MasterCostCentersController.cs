using BCG_RPAS_API.DTOs.Master;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/cost-center")]
    public class MasterCostCentersController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public MasterCostCentersController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var queryMasterCostCenters = from _data in _bcg.MasterCostCenters.AsNoTracking()
                                         where includeInactive || _data.Active == 1
                                         orderby _data.CostCenterCode
                                         select new
                                         {
                                             _data.Id,
                                             _data.CostCenterCode,
                                             _data.Name,
                                             _data.Active,
                                             _data.ConcurrencyVersion,
                                             _data.ModifyDate,
                                             _data.CreateDate
                                         };

            var result = await queryMasterCostCenters.ToListAsync(cancellationToken);
            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertCostCenterModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var code = request.CostCenterCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { detail = "กรุณาระบุรหัสศูนย์ต้นทุน (Cost Center Code)" });
            }

            if (code.Length > 100)
            {
                return BadRequest(new { detail = "รหัสศูนย์ต้นทุนต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อศูนย์ต้นทุน/แผนก" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อศูนย์ต้นทุนต้องไม่เกิน 200 ตัวอักษร" });
            }

            var isDuplicate = await _bcg.MasterCostCenters
                .AsNoTracking()
                .AnyAsync(c => c.Id != request.Id && c.CostCenterCode == code, cancellationToken);
            if (isDuplicate)
            {
                return Conflict(new { detail = $"รหัสศูนย์ต้นทุน '{code}' มีอยู่ในระบบแล้ว" });
            }

            var now = DateTime.Now;
            var activeFlag = request.Active == 0 ? 0 : 1;

            if (request.Id == 0)
            {
                var entity = new MasterCostCenter
                {
                    CostCenterCode = code,
                    Name = name,
                    Active = activeFlag,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterCostCenters.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลศูนย์ต้นทุน (Cost Center) เรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterCostCenters
                    .SingleOrDefaultAsync(c => c.Id == request.Id, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลศูนย์ต้นทุนที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.CostCenterCode = code;
                existing.Name = name;
                existing.Active = activeFlag;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลศูนย์ต้นทุนเรียบร้อยแล้ว"
                });
            }
        }
    }
}
