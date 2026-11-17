namespace BCG_RPAS_API.Services
{
    public static class DataFactory
    {
        // ==========================================
        // 1. Request Statuses (MasterRequestStatuses)
        // ==========================================
        public const int StatusDraft = 1;
        public const int StatusPendingManager = 2;
        public const int StatusPendingExecutive = 3;
        public const int StatusApproved = 4;
        public const int StatusRejected = 5;
        public const int StatusRevisionRequired = 6;
        public const int StatusCancelled = 7;

        public static class StatusCode
        {
            public const string StatusCodeDraft = "DRAFT";
            public const string StatusCodePendingManager = "PENDING_MANAGER";
            public const string StatusCodePendingExecutive = "PENDING_EXECUTIVE";
            public const string StatusCodeApproved = "APPROVED";
            public const string StatusCodeRejected = "REJECTED";
            public const string StatusCodeRevisionRequired = "REVISION_REQUIRED";
            public const string StatusCodeCancelled = "CANCELLED";
        }

        // ==========================================
        // 2. User Roles (MasterRoles)
        // ==========================================
        public const int RoleRequester = 1;
        public const int RoleManager = 2;
        public const int RoleExecutive = 3;
        public const int RoleSystem = 4;
        public const int RoleAdministrator = 5;

        public static class RoleCode
        {
            public const string RoleCodeRequester = "REQUESTER";
            public const string RoleCodeManager = "MANAGER";
            public const string RoleCodeExecutive = "EXECUTIVE";
            public const string RoleCodeSystem = "SYSTEM";
            public const string RoleCodeAdministrator = "ADMINISTRATOR";
        }

        // ==========================================
        // 3. Approval Step Statuses (ApprovalSteps.Status)
        // ==========================================
        public static class StepStatus
        {
            public const string StepStatusWaiting = "Waiting";
            public const string StepStatusPending = "Pending";
            public const string StepStatusApproved = "Approved";
            public const string StepStatusRejected = "Rejected";
            public const string StepStatusReturned = "Returned";
            public const string StepStatusCancelled = "Cancelled";
            public const string StepStatusSuperseded = "Superseded";
        }

        // ==========================================
        // 4. ERP Sync Statuses (RentalRequests.SyncStatus & ErpJobs.Status)
        // ==========================================
        public static class SyncStatus
        {
            public const string SyncStatusNotQueued = "NotQueued";
            public const string SyncStatusPending = "Pending";
            public const string SyncStatusProcessing = "Processing";
            public const string SyncStatusRetryScheduled = "RetryScheduled";
            public const string SyncStatusSucceeded = "Succeeded";
            public const string SyncStatusFailed = "Failed";
        }

        // ==========================================
        // 5. Audit Log Actions (CK_Logs_Action)
        // ==========================================
        public static class LogAction
        {
            public const string LogActionCreate = "Create";
            public const string LogActionEdit = "Edit";
            public const string LogActionSubmit = "Submit";
            public const string LogActionResubmit = "Resubmit";
            public const string LogActionApprove = "Approve";
            public const string LogActionReject = "Reject";
            public const string LogActionReturn = "Return";
            public const string LogActionCancel = "Cancel";
            public const string LogActionErpQueued = "ErpQueued";
            public const string LogActionErpAttempt = "ErpAttempt";
            public const string LogActionErpSucceeded = "ErpSucceeded";
            public const string LogActionErpFailed = "ErpFailed";
            public const string LogActionErpRetry = "ErpRetry";
        }

        // ==========================================
        // 6. Business Thresholds & Rules
        // ==========================================
        /// <summary>
        /// เกณฑ์วงเงินอนุมัติขั้นต้นของหัวหน้า (ก่อน VAT)
        /// หากยอดเกิน 50,000.00 THB ต้องผ่านการอนุมัติของผู้บริหาร (Executive) ต่อไป
        /// </summary>
        public const decimal ManagerApprovalThreshold = 50000.00m;
    }
}
