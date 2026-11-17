namespace BCG_RPAS_API.Services
{
    public class GetCurrentUser 
    {
        private readonly BCG_RPASContext _bcg;
        private readonly IHttpContextAccessor _httpContextAccessor;
        public GetCurrentUser(
                BCG_RPASContext bcg,
                IHttpContextAccessor httpContextAccessor)
        {
            _bcg = bcg;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<int?> GetCurrentUserId(
              CancellationToken cancellationToken)
        {
            var currentUser = _httpContextAccessor.HttpContext?.User;

            if (currentUser?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var sub = currentUser.FindFirst("sub")?.Value;

            if (!int.TryParse(sub, out var userId) || userId <= 0)
            {
                return null;
            }

            var query =
                from u in _bcg.Users.AsNoTracking()
                where u.Id == userId
                    && u.Active == 1
                    && u.CanLogin == 1
                    && u.Role.Active == 1
                    && u.Role.RoleCode != "SYSTEM"
                select (int?)u.Id;

            var result = await query.SingleOrDefaultAsync(
                cancellationToken);

            return result;
        }
    }
}
