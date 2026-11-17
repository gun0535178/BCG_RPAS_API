using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.Auth
{
    public class LoginRequestModel
    {
        [Required]
        public string UserName { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;
    }
}
