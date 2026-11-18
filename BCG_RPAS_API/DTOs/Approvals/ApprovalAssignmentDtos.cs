namespace BCG_RPAS_API.DTOs.Approvals
{
    public class UpsertApprovalAssignmentModel
    {
        public int RequesterId { get; set; }
        public int ManagerApproverId { get; set; }
        public int ExecutiveApproverId { get; set; }
    }
}
