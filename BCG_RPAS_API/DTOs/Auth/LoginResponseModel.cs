using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.Auth
{
    public class LoginResponseModel
    {
        public string AccessToken { get; set; } = string.Empty;

        public string TokenType { get; set; } = "Bearer";

        public DateTime ExpiresDate { get; set; }

        public LoginUserResponse User { get; set; }
            = new LoginUserResponse();
    }

    public class LoginUserResponse
    {
        public int Id { get; set; }

        public string UserName { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        public string RoleCode { get; set; } = string.Empty;

        public string RoleName { get; set; } = string.Empty;
    }
}
