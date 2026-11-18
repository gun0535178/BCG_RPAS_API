namespace BCG_RPAS_API.DTOs.Users
{
    public class UpsertUserModel
    {
        public int Id { get; set; }
        public string? UserName { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
        public int RoleId { get; set; }
        public string? Password { get; set; }
        public int Active { get; set; } = 1;
        public int? ExpectedConcurrencyVersion { get; set; }
    }
}
