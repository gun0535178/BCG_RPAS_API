using BCG_RPAS_API.DTOs.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Authorize(Roles = "ADMINISTRATOR")]
    [Route("api/users")]
    public class UsersController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public UsersController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
        {
            var usersQuery =
                from _data in _bcg.Users.AsNoTracking()
                where _data.Active == 1
                    && _data.Role.Active == 1
                    && _data.Role.RoleCode != DataFactory.RoleCode.RoleCodeSystem
                orderby _data.Id
                select new
                {
                    _data.Id,
                    _data.UserName,
                    _data.DisplayName,
                    _data.Email,
                    _data.RoleId,
                    RoleCode = _data.Role.RoleCode,
                    RoleName = _data.Role.Name,
                    _data.ConcurrencyVersion,
                    _data.CreateDate,
                    _data.ModifyDate
                };

            var rolesQuery =
                from _data in _bcg.MasterRoles.AsNoTracking()
                where _data.Active == 1
                    && _data.RoleCode != DataFactory.RoleCode.RoleCodeSystem
                orderby _data.Id
                select new
                {
                    _data.Id,
                    _data.RoleCode,
                    _data.Name
                };

            var users = await usersQuery.ToListAsync(cancellationToken);
            var roles = await rolesQuery.ToListAsync(cancellationToken);

            return Ok(new
            {
                users,
                roles
            });
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertUserModel request,
            CancellationToken cancellationToken)
        {
            var adminId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (adminId == null) return Unauthorized();

            var now = DateTime.Now;

            // กรณีสั่งลบแบบ Soft Delete (Active = 0)
            if (request.Id > 0 && request.Active == 0)
            {
                if (request.Id == adminId.Value)
                {
                    return BadRequest(new { detail = "ไม่สามารถลบบัญชีผู้ใช้งานของตนเองที่กำลังเข้าสู่ระบบอยู่ได้" });
                }

                var deleteQuery =
                    from _data in _bcg.Users
                    where _data.Active == 1
                        && _data.Id == request.Id
                    select _data;

                var toDelete = await deleteQuery.SingleOrDefaultAsync(cancellationToken);

                if (toDelete == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลผู้ใช้งานที่ต้องการลบ" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    toDelete.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกเปลี่ยนแปลงโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                toDelete.Active = 0;
                toDelete.CanLogin = 0;
                toDelete.ModifyBy = adminId.Value;
                toDelete.ModifyDate = now;
                toDelete.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = toDelete.Id,
                    message = "ลบผู้ใช้งานออกจากระบบเรียบร้อยแล้ว"
                });
            }

            var userName = request.UserName?.Trim();
            var displayName = request.DisplayName?.Trim();
            var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            var password = request.Password;

            if (string.IsNullOrWhiteSpace(userName))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อบัญชีผู้ใช้" });
            }

            if (userName.Length > 100)
            {
                return BadRequest(new { detail = "ชื่อบัญชีผู้ใช้ต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อ-นามสกุลที่แสดงผล" });
            }

            if (displayName.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อ-นามสกุลต้องไม่เกิน 200 ตัวอักษร" });
            }

            if (email != null && email.Length > 254)
            {
                return BadRequest(new { detail = "อีเมลต้องไม่เกิน 254 ตัวอักษร" });
            }

            // ตรวจสอบบทบาท (Role)
            var roleQuery =
                from _data in _bcg.MasterRoles.AsNoTracking()
                where _data.Active == 1
                    && _data.Id == request.RoleId
                    && _data.RoleCode != DataFactory.RoleCode.RoleCodeSystem
                select _data.Id;

            if (!await roleQuery.AnyAsync(cancellationToken))
            {
                return BadRequest(new { detail = "กรุณาเลือกบทบาทผู้ใช้งานที่ถูกต้อง" });
            }

            // ตรวจสอบชื่อบัญชีผู้ใช้ซ้ำ (รวมทั้งรายการที่เคย Soft Delete Active = 0 เนื่องจาก UQ_Users_UserName)
            var duplicateQuery =
                from _data in _bcg.Users
                where _data.Id != request.Id
                    && _data.UserName == userName
                select _data;

            var duplicateEntity = await duplicateQuery.SingleOrDefaultAsync(cancellationToken);

            var passwordHasher = new PasswordHasher<User>();

            if (duplicateEntity != null)
            {
                if (duplicateEntity.Active == 1)
                {
                    return Conflict(new { detail = $"ชื่อบัญชีผู้ใช้ '{userName}' มีอยู่ในระบบแล้ว" });
                }

                // กรณีสร้างใหม่แต่ชื่อบัญชีตรงกับแถวที่เคยถูก Soft Delete (Active = 0) -> เปิดใช้งานกลับมา (Reactivate)
                if (request.Id == 0)
                {
                    if (string.IsNullOrWhiteSpace(password))
                    {
                        return BadRequest(new { detail = "กรุณากำหนดรหัสผ่านสำหรับผู้ใช้งานใหม่" });
                    }

                    duplicateEntity.DisplayName = displayName;
                    duplicateEntity.Email = email;
                    duplicateEntity.RoleId = request.RoleId;
                    duplicateEntity.PasswordHash = passwordHasher.HashPassword(duplicateEntity, password);
                    duplicateEntity.CanLogin = 1;
                    duplicateEntity.Active = 1;
                    duplicateEntity.ModifyBy = adminId.Value;
                    duplicateEntity.ModifyDate = now;
                    duplicateEntity.ConcurrencyVersion += 1;

                    await _bcg.SaveChangesAsync(cancellationToken);

                    return Ok(new
                    {
                        id = duplicateEntity.Id,
                        message = "สร้างผู้ใช้งานใหม่เรียบร้อยแล้ว"
                    });
                }

                return Conflict(new { detail = $"ชื่อบัญชีผู้ใช้ '{userName}' ซ้ำกับรายการที่มีประวัติในระบบ" });
            }

            if (request.Id == 0)
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    return BadRequest(new { detail = "กรุณากำหนดรหัสผ่านสำหรับผู้ใช้งานใหม่" });
                }

                var entity = new User
                {
                    UserName = userName,
                    DisplayName = displayName,
                    Email = email,
                    RoleId = request.RoleId,
                    CanLogin = 1,
                    Active = 1,
                    ConcurrencyVersion = 1,
                    CreateBy = adminId.Value,
                    CreateDate = now
                };

                entity.PasswordHash = passwordHasher.HashPassword(entity, password);

                _bcg.Users.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "สร้างผู้ใช้งานใหม่เรียบร้อยแล้ว"
                });
            }
            else
            {
                var existingQuery =
                    from _data in _bcg.Users
                    where _data.Active == 1
                        && _data.Id == request.Id
                    select _data;

                var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลผู้ใช้งานที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.UserName = userName;
                existing.DisplayName = displayName;
                existing.Email = email;
                existing.RoleId = request.RoleId;
                existing.CanLogin = 1;
                existing.Active = 1;
                existing.ModifyBy = adminId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                // หากกรอกรหัสผ่านใหม่มาด้วย ให้ทำการอัปเดตรหัสผ่านใหม่
                if (!string.IsNullOrWhiteSpace(password))
                {
                    existing.PasswordHash = passwordHasher.HashPassword(existing, password);
                }

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลผู้ใช้งานเรียบร้อยแล้ว"
                });
            }
        }
    }
}
