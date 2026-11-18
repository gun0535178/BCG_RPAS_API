using BCG_RPAS_API.DTOs.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly IConfiguration _configuration;

        public AuthController(BCG_RPASContext bcg, IConfiguration configuration)
        {
            _bcg = bcg;
            _configuration = configuration;
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new
            {
                message = "Auth API พร้อมรับคำขอ"
            });
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public ActionResult<LoginResponseModel> Login([FromBody] LoginRequestModel request)
        {
            try
            {
                string userName = request.UserName;
                string password = request.Password;

                var query =
                    from _data in _bcg.Users.Include(x => x.Role).AsNoTracking()
                    where _data.Active == 1
                        && _data.CanLogin == 1
                        && _data.UserName == userName
                    select _data;

                var user = query.FirstOrDefault();

                if (user == null || string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    return Unauthorized(new
                    {
                        title = "เข้าสู่ระบบไม่สำเร็จ",
                        status = StatusCodes.Status401Unauthorized,
                        detail = "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
                    });
                }

                var passwordHasher = new PasswordHasher<User>();

                var verificationResult = passwordHasher.VerifyHashedPassword(
                    user,
                    user.PasswordHash,
                    password
                );

                if (verificationResult == PasswordVerificationResult.Failed)
                {
                    return Unauthorized(new
                    {
                        title = "เข้าสู่ระบบไม่สำเร็จ",
                        status = StatusCodes.Status401Unauthorized,
                        detail = "ชื่อผู้ใช้หรือรหัสผ่านไม่ถูกต้อง",
                    });
                }

                var now = DateTime.Now;
                var expiresDate = DateTime.Today.AddDays(1).ToUniversalTime();

                var claims = new[]
                {
                    new Claim(
                        JwtRegisteredClaimNames.Sub,
                        user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),

                    new Claim(
                        JwtRegisteredClaimNames.Jti,
                        Guid.NewGuid().ToString("N")),

                    new Claim("name", user.UserName),

                    new Claim("role", user.Role.RoleCode)
                };

                var signingKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!)
                );

                var token = new JwtSecurityToken(
                    issuer: _configuration["Jwt:Issuer"],
                    audience: _configuration["Jwt:Audience"],
                    claims: claims,
                    notBefore: now,
                    expires: expiresDate,
                    signingCredentials: new SigningCredentials(
                        signingKey,
                        SecurityAlgorithms.HmacSha256)
                );

                var response = new LoginResponseModel
                {
                    AccessToken = new JwtSecurityTokenHandler().WriteToken(token),
                    TokenType = "Bearer",
                    ExpiresDate = expiresDate,
                    User = new LoginUserResponse
                    {
                        Id = user.Id,
                        UserName = user.UserName,
                        DisplayName = user.DisplayName ?? string.Empty,
                        RoleCode = user.Role.RoleCode,
                        RoleName = user.Role.Name ?? string.Empty
                    }
                };

                return Ok(response);
            }
            catch (Exception)
            {
                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "ระบบขัดข้อง",
                    detail: "ไม่สามารถเข้าสู่ระบบได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                );
            }
        }
    }
}
