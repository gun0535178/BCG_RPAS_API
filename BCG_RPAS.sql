/* BCG_RPAS new-database installer; SQL Server 2016+. NOT runtime-tested.
   Run whole file in SSMS with CREATE DATABASE permission; no SQLCMD required.
   No GO separators. Existing BCG_RPAS is never altered. Failed schema transaction
   can leave newly created empty database; inspect manually, no DROP performed. */
SET NOCOUNT ON;
IF @@TRANCOUNT <> 0
    THROW 51010, N'Run BCG_RPAS installer outside any transaction.', 1;
IF DB_ID(N'BCG_RPAS') IS NOT NULL
    THROW 51011, N'BCG_RPAS already exists; installer stopped without changing it.', 1;
BEGIN TRY
    EXEC(N'CREATE DATABASE [BCG_RPAS];');
    EXEC sys.sp_executesql N'USE [BCG_RPAS];
IF DB_NAME() <> N''BCG_RPAS''
    THROW 51012, N''Wrong database context; installation stopped.'', 1;
/* Rental Payment Approval: SQL Server 2016+; new BCG_RPAS database only.
   STATIC CHECKED ONLY, NOT executed against SQL Server. No migration/backend changes. Requires CREATE DATABASE permission.
   Open this file in SSMS and execute as one batch; no SQLCMD mode required.
   Existing BCG_RPAS: abort without modifying it. If schema creation fails, the NEW
   database may remain empty; inspect manually, no automatic DROP is performed.
   ER: Users -> ApprovalAssignments (requester/approver); MasterVendors -> MasterContracts;
   Users/MasterVendors/MasterContracts/MasterCostCenters -> RentalRequests -> RequestRevisions;
   RequestRevisions -> Attachments/ApprovalSteps/ErpJobs; ErpJobs -> ErpAttempts;
   RentalRequests -> Logs. Every table has CreateBy/ModifyBy -> Users (NO ACTION).
   Assumptions: UTC datetime (GETUTCDATE); Active 0/1 is NOT workflow status.
   RequesterId is owner, CreateBy is authenticated creator; initially self-request only.
   One request = one contract and exact arbitrary date period (not calendar months).
   Complete keys reserve dates for DRAFT/PENDING_MANAGER/PENDING_EXECUTIVE/REVISION_REQUIRED/APPROVED,
   regardless of Active. REJECTED/CANCELLED release reservation, allowing a NEW request.
   Exact duplicates only: overlapping but nonidentical periods remain a documented risk.
   Before-VAT THB Amount <= 50000: manager; >50000: manager then executive.
   Draft fields nullable, supplied values valid; VAT nonnegative with no fixed rate.
   Sole non-login system identity; demo master seeds only, no human accounts.
   Replace demo masters with verified business data; provision users/approver mappings separately.
   One primary role per Users.RoleId -> MasterRoles; no UserRoles/multi-role model.
   No delegation or self approval. Application must check active requester/approver roles,
   match assignment ApprovalRoleId to active approver RoleId, and require an active assignment;
   possession of a manager/executive role alone NEVER authorizes approval.
   Stable role IDs 2/3 constrain assignment and step routing; SYSTEM (4) cannot log in.
   RentalRequests derive rental type through MasterContracts.RentalTypeId; on submission
   snapshot rental type id/code/name with contract/vendor/cost center into SnapshotJson.
   Master edits must never change earlier immutable revisions or ERP payloads.
   APPLICATION TRANSACTION/PERMISSION obligations (NOT enforced by SQL CHECK/FK):
   - Self-request initially; owner-only edit DRAFT/REVISION_REQUIRED; submit validates all fields,
     positive amount, dates, active masters, >=1 attachment, active mapped approvers.
   - REVISION_REQUIRED (formerly Returned) edits working copy; each submit/resubmit appends new immutable revision,
     numbered from 1; snapshot masters, money, filenames, approvers and routing together.
     Earlier revision/attachment snapshots never change; old steps retain action history.
     Executive return restarts manager; never re-use a submitted revision.
   - APPROVED/REJECTED/CANCELLED terminal; reject/return reasons required; cancellation
     owner-only before approval with reason. Check transition graph and optimistic ConcurrencyVersion.
   - ConcurrencyVersion is application-managed int: EVERY update must atomically SET
     ConcurrencyVersion = ConcurrencyVersion + 1 WHERE Id = @Id AND
     ConcurrencyVersion = @ExpectedVersion; require exactly one affected row or reject conflict.
     Never reset/reuse versions; handle int exhaustion before incrementing.
   - IdempotencyKey defaults to a Unicode GUID string; nullable LockToken has no default.
     Application assigns CONVERT(nvarchar(36), NEWID()) only when atomically claiming a lease.
   - PeriodStartDate/PeriodEndDate are datetime: normalize date-only periods to UTC midnight
     consistently to preserve exact-period reservation semantics.
   - VatAmount/TotalAmount explicitly CONVERT to decimal(19,2) for computed metadata.
   - SnapshotJson uses documented application schema containing master codes/names (including rental type), period,
     amount/VAT/total, attachment filenames, approver ids/names/roles and notes. Relational
     snapshot fields/FKs must match JSON; master display changes never rewrite snapshots.
   - RequestRevisions.SnapshotJson captures submitted StatusId/StatusCode/Name (normally
     PENDING_MANAGER), never a live join; later workflow events do not rewrite revisions.
     Logs.FromStatusId/ToStatusId reference stable request states at the event; Details may
     snapshot their code/name for historical display. Never replace these with live StatusId.
     ApprovalSteps.Status is a separate step lifecycle (including Waiting/Superseded);
     SyncStatus and ERP statuses are separate integration lifecycles, not request statuses.
   - MasterRequestStatuses is reference data, NOT a transition engine. Application enforces
     DRAFT -> PENDING_MANAGER; manager -> APPROVED (Amount <= 50000) or PENDING_EXECUTIVE
     (Amount > 50000); executive -> APPROVED; pending reviewers may REJECT or return to
     REVISION_REQUIRED. Resubmission always restarts PENDING_MANAGER with a new revision.
     Owner cancellation is pre-approval only; terminal states cannot reopen. Stable status
     IDs/codes must never be renumbered/repurposed; application checks active reference data.
   - Audit append-only via service permissions; no physical deletes or identity from body.
   - Atomically approve + create one durable ERP job for approved revision + audit;
     SyncStatus mirrors job state without undoing approval. Immutable payload/idempotency key.
     Claim lease atomically; bounded backoff, durable attempt counters, reconcile expired
     leases and unknown outcomes, no DB transaction across HTTP. Job cannot be NotQueued.
   - Immutable revisions/payload/audit and terminal-state protection require application
     permissions/transactions; this schema does not create logins or authorization policies.
   - All subsequent timestamps UTC; ModifyBy/ModifyDate paired; no plaintext passwords stored.
   LOGIN SCHEMA ONLY: no working login UI/API or backend is implemented by this script.
   - PasswordHash stores only an encoded password hash; never store plaintext passwords.
   - Future backend must generate and verify hashes with ASP.NET Core PasswordHasher
     (including VerifyHashedPassword/rehash handling), or an equivalent vetted password hasher.
   - Future backend must require Active = 1 AND CanLogin = 1, verify the password hash,
     and implement JWT/session authentication plus server-side authorization separately.
   - Never return PasswordHash in API responses or write passwords/hashes to Logs.
   - System identity has CanLogin = 0 and no hash; no human accounts or secrets are seeded.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

-- Refuse to add application objects to an already populated user database.
IF EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0
           AND type IN (N''U'',N''V'',N''P'',N''FN'',N''IF'',N''TF'',N''SO'',N''SN''))
    THROW 51000, N''Select an empty application database; no existing objects will be replaced.'', 1;
IF DB_NAME() IN (N''master'', N''model'', N''msdb'', N''tempdb'')
    THROW 51001, N''Do not run this script in a system database.'', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    CREATE TABLE dbo.MasterRoles (
        Id int NOT NULL CONSTRAINT PK_MasterRoles PRIMARY KEY,
        RoleCode nvarchar(100) NOT NULL,
        Name nvarchar(200) NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterRoles_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterRoles_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterRoles_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterRoles_Code UNIQUE (RoleCode),
        CONSTRAINT CK_MasterRoles_Code CHECK (LEN(LTRIM(RTRIM(RoleCode))) > 0),
        CONSTRAINT CK_MasterRoles_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MasterRoles_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterRoles_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        -- Stable IDs support local CHECKs; do not renumber or repurpose role codes.
        CONSTRAINT CK_MasterRoles_IdCode CHECK ((Id = 1 AND RoleCode = N''REQUESTER'') OR (Id = 2 AND RoleCode = N''MANAGER'') OR (Id = 3 AND RoleCode = N''EXECUTIVE'') OR (Id = 4 AND RoleCode = N''SYSTEM'') OR (Id = 5 AND RoleCode = N''ADMINISTRATOR''))
    );

    CREATE TABLE dbo.Users (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        UserName nvarchar(100) NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        Email nvarchar(254) NULL,
        RoleId int NOT NULL,
        CanLogin int NOT NULL CONSTRAINT DF_Users_CanLogin DEFAULT (0),
        PasswordHash nvarchar(512) NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_Users_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_Users_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_Users_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_Users_UserName UNIQUE (UserName),
        CONSTRAINT CK_Users_UserName CHECK (LEN(LTRIM(RTRIM(UserName))) > 0),
        CONSTRAINT CK_Users_DisplayName CHECK (LEN(LTRIM(RTRIM(DisplayName))) > 0),
        CONSTRAINT CK_Users_CanLogin CHECK (CanLogin IN (0,1)),
        CONSTRAINT CK_Users_LoginPasswordHash CHECK (CanLogin = 0 OR (PasswordHash IS NOT NULL AND LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(PasswordHash, NCHAR(9), N''''), NCHAR(10), N''''), NCHAR(13), N'''')))) > 0)),
        CONSTRAINT CK_Users_SystemLogin CHECK (RoleId <> 4 OR CanLogin = 0),
        CONSTRAINT CK_Users_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_Users_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_Users_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Users_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    -- Bootstrap inside the schema transaction: roles first without audit FKs,
    -- then the sole non-login system identity (self audit FK), then trusted cycle FKs.
    -- ADMINISTRATOR preserves the pre-existing role scope; it grants no approval assignment.
    INSERT dbo.MasterRoles (Id, RoleCode, Name, CreateBy)
    VALUES (1, N''REQUESTER'', N''ผู้ขออนุมัติ'', 1),
           (2, N''MANAGER'', N''ผู้จัดการ'', 1),
           (3, N''EXECUTIVE'', N''ผู้บริหาร'', 1),
           (4, N''SYSTEM'', N''ระบบอัตโนมัติ'', 1),
           (5, N''ADMINISTRATOR'', N''ผู้ดูแลระบบ'', 1);
    SET IDENTITY_INSERT dbo.Users ON;
    INSERT dbo.Users (Id, UserName, DisplayName, RoleId, CanLogin, Active, CreateBy, CreateDate)
    VALUES (1, N''system'', N''System worker'', 4, 0, 1, 1, GETUTCDATE());
    SET IDENTITY_INSERT dbo.Users OFF;
    ALTER TABLE dbo.Users WITH CHECK ADD CONSTRAINT FK_Users_Role
      FOREIGN KEY (RoleId) REFERENCES dbo.MasterRoles(Id) ON DELETE NO ACTION ON UPDATE NO ACTION;
    ALTER TABLE dbo.MasterRoles WITH CHECK ADD CONSTRAINT FK_MasterRoles_CreateBy
      FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION;
    ALTER TABLE dbo.MasterRoles WITH CHECK ADD CONSTRAINT FK_MasterRoles_ModifyBy
      FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION;

    -- Fixed request reference data: stable IDs/codes underpin CHECKs and reservation index.
    -- This table/FKs validate state membership only, NOT allowed state-machine transitions.
    -- Application transactions must enforce the transition graph, permissions and reasons.
    CREATE TABLE dbo.MasterRequestStatuses (
        Id int NOT NULL CONSTRAINT PK_MasterRequestStatuses PRIMARY KEY,
        StatusCode nvarchar(100) NOT NULL,
        Name nvarchar(200) NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterRequestStatuses_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterRequestStatuses_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterRequestStatuses_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterRequestStatuses_Code UNIQUE (StatusCode),
        CONSTRAINT CK_MasterRequestStatuses_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MasterRequestStatuses_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterRequestStatuses_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT CK_MasterRequestStatuses_IdCode CHECK ((Id = 1 AND StatusCode = N''DRAFT'') OR (Id = 2 AND StatusCode = N''PENDING_MANAGER'') OR (Id = 3 AND StatusCode = N''PENDING_EXECUTIVE'') OR (Id = 4 AND StatusCode = N''APPROVED'') OR (Id = 5 AND StatusCode = N''REJECTED'') OR (Id = 6 AND StatusCode = N''REVISION_REQUIRED'') OR (Id = 7 AND StatusCode = N''CANCELLED'')),
        CONSTRAINT FK_MasterRequestStatuses_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterRequestStatuses_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    -- Users(1) already exists; reference rows are seeded before dependent request tables.
    INSERT dbo.MasterRequestStatuses (Id, StatusCode, Name, CreateBy)
    VALUES (1, N''DRAFT'', N''ฉบับร่าง'', 1),
           (2, N''PENDING_MANAGER'', N''รอหัวหน้าอนุมัติ'', 1),
           (3, N''PENDING_EXECUTIVE'', N''รอผู้บริหารอนุมัติ'', 1),
           (4, N''APPROVED'', N''อนุมัติครบถ้วน/เตรียมส่งERP'', 1),
           (5, N''REJECTED'', N''ไม่อนุมัติ'', 1),
           (6, N''REVISION_REQUIRED'', N''ส่งกลับแก้ไข'', 1),
           (7, N''CANCELLED'', N''ยกเลิกคำขอ'', 1);

    CREATE TABLE dbo.MasterRentalTypes (
        Id int NOT NULL CONSTRAINT PK_MasterRentalTypes PRIMARY KEY,
        RentalTypeCode nvarchar(100) NOT NULL,
        Name nvarchar(200) NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterRentalTypes_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterRentalTypes_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterRentalTypes_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterRentalTypes_Code UNIQUE (RentalTypeCode),
        CONSTRAINT CK_MasterRentalTypes_Code CHECK (LEN(LTRIM(RTRIM(RentalTypeCode))) > 0),
        CONSTRAINT CK_MasterRentalTypes_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MasterRentalTypes_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterRentalTypes_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_MasterRentalTypes_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterRentalTypes_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    -- EXAMPLE CATEGORIES ONLY: confirm/maintain these rental types before production use.
    INSERT dbo.MasterRentalTypes (Id, RentalTypeCode, Name, CreateBy)
    VALUES (1, N''OFFICE'', N''สำนักงาน'', 1),
           (2, N''WAREHOUSE'', N''คลังสินค้า'', 1),
           (3, N''EQUIPMENT'', N''อุปกรณ์และเครื่องจักร'', 1);

    CREATE TABLE dbo.ApprovalAssignments (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApprovalAssignments PRIMARY KEY,
        RequesterId int NOT NULL,
        ApproverId int NOT NULL,
        ApprovalRoleId int NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_ApprovalAssignments_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_ApprovalAssignments_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_ApprovalAssignments_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT CK_ApprovalAssignments_Role CHECK (ApprovalRoleId IN (2,3)),
        CONSTRAINT CK_ApprovalAssignments_NoSelf CHECK (RequesterId <> ApproverId),
        CONSTRAINT CK_ApprovalAssignments_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_ApprovalAssignments_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_ApprovalAssignments_Requester FOREIGN KEY (RequesterId) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalAssignments_Role FOREIGN KEY (ApprovalRoleId) REFERENCES dbo.MasterRoles(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalAssignments_Approver FOREIGN KEY (ApproverId) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalAssignments_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalAssignments_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.MasterVendors (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MasterVendors PRIMARY KEY,
        VendorCode nvarchar(100) NOT NULL,
        Name nvarchar(200) NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterVendors_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterVendors_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterVendors_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterVendors_Code UNIQUE (VendorCode),
        CONSTRAINT CK_MasterVendors_VendorCode CHECK (LEN(LTRIM(RTRIM(VendorCode))) > 0),
        CONSTRAINT CK_MasterVendors_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MasterVendors_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterVendors_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_MasterVendors_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterVendors_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.MasterContracts (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MasterContracts PRIMARY KEY,
        ContractNumber nvarchar(100) NOT NULL,
        VendorId int NOT NULL,
        RentalTypeId int NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterContracts_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterContracts_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterContracts_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterContracts_Number UNIQUE (ContractNumber),
        CONSTRAINT UQ_MasterContracts_IdVendor UNIQUE (Id, VendorId),
        CONSTRAINT CK_MasterContracts_ContractNumber CHECK (LEN(LTRIM(RTRIM(ContractNumber))) > 0),
        CONSTRAINT FK_MasterContracts_RentalType FOREIGN KEY (RentalTypeId) REFERENCES dbo.MasterRentalTypes(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterContracts_Vendor FOREIGN KEY (VendorId) REFERENCES dbo.MasterVendors(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_MasterContracts_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterContracts_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_MasterContracts_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterContracts_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.MasterCostCenters (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MasterCostCenters PRIMARY KEY,
        CostCenterCode nvarchar(100) NOT NULL,
        Name nvarchar(200) NOT NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_MasterCostCenters_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_MasterCostCenters_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_MasterCostCenters_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_MasterCostCenters_Code UNIQUE (CostCenterCode),
        CONSTRAINT CK_MasterCostCenters_CostCenterCode CHECK (LEN(LTRIM(RTRIM(CostCenterCode))) > 0),
        CONSTRAINT CK_MasterCostCenters_Name CHECK (LEN(LTRIM(RTRIM(Name))) > 0),
        CONSTRAINT CK_MasterCostCenters_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_MasterCostCenters_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_MasterCostCenters_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_MasterCostCenters_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    -- Initial department master data; codes remain independent of contracts.
    INSERT dbo.MasterCostCenters
        (CostCenterCode, Name, Active, CreateBy, CreateDate, ModifyBy, ModifyDate)
    VALUES (N''IT-001'', N''ฝ่ายเทคโนโลยีสารสนเทศ'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''ACC-002'', N''ฝ่ายบัญชี'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''SALES-003'', N''ฝ่ายขาย'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''HR-004'', N''ฝ่ายทรัพยากรบุคคล'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''MKT-005'', N''ฝ่ายการตลาด'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''FIN-006'', N''ฝ่ายการเงิน'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''PUR-007'', N''ฝ่ายจัดซื้อ'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''WH-008'', N''ฝ่ายคลังสินค้า'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''LOG-009'', N''ฝ่ายโลจิสติกส์'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''PROD-010'', N''ฝ่ายผลิต'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''QC-011'', N''ฝ่ายควบคุมคุณภาพ'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''ADMIN-012'', N''ฝ่ายบริหารทั่วไป'', 1, 1, GETUTCDATE(), NULL, NULL);

    -- MasterVendors is maintainable shared master data, NOT a fixed vendor list.
    -- Add future vendors with normal INSERT (omit identity Id); update business details
    -- with paired ModifyBy/ModifyDate and increment ConcurrencyVersion as documented.
    -- These three fictional demo companies must be reviewed/replaced before production.
    INSERT dbo.MasterVendors
        (VendorCode, Name, Active, CreateBy, CreateDate, ModifyBy, ModifyDate)
    VALUES (N''V001'', N''บริษัท พื้นที่ดี จำกัด'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''V002'', N''บริษัท คลังพร้อม จำกัด'', 1, 1, GETUTCDATE(), NULL, NULL),
           (N''V003'', N''บริษัท อุปกรณ์พร้อมใช้ จำกัด'', 1, 1, GETUTCDATE(), NULL, NULL);

    -- Demo contracts: resolve actual identity IDs by unique vendor/type codes.
    -- Intended demo coverage: 2026-01-01 through 2027-12-31 (UTC dates).
    -- MasterContracts has no start/end date columns: coverage is documentation only,
    -- not stored/enforced; no schema extension or cost-center association is introduced.
    INSERT dbo.MasterContracts
        (ContractNumber, VendorId, RentalTypeId, Active, CreateBy, CreateDate, ModifyBy, ModifyDate)
    VALUES (N''RENT-2026-001'',
            (SELECT Id FROM dbo.MasterVendors WHERE VendorCode = N''V001''),
            (SELECT Id FROM dbo.MasterRentalTypes WHERE RentalTypeCode = N''OFFICE''),
            1, 1, GETUTCDATE(), NULL, NULL),
           (N''RENT-2026-002'',
            (SELECT Id FROM dbo.MasterVendors WHERE VendorCode = N''V002''),
            (SELECT Id FROM dbo.MasterRentalTypes WHERE RentalTypeCode = N''WAREHOUSE''),
            1, 1, GETUTCDATE(), NULL, NULL),
           (N''RENT-2026-003'',
            (SELECT Id FROM dbo.MasterVendors WHERE VendorCode = N''V003''),
            (SELECT Id FROM dbo.MasterRentalTypes WHERE RentalTypeCode = N''EQUIPMENT''),
            1, 1, GETUTCDATE(), NULL, NULL);

    CREATE TABLE dbo.RentalRequests (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RentalRequests PRIMARY KEY,
        RequesterId int NOT NULL,
        ContractId int NULL,
        VendorId int NULL,
        CostCenterId int NULL,
        PeriodStartDate datetime NULL,
        PeriodEndDate datetime NULL,
        Amount decimal(19,2) NULL,
        VatRate decimal(5,2) NULL,
        VatAmount AS (CONVERT(decimal(19,2), ROUND(Amount * VatRate / 100.0, 2))) PERSISTED,
        TotalAmount AS (CONVERT(decimal(19,2), Amount + ROUND(Amount * VatRate / 100.0, 2))) PERSISTED,
        CancellationReason nvarchar(2000) NULL,
        Notes nvarchar(2000) NULL,
        StatusId int NOT NULL CONSTRAINT DF_RentalRequests_StatusId DEFAULT (1),
        SyncStatus nvarchar(20) NOT NULL CONSTRAINT DF_RentalRequests_SyncStatus DEFAULT (N''NotQueued''),
        CurrentRevision int NULL,
        SubmittedDate datetime NULL,
        ApprovedDate datetime NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_RentalRequests_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_RentalRequests_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_RentalRequests_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT CK_RentalRequests_Amount CHECK (Amount IS NULL OR Amount > 0),
        CONSTRAINT CK_RentalRequests_VatRate CHECK (VatRate IS NULL OR VatRate >= 0),
        CONSTRAINT CK_RentalRequests_Period CHECK (PeriodStartDate IS NULL OR PeriodEndDate IS NULL OR PeriodEndDate >= PeriodStartDate),
        CONSTRAINT FK_RentalRequests_Status FOREIGN KEY (StatusId) REFERENCES dbo.MasterRequestStatuses(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_RentalRequests_SyncStatus CHECK (SyncStatus IN (N''NotQueued'',N''Pending'',N''Processing'',N''RetryScheduled'',N''Succeeded'',N''Failed'')),
        CONSTRAINT CK_RentalRequests_Revision CHECK (CurrentRevision IS NULL OR CurrentRevision >= 1),
        CONSTRAINT CK_RentalRequests_SubmittedRevision CHECK (StatusId IN (1,7) OR (CurrentRevision IS NOT NULL AND SubmittedDate IS NOT NULL)),
        CONSTRAINT CK_RentalRequests_ApprovedTime CHECK ((StatusId = 4 AND ApprovedDate IS NOT NULL) OR (StatusId <> 4 AND ApprovedDate IS NULL)),
        CONSTRAINT CK_RentalRequests_SyncApproval CHECK (SyncStatus = N''NotQueued'' OR StatusId = 4),
        CONSTRAINT CK_RentalRequests_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_RentalRequests_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_RentalRequests_Requester FOREIGN KEY (RequesterId) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RentalRequests_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RentalRequests_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_RentalRequests_ContractVendorProvided CHECK (ContractId IS NULL OR VendorId IS NOT NULL),
        CONSTRAINT FK_RentalRequests_ContractVendor FOREIGN KEY (ContractId, VendorId) REFERENCES dbo.MasterContracts(Id, VendorId) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RentalRequests_Vendor FOREIGN KEY (VendorId) REFERENCES dbo.MasterVendors(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RentalRequests_CostCenter FOREIGN KEY (CostCenterId) REFERENCES dbo.MasterCostCenters(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_RentalRequests_CancellationReason CHECK (StatusId <> 7 OR (CancellationReason IS NOT NULL AND LEN(LTRIM(RTRIM(CancellationReason))) > 0))
    );

    CREATE TABLE dbo.RequestRevisions (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RequestRevisions PRIMARY KEY,
        RequestId int NOT NULL,
        RevisionNumber int NOT NULL,
        ContractId int NOT NULL,
        VendorId int NOT NULL,
        CostCenterId int NOT NULL,
        PeriodStartDate datetime NOT NULL,
        PeriodEndDate datetime NOT NULL,
        Amount decimal(19,2) NOT NULL,
        VatRate decimal(5,2) NOT NULL,
        VatAmount AS (CONVERT(decimal(19,2), ROUND(Amount * VatRate / 100.0, 2))) PERSISTED,
        TotalAmount AS (CONVERT(decimal(19,2), Amount + ROUND(Amount * VatRate / 100.0, 2))) PERSISTED,
        SnapshotJson nvarchar(max) NOT NULL,
        SubmittedDate datetime NOT NULL CONSTRAINT DF_RequestRevisions_SubmittedDate DEFAULT (GETUTCDATE()),
        Active int NOT NULL CONSTRAINT DF_RequestRevisions_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_RequestRevisions_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_RequestRevisions_Number UNIQUE (RequestId, RevisionNumber),
        CONSTRAINT UQ_RequestRevisions_IdRequest UNIQUE (Id, RequestId),
        CONSTRAINT CK_RequestRevisions_Number CHECK (RevisionNumber >= 1),
        CONSTRAINT CK_RequestRevisions_Money CHECK (Amount > 0 AND VatRate >= 0),
        CONSTRAINT CK_RequestRevisions_Period CHECK (PeriodEndDate >= PeriodStartDate),
        CONSTRAINT CK_RequestRevisions_Json CHECK (ISJSON(SnapshotJson) = 1),
        CONSTRAINT FK_RequestRevisions_Request FOREIGN KEY (RequestId) REFERENCES dbo.RentalRequests(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RequestRevisions_ContractVendor FOREIGN KEY (ContractId, VendorId) REFERENCES dbo.MasterContracts(Id, VendorId) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RequestRevisions_Vendor FOREIGN KEY (VendorId) REFERENCES dbo.MasterVendors(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RequestRevisions_CostCenter FOREIGN KEY (CostCenterId) REFERENCES dbo.MasterCostCenters(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_RequestRevisions_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_RequestRevisions_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_RequestRevisions_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_RequestRevisions_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.Attachments (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
        RequestId int NOT NULL,
        RequestRevisionId int NULL, -- NULL = editable draft attachment; non-NULL = immutable snapshot
        FileName nvarchar(255) NOT NULL,
        Active int NOT NULL CONSTRAINT DF_Attachments_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_Attachments_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT CK_Attachments_FileName CHECK (LEN(LTRIM(RTRIM(FileName))) > 0),
        CONSTRAINT CK_Attachments_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_Attachments_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_Attachments_Request FOREIGN KEY (RequestId) REFERENCES dbo.RentalRequests(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Attachments_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Attachments_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Attachments_RevisionRequest FOREIGN KEY (RequestRevisionId, RequestId) REFERENCES dbo.RequestRevisions(Id, RequestId) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.ApprovalSteps (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApprovalSteps PRIMARY KEY,
        RequestId int NOT NULL,
        RequestRevisionId int NOT NULL,
        StepNumber int NOT NULL,
        ApprovalRoleId int NOT NULL,
        ApproverId int NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ApprovalSteps_Status DEFAULT (N''Waiting''),
        ActionDate datetime NULL,
        Reason nvarchar(2000) NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_ApprovalSteps_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_ApprovalSteps_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_ApprovalSteps_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_ApprovalSteps_RequestRevisionStep UNIQUE (RequestRevisionId, StepNumber),
        CONSTRAINT CK_ApprovalSteps_RoleStep CHECK ((StepNumber = 1 AND ApprovalRoleId = 2) OR (StepNumber = 2 AND ApprovalRoleId = 3)),
        CONSTRAINT CK_ApprovalSteps_Status CHECK (Status IN (N''Waiting'',N''Pending'',N''Approved'',N''Rejected'',N''Returned'',N''Cancelled'',N''Superseded'')),
        CONSTRAINT CK_ApprovalSteps_Reason CHECK (Status NOT IN (N''Rejected'',N''Returned'') OR (Reason IS NOT NULL AND LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(Reason, CHAR(9), N''''), CHAR(10), N''''), CHAR(13), N'''')))) > 0)),
        CONSTRAINT CK_ApprovalSteps_ActionTime CHECK ((Status IN (N''Waiting'',N''Pending'') AND ActionDate IS NULL) OR (Status NOT IN (N''Waiting'',N''Pending'') AND ActionDate IS NOT NULL)),
        CONSTRAINT CK_ApprovalSteps_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_ApprovalSteps_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_ApprovalSteps_Request FOREIGN KEY (RequestId) REFERENCES dbo.RentalRequests(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalSteps_Role FOREIGN KEY (ApprovalRoleId) REFERENCES dbo.MasterRoles(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalSteps_Approver FOREIGN KEY (ApproverId) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalSteps_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalSteps_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ApprovalSteps_RevisionRequest FOREIGN KEY (RequestRevisionId, RequestId) REFERENCES dbo.RequestRevisions(Id, RequestId) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.Logs (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Logs PRIMARY KEY,
        RequestId int NOT NULL,
        ActorId int NOT NULL,
        Action nvarchar(30) NOT NULL,
        FromStatusId int NULL, -- NULL only when no prior request state; immutable audit snapshot
        ToStatusId int NOT NULL, -- request state at this event, not the current live state
        RequestRevisionId int NULL,
        ActionDate datetime NOT NULL CONSTRAINT DF_Logs_ActionDate DEFAULT (GETUTCDATE()),
        Reason nvarchar(2000) NULL,
        Details nvarchar(max) NULL,
        TraceId nvarchar(128) NOT NULL,
        Active int NOT NULL CONSTRAINT DF_Logs_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_Logs_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT CK_Logs_Action CHECK (Action IN (N''Create'',N''Edit'',N''Submit'',N''Resubmit'',N''Approve'',N''Reject'',N''Return'',N''Cancel'',N''ErpQueued'',N''ErpAttempt'',N''ErpSucceeded'',N''ErpFailed'',N''ErpRetry'')),
        CONSTRAINT FK_Logs_FromStatus FOREIGN KEY (FromStatusId) REFERENCES dbo.MasterRequestStatuses(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Logs_ToStatus FOREIGN KEY (ToStatusId) REFERENCES dbo.MasterRequestStatuses(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT CK_Logs_Reason CHECK (Action NOT IN (N''Reject'',N''Return'',N''Cancel'') OR (Reason IS NOT NULL AND LEN(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(Reason, CHAR(9), N''''), CHAR(10), N''''), CHAR(13), N'''')))) > 0)),
        CONSTRAINT CK_Logs_Trace CHECK (LEN(LTRIM(RTRIM(TraceId))) > 0),
        CONSTRAINT CK_Logs_ActorCreator CHECK (ActorId = CreateBy),
        CONSTRAINT CK_Logs_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_Logs_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_Logs_Request FOREIGN KEY (RequestId) REFERENCES dbo.RentalRequests(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Logs_Actor FOREIGN KEY (ActorId) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Logs_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Logs_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_Logs_RevisionRequest FOREIGN KEY (RequestRevisionId, RequestId) REFERENCES dbo.RequestRevisions(Id, RequestId) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.ErpJobs (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ErpJobs PRIMARY KEY,
        RequestId int NOT NULL,
        RequestRevisionId int NOT NULL,
        PayloadSnapshot nvarchar(max) NOT NULL,
        IdempotencyKey nvarchar(36) NOT NULL CONSTRAINT DF_ErpJobs_IdempotencyKey DEFAULT (CONVERT(nvarchar(36), NEWID())),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ErpJobs_Status DEFAULT (N''Pending''),
        AttemptCount int NOT NULL CONSTRAINT DF_ErpJobs_AttemptCount DEFAULT (0),
        MaxAttempts int NOT NULL CONSTRAINT DF_ErpJobs_MaxAttempts DEFAULT (5),
        NextAttemptDate datetime NULL CONSTRAINT DF_ErpJobs_NextAttemptDate DEFAULT (GETUTCDATE()),
        LockedUntilDate datetime NULL,
        LockToken nvarchar(36) NULL,
        LastError nvarchar(4000) NULL,
        ExternalReference nvarchar(200) NULL,
        CompletedDate datetime NULL,
        ConcurrencyVersion int NOT NULL CONSTRAINT DF_ErpJobs_ConcurrencyVersion DEFAULT (1),
        Active int NOT NULL CONSTRAINT DF_ErpJobs_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_ErpJobs_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_ErpJobs_Request UNIQUE (RequestId),
        CONSTRAINT UQ_ErpJobs_IdempotencyKey UNIQUE (IdempotencyKey),
        CONSTRAINT CK_ErpJobs_Payload CHECK (ISJSON(PayloadSnapshot) = 1),
        CONSTRAINT CK_ErpJobs_Status CHECK (Status IN (N''Pending'',N''Processing'',N''RetryScheduled'',N''Succeeded'',N''Failed'')),
        CONSTRAINT CK_ErpJobs_Attempts CHECK (MaxAttempts BETWEEN 1 AND 100 AND AttemptCount BETWEEN 0 AND MaxAttempts),
        CONSTRAINT CK_ErpJobs_RetryBudget CHECK (Status NOT IN (N''Pending'',N''RetryScheduled'') OR AttemptCount < MaxAttempts),
        CONSTRAINT CK_ErpJobs_Lease CHECK ((Status = N''Processing'' AND LockedUntilDate IS NOT NULL AND LockToken IS NOT NULL) OR (Status <> N''Processing'' AND LockedUntilDate IS NULL AND LockToken IS NULL)),
        CONSTRAINT CK_ErpJobs_Schedule CHECK ((Status IN (N''Pending'',N''RetryScheduled'') AND NextAttemptDate IS NOT NULL) OR (Status NOT IN (N''Pending'',N''RetryScheduled'') AND NextAttemptDate IS NULL)),
        CONSTRAINT CK_ErpJobs_Completion CHECK ((Status IN (N''Succeeded'',N''Failed'') AND CompletedDate IS NOT NULL) OR (Status NOT IN (N''Succeeded'',N''Failed'') AND CompletedDate IS NULL)),
        CONSTRAINT CK_ErpJobs_Reference CHECK (Status <> N''Succeeded'' OR (ExternalReference IS NOT NULL AND LEN(LTRIM(RTRIM(ExternalReference))) > 0)),
        CONSTRAINT CK_ErpJobs_Failure CHECK (Status <> N''Failed'' OR (LastError IS NOT NULL AND LEN(LTRIM(RTRIM(LastError))) > 0)),
        CONSTRAINT CK_ErpJobs_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_ErpJobs_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_ErpJobs_Request FOREIGN KEY (RequestId) REFERENCES dbo.RentalRequests(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ErpJobs_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ErpJobs_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ErpJobs_RevisionRequest FOREIGN KEY (RequestRevisionId, RequestId) REFERENCES dbo.RequestRevisions(Id, RequestId) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    CREATE TABLE dbo.ErpAttempts (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ErpAttempts PRIMARY KEY,
        JobId int NOT NULL,
        AttemptNumber int NOT NULL,
        StartedDate datetime NOT NULL CONSTRAINT DF_ErpAttempts_StartedDate DEFAULT (GETUTCDATE()),
        FinishedDate datetime NULL,
        Outcome nvarchar(20) NOT NULL CONSTRAINT DF_ErpAttempts_Outcome DEFAULT (N''InProgress''),
        HttpStatus int NULL,
        Error nvarchar(4000) NULL,
        ResponseSummary nvarchar(4000) NULL,
        TraceId nvarchar(128) NOT NULL,
        Active int NOT NULL CONSTRAINT DF_ErpAttempts_Active DEFAULT (1),
        ModifyBy int NULL,
        ModifyDate datetime NULL,
        CreateBy int NOT NULL,
        CreateDate datetime NOT NULL CONSTRAINT DF_ErpAttempts_CreateDate DEFAULT (GETUTCDATE()),
        CONSTRAINT UQ_ErpAttempts_JobAttempt UNIQUE (JobId, AttemptNumber),
        CONSTRAINT CK_ErpAttempts_Number CHECK (AttemptNumber >= 1),
        CONSTRAINT CK_ErpAttempts_HttpStatus CHECK (HttpStatus IS NULL OR HttpStatus BETWEEN 100 AND 599),
        CONSTRAINT CK_ErpAttempts_Outcome CHECK (Outcome IN (N''InProgress'',N''Succeeded'',N''TransientFailure'',N''PermanentFailure'',N''Unknown'')),
        CONSTRAINT CK_ErpAttempts_Finished CHECK ((Outcome = N''InProgress'' AND FinishedDate IS NULL) OR (Outcome <> N''InProgress'' AND FinishedDate IS NOT NULL AND FinishedDate >= StartedDate)),
        CONSTRAINT CK_ErpAttempts_Success CHECK (Outcome <> N''Succeeded'' OR (HttpStatus IS NOT NULL AND HttpStatus BETWEEN 200 AND 299)),
        CONSTRAINT CK_ErpAttempts_Error CHECK (Outcome NOT IN (N''TransientFailure'',N''PermanentFailure'',N''Unknown'') OR (Error IS NOT NULL AND LEN(LTRIM(RTRIM(Error))) > 0)),
        CONSTRAINT CK_ErpAttempts_Trace CHECK (LEN(LTRIM(RTRIM(TraceId))) > 0),
        CONSTRAINT CK_ErpAttempts_Active CHECK (Active IN (0,1)),
        CONSTRAINT CK_ErpAttempts_Modification CHECK ((ModifyBy IS NULL AND ModifyDate IS NULL) OR (ModifyBy IS NOT NULL AND ModifyDate IS NOT NULL)),
        CONSTRAINT FK_ErpAttempts_Job FOREIGN KEY (JobId) REFERENCES dbo.ErpJobs(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ErpAttempts_CreateBy FOREIGN KEY (CreateBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION,
        CONSTRAINT FK_ErpAttempts_ModifyBy FOREIGN KEY (ModifyBy) REFERENCES dbo.Users(Id) ON DELETE NO ACTION ON UPDATE NO ACTION
    );

    -- All three reservation keys must be complete. SQL Server filtered-index simple
    -- comparisons only; deliberately no NOT IN, computed columns, OR, or Active filter.
    CREATE UNIQUE INDEX UX_RentalRequests_ContractPeriod
      ON dbo.RentalRequests(ContractId, PeriodStartDate, PeriodEndDate)
      WHERE ContractId IS NOT NULL AND PeriodStartDate IS NOT NULL AND PeriodEndDate IS NOT NULL
        AND StatusId <> 5 AND StatusId <> 7;
    CREATE UNIQUE INDEX UX_ApprovalAssignments_ActiveRole
      ON dbo.ApprovalAssignments(RequesterId, ApprovalRoleId) WHERE Active = 1;
    ALTER TABLE dbo.RentalRequests ADD CONSTRAINT FK_RentalRequests_CurrentRevision
      FOREIGN KEY (Id, CurrentRevision) REFERENCES dbo.RequestRevisions(RequestId, RevisionNumber)
      ON DELETE NO ACTION ON UPDATE NO ACTION;
    CREATE INDEX IX_RentalRequests_Status ON dbo.RentalRequests(StatusId, CreateDate) INCLUDE (Amount, SyncStatus);
    CREATE INDEX IX_ApprovalSteps_Inbox ON dbo.ApprovalSteps(ApproverId, Status, Active);
    CREATE INDEX IX_ErpJobs_Queue ON dbo.ErpJobs(Status, NextAttemptDate, LockedUntilDate) INCLUDE (AttemptCount, MaxAttempts, Active);
    CREATE INDEX IX_Logs_Timeline ON dbo.Logs(RequestId, ActionDate, Id);
    CREATE INDEX IX_Users_CreateBy ON dbo.Users(CreateBy);
    CREATE INDEX IX_Users_ModifyBy ON dbo.Users(ModifyBy);
    CREATE INDEX IX_ApprovalAssignments_Requester ON dbo.ApprovalAssignments(RequesterId);
    CREATE INDEX IX_ApprovalAssignments_Approver ON dbo.ApprovalAssignments(ApproverId);
    CREATE INDEX IX_ApprovalAssignments_CreateBy ON dbo.ApprovalAssignments(CreateBy);
    CREATE INDEX IX_ApprovalAssignments_ModifyBy ON dbo.ApprovalAssignments(ModifyBy);
    CREATE INDEX IX_MasterVendors_CreateBy ON dbo.MasterVendors(CreateBy);
    CREATE INDEX IX_MasterVendors_ModifyBy ON dbo.MasterVendors(ModifyBy);
    CREATE INDEX IX_MasterContracts_Vendor ON dbo.MasterContracts(VendorId);
    CREATE INDEX IX_MasterContracts_CreateBy ON dbo.MasterContracts(CreateBy);
    CREATE INDEX IX_MasterContracts_ModifyBy ON dbo.MasterContracts(ModifyBy);
    CREATE INDEX IX_MasterCostCenters_CreateBy ON dbo.MasterCostCenters(CreateBy);
    CREATE INDEX IX_MasterCostCenters_ModifyBy ON dbo.MasterCostCenters(ModifyBy);
    CREATE INDEX IX_RentalRequests_Requester ON dbo.RentalRequests(RequesterId);
    CREATE INDEX IX_RentalRequests_CreateBy ON dbo.RentalRequests(CreateBy);
    CREATE INDEX IX_RentalRequests_ModifyBy ON dbo.RentalRequests(ModifyBy);
    CREATE INDEX IX_RentalRequests_ContractVendor ON dbo.RentalRequests(ContractId, VendorId);
    CREATE INDEX IX_RentalRequests_Vendor ON dbo.RentalRequests(VendorId);
    CREATE INDEX IX_RentalRequests_CostCenter ON dbo.RentalRequests(CostCenterId);
    CREATE INDEX IX_RequestRevisions_Request ON dbo.RequestRevisions(RequestId);
    CREATE INDEX IX_RequestRevisions_ContractVendor ON dbo.RequestRevisions(ContractId, VendorId);
    CREATE INDEX IX_RequestRevisions_Vendor ON dbo.RequestRevisions(VendorId);
    CREATE INDEX IX_RequestRevisions_CostCenter ON dbo.RequestRevisions(CostCenterId);
    CREATE INDEX IX_RequestRevisions_CreateBy ON dbo.RequestRevisions(CreateBy);
    CREATE INDEX IX_RequestRevisions_ModifyBy ON dbo.RequestRevisions(ModifyBy);
    CREATE INDEX IX_Attachments_Request ON dbo.Attachments(RequestId);
    CREATE INDEX IX_Attachments_CreateBy ON dbo.Attachments(CreateBy);
    CREATE INDEX IX_Attachments_ModifyBy ON dbo.Attachments(ModifyBy);
    CREATE INDEX IX_Attachments_RevisionRequest ON dbo.Attachments(RequestRevisionId, RequestId);
    CREATE INDEX IX_ApprovalSteps_Request ON dbo.ApprovalSteps(RequestId);
    CREATE INDEX IX_ApprovalSteps_Approver ON dbo.ApprovalSteps(ApproverId);
    CREATE INDEX IX_ApprovalSteps_CreateBy ON dbo.ApprovalSteps(CreateBy);
    CREATE INDEX IX_ApprovalSteps_ModifyBy ON dbo.ApprovalSteps(ModifyBy);
    CREATE INDEX IX_ApprovalSteps_RevisionRequest ON dbo.ApprovalSteps(RequestRevisionId, RequestId);
    CREATE INDEX IX_Logs_Request ON dbo.Logs(RequestId);
    CREATE INDEX IX_Logs_Actor ON dbo.Logs(ActorId);
    CREATE INDEX IX_Logs_CreateBy ON dbo.Logs(CreateBy);
    CREATE INDEX IX_Logs_ModifyBy ON dbo.Logs(ModifyBy);
    CREATE INDEX IX_Logs_RevisionRequest ON dbo.Logs(RequestRevisionId, RequestId);
    CREATE INDEX IX_ErpJobs_Request ON dbo.ErpJobs(RequestId);
    CREATE INDEX IX_ErpJobs_CreateBy ON dbo.ErpJobs(CreateBy);
    CREATE INDEX IX_ErpJobs_ModifyBy ON dbo.ErpJobs(ModifyBy);
    CREATE INDEX IX_ErpJobs_RevisionRequest ON dbo.ErpJobs(RequestRevisionId, RequestId);
    CREATE INDEX IX_ErpAttempts_Job ON dbo.ErpAttempts(JobId);
    CREATE INDEX IX_ErpAttempts_CreateBy ON dbo.ErpAttempts(CreateBy);
    CREATE INDEX IX_ErpAttempts_ModifyBy ON dbo.ErpAttempts(ModifyBy);
    CREATE INDEX IX_RentalRequests_CurrentRevision ON dbo.RentalRequests(Id, CurrentRevision);
    CREATE INDEX IX_MasterRoles_CreateBy ON dbo.MasterRoles(CreateBy);
    CREATE INDEX IX_MasterRoles_ModifyBy ON dbo.MasterRoles(ModifyBy);
    CREATE INDEX IX_MasterRentalTypes_CreateBy ON dbo.MasterRentalTypes(CreateBy);
    CREATE INDEX IX_MasterRentalTypes_ModifyBy ON dbo.MasterRentalTypes(ModifyBy);
    CREATE INDEX IX_Users_RoleId ON dbo.Users(RoleId);
    CREATE INDEX IX_MasterContracts_RentalTypeId ON dbo.MasterContracts(RentalTypeId);
    CREATE INDEX IX_ApprovalAssignments_ApprovalRoleId ON dbo.ApprovalAssignments(ApprovalRoleId);
    CREATE INDEX IX_ApprovalSteps_ApprovalRoleId ON dbo.ApprovalSteps(ApprovalRoleId);
    CREATE INDEX IX_MasterRequestStatuses_CreateBy ON dbo.MasterRequestStatuses(CreateBy);
    CREATE INDEX IX_MasterRequestStatuses_ModifyBy ON dbo.MasterRequestStatuses(ModifyBy);
    CREATE INDEX IX_Logs_FromStatusId ON dbo.Logs(FromStatusId);
    CREATE INDEX IX_Logs_ToStatusId ON dbo.Logs(ToStatusId);

    -- =========================================================================
    -- READY-TO-TEST SEED DATA (Users, ApprovalAssignments, Sample Requests)
    -- All human accounts below use Password: 1234
    -- =========================================================================
    DECLARE @DefaultPwdHash nvarchar(512) = N''AQAAAAIAAYagAAAAEAECAwQFBgcICQoLDA0ODxB3QwDNE2cHlLJMb2aJMYmH287PFhukSn9OLhiY0SB7SA=='';

    SET IDENTITY_INSERT dbo.Users ON;
    INSERT dbo.Users (Id, UserName, DisplayName, Email, RoleId, CanLogin, PasswordHash, ConcurrencyVersion, Active, CreateBy, CreateDate)
    VALUES (2, N''admin01'', N''ผู้ดูแลระบบหลัก'', N''admin01@bcg.local'', 5, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE()),
           (3, N''mgr01'',   N''สมศักดิ์ (ผู้จัดการฝ่าย)'', N''mgr01@bcg.local'', 2, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE()),
           (4, N''exec01'',  N''วิชัย (ผู้บริหารระดับสูง)'', N''exec01@bcg.local'', 3, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE()),
           (5, N''req01'',   N''สมชาย (พนักงานขออนุมัติ 1)'', N''req01@bcg.local'', 1, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE()),
           (6, N''req02'',   N''กานดา (พนักงานขออนุมัติ 2)'', N''req02@bcg.local'', 1, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE()),
           (7, N''gun01'',   N''กันตพงศ์ (พนักงานขออนุมัติ)'', N''gun01@bcg.local'', 1, 1, @DefaultPwdHash, 1, 1, 1, GETUTCDATE());
    SET IDENTITY_INSERT dbo.Users OFF;

    -- Map Approval Assignments (Step 1: Manager = mgr01 [Id=3], Step 2: Executive = exec01 [Id=4])
    INSERT dbo.ApprovalAssignments (RequesterId, ApproverId, ApprovalRoleId, ConcurrencyVersion, Active, CreateBy, CreateDate)
    VALUES (5, 3, 2, 1, 1, 2, GETUTCDATE()),
           (5, 4, 3, 1, 1, 2, GETUTCDATE()),
           (6, 3, 2, 1, 1, 2, GETUTCDATE()),
           (6, 4, 3, 1, 1, 2, GETUTCDATE()),
           (7, 3, 2, 1, 1, 2, GETUTCDATE()),
           (7, 4, 3, 1, 1, 2, GETUTCDATE()),
           (2, 3, 2, 1, 1, 2, GETUTCDATE()),
           (2, 4, 3, 1, 1, 2, GETUTCDATE());

    -- Sample Rental Requests across all key workflow states
    -- Step A: Insert as DRAFT (StatusId = 1, CurrentRevision = NULL) first to satisfy circular FK_RentalRequests_CurrentRevision
    SET IDENTITY_INSERT dbo.RentalRequests ON;
    INSERT dbo.RentalRequests
        (Id, RequesterId, ContractId, VendorId, CostCenterId, PeriodStartDate, PeriodEndDate, Amount, VatRate, Notes, StatusId, SyncStatus, CurrentRevision, SubmittedDate, ApprovedDate, ConcurrencyVersion, Active, CreateBy, CreateDate)
    VALUES
        -- #1: DRAFT (ฉบับร่าง)
        (1, 5, 1, 1, 1, ''2026-01-01T00:00:00'', ''2026-01-31T00:00:00'', 35000.00, 7.00, N''ค่าเช่าสำนักงานประจำเดือนมกราคม 2026 (ฉบับร่างรอส่งอนุมัติ)'', 1, N''NotQueued'', NULL, NULL, NULL, 1, 1, 5, GETUTCDATE()),
        -- #2: Will become PENDING_MANAGER
        (2, 5, 1, 1, 1, ''2026-02-01T00:00:00'', ''2026-02-28T00:00:00'', 45000.00, 7.00, N''ค่าเช่าสำนักงานประจำเดือนกุมภาพันธ์ 2026'', 1, N''NotQueued'', NULL, NULL, NULL, 1, 1, 5, GETUTCDATE()),
        -- #3: Will become PENDING_EXECUTIVE
        (3, 6, 2, 2, 8, ''2026-03-01T00:00:00'', ''2026-03-31T00:00:00'', 85000.00, 7.00, N''ค่าเช่าคลังสินค้าขนาดใหญ่ประจำเดือนมีนาคม 2026'', 1, N''NotQueued'', NULL, NULL, NULL, 1, 1, 6, GETUTCDATE()),
        -- #4: Will become APPROVED
        (4, 7, 3, 3, 10, ''2026-04-01T00:00:00'', ''2026-04-30T00:00:00'', 40000.00, 7.00, N''ค่าเช่าเครื่องจักรฝ่ายผลิตประจำเดือนเมษายน 2026'', 1, N''NotQueued'', NULL, NULL, NULL, 1, 1, 7, GETUTCDATE());
    SET IDENTITY_INSERT dbo.RentalRequests OFF;

    -- Step B: Insert Request Revisions for #2, #3, #4
    SET IDENTITY_INSERT dbo.RequestRevisions ON;
    INSERT dbo.RequestRevisions
        (Id, RequestId, RevisionNumber, ContractId, VendorId, CostCenterId, PeriodStartDate, PeriodEndDate, Amount, VatRate, SnapshotJson, SubmittedDate, Active, CreateBy, CreateDate)
    VALUES
        (1, 2, 1, 1, 1, 1, ''2026-02-01T00:00:00'', ''2026-02-28T00:00:00'', 45000.00, 7.00,
         N''{"RequestId":2,"RevisionNumber":1,"Contract":{"Id":1,"ContractNumber":"RENT-2026-001","RentalTypeCode":"OFFICE","RentalTypeName":"สำนักงาน"},"Vendor":{"Id":1,"VendorCode":"V001","Name":"บริษัท พื้นที่ดี จำกัด"},"CostCenter":{"Id":1,"CostCenterCode":"IT-001","Name":"ฝ่ายเทคโนโลยีสารสนเทศ"},"Amount":45000.00,"VatRate":7.00,"VatAmount":3150.00,"TotalAmount":48150.00,"Attachments":["20261118_100000001.pdf"]}'' ,
         GETUTCDATE(), 1, 5, GETUTCDATE()),
        (2, 3, 1, 2, 2, 8, ''2026-03-01T00:00:00'', ''2026-03-31T00:00:00'', 85000.00, 7.00,
         N''{"RequestId":3,"RevisionNumber":1,"Contract":{"Id":2,"ContractNumber":"RENT-2026-002","RentalTypeCode":"WAREHOUSE","RentalTypeName":"คลังสินค้า"},"Vendor":{"Id":2,"VendorCode":"V002","Name":"บริษัท คลังพร้อม จำกัด"},"CostCenter":{"Id":8,"CostCenterCode":"WH-008","Name":"ฝ่ายคลังสินค้า"},"Amount":85000.00,"VatRate":7.00,"VatAmount":5950.00,"TotalAmount":90950.00,"Attachments":["20261118_100000002.pdf"]}'' ,
         GETUTCDATE(), 1, 6, GETUTCDATE()),
        (3, 4, 1, 3, 3, 10, ''2026-04-01T00:00:00'', ''2026-04-30T00:00:00'', 40000.00, 7.00,
         N''{"RequestId":4,"RevisionNumber":1,"Contract":{"Id":3,"ContractNumber":"RENT-2026-003","RentalTypeCode":"EQUIPMENT","RentalTypeName":"อุปกรณ์และเครื่องจักร"},"Vendor":{"Id":3,"VendorCode":"V003","Name":"บริษัท อุปกรณ์พร้อมใช้ จำกัด"},"CostCenter":{"Id":10,"CostCenterCode":"PROD-010","Name":"ฝ่ายผลิต"},"Amount":40000.00,"VatRate":7.00,"VatAmount":2800.00,"TotalAmount":42800.00,"Attachments":["20261118_100000003.pdf"]}'' ,
         GETUTCDATE(), 1, 7, GETUTCDATE());
    SET IDENTITY_INSERT dbo.RequestRevisions OFF;

    -- Step C: Update RentalRequests #2, #3, #4 with CurrentRevision = 1 and their target StatusId
    UPDATE dbo.RentalRequests
    SET StatusId = 2, CurrentRevision = 1, SubmittedDate = GETUTCDATE(), ConcurrencyVersion = 2, ModifyBy = 5, ModifyDate = GETUTCDATE()
    WHERE Id = 2;

    UPDATE dbo.RentalRequests
    SET StatusId = 3, CurrentRevision = 1, SubmittedDate = GETUTCDATE(), ConcurrencyVersion = 3, ModifyBy = 3, ModifyDate = GETUTCDATE()
    WHERE Id = 3;

    UPDATE dbo.RentalRequests
    SET StatusId = 4, SyncStatus = N''Succeeded'', CurrentRevision = 1, SubmittedDate = GETUTCDATE(), ApprovedDate = GETUTCDATE(), ConcurrencyVersion = 3, ModifyBy = 3, ModifyDate = GETUTCDATE()
    WHERE Id = 4;

    -- Sample Attachments
    INSERT dbo.Attachments (RequestId, RequestRevisionId, FileName, Active, CreateBy, CreateDate)
    VALUES (1, NULL, N''20261118_090000001.pdf'', 1, 5, GETUTCDATE()),
           (2, NULL, N''20261118_100000001.pdf'', 1, 5, GETUTCDATE()),
           (2, 1,    N''20261118_100000001.pdf'', 1, 5, GETUTCDATE()),
           (3, NULL, N''20261118_100000002.pdf'', 1, 6, GETUTCDATE()),
           (3, 2,    N''20261118_100000002.pdf'', 1, 6, GETUTCDATE()),
           (4, NULL, N''20261118_100000003.pdf'', 1, 7, GETUTCDATE()),
           (4, 3,    N''20261118_100000003.pdf'', 1, 7, GETUTCDATE());

    -- Sample Approval Steps
    INSERT dbo.ApprovalSteps
        (RequestId, RequestRevisionId, StepNumber, ApprovalRoleId, ApproverId, Status, ActionDate, Reason, ConcurrencyVersion, Active, CreateBy, CreateDate)
    VALUES
        -- Request #2: Step 1 Pending Manager (mgr01)
        (2, 1, 1, 2, 3, N''Pending'', NULL, NULL, 1, 1, 5, GETUTCDATE()),
        -- Request #3: Step 1 Approved by Manager (mgr01), Step 2 Pending Executive (exec01)
        (3, 2, 1, 2, 3, N''Approved'', GETUTCDATE(), N''ตรวจสอบใบแจ้งหนี้และสัญญาคลังสินค้าถูกต้อง'', 2, 1, 6, GETUTCDATE()),
        (3, 2, 2, 3, 4, N''Pending'', NULL, NULL, 2, 1, 6, GETUTCDATE()),
        -- Request #4: Step 1 Approved by Manager (mgr01)
        (4, 3, 1, 2, 3, N''Approved'', GETUTCDATE(), N''อนุมัติจ่ายค่าเช่าเครื่องจักรตามสัญญา'', 2, 1, 7, GETUTCDATE());

    -- Sample ERP Job & Attempt for Request #4 (Succeeded)
    SET IDENTITY_INSERT dbo.ErpJobs ON;
    INSERT dbo.ErpJobs
        (Id, RequestId, RequestRevisionId, PayloadSnapshot, IdempotencyKey, Status, AttemptCount, MaxAttempts, NextAttemptDate, LockedUntilDate, LockToken, LastError, ExternalReference, CompletedDate, ConcurrencyVersion, Active, CreateBy, CreateDate)
    VALUES
        (1, 4, 3, N''{"RequestId":4,"RevisionId":3,"Amount":40000.00,"VatRate":7.00,"TotalAmount":42800.00}'', N''11111111-2222-3333-4444-555555555555'', N''Succeeded'', 1, 5, NULL, NULL, NULL, NULL, N''ERP-2026-0004-01'', GETUTCDATE(), 2, 1, 3, GETUTCDATE());
    SET IDENTITY_INSERT dbo.ErpJobs OFF;

    INSERT dbo.ErpAttempts
        (JobId, AttemptNumber, StartedDate, FinishedDate, Outcome, HttpStatus, Error, ResponseSummary, TraceId, Active, CreateBy, CreateDate)
    VALUES
        (1, 1, GETUTCDATE(), GETUTCDATE(), N''Succeeded'', 200, NULL, N''{"status":"SUCCESS","erpReference":"ERP-2026-0004-01"}'', N''seed-trace-erp-0004'', 1, 1, GETUTCDATE());

    -- Sample Audit Logs
    INSERT dbo.Logs
        (RequestId, ActorId, Action, FromStatusId, ToStatusId, RequestRevisionId, ActionDate, Reason, Details, TraceId, Active, CreateBy, CreateDate)
    VALUES
        (1, 5, N''Create'', NULL, 1, NULL, GETUTCDATE(), NULL, N''สร้างคำขอค่าเช่าฉบับร่าง'', N''seed-trace-req1-create'', 1, 5, GETUTCDATE()),
        (2, 5, N''Create'', NULL, 1, NULL, GETUTCDATE(), NULL, N''สร้างคำขอค่าเช่าฉบับร่าง'', N''seed-trace-req2-create'', 1, 5, GETUTCDATE()),
        (2, 5, N''Submit'', 1, 2, 1, GETUTCDATE(), NULL, N''ส่งคำขออนุมัติครั้งแรก (Revision #1)'', N''seed-trace-req2-submit'', 1, 5, GETUTCDATE()),
        (3, 6, N''Create'', NULL, 1, NULL, GETUTCDATE(), NULL, N''สร้างคำขอค่าเช่าฉบับร่าง'', N''seed-trace-req3-create'', 1, 6, GETUTCDATE()),
        (3, 6, N''Submit'', 1, 2, 2, GETUTCDATE(), NULL, N''ส่งคำขออนุมัติครั้งแรก (Revision #1)'', N''seed-trace-req3-submit'', 1, 6, GETUTCDATE()),
        (3, 3, N''Approve'', 2, 3, 2, GETUTCDATE(), N''ตรวจสอบใบแจ้งหนี้และสัญญาคลังสินค้าถูกต้อง'', N''ผู้จัดการอนุมัติขั้นที่ 1 สำเร็จ ส่งต่อผู้บริหาร (Revision #1)'', N''seed-trace-req3-approve1'', 1, 3, GETUTCDATE()),
        (4, 7, N''Create'', NULL, 1, NULL, GETUTCDATE(), NULL, N''สร้างคำขอค่าเช่าฉบับร่าง'', N''seed-trace-req4-create'', 1, 7, GETUTCDATE()),
        (4, 7, N''Submit'', 1, 2, 3, GETUTCDATE(), NULL, N''ส่งคำขออนุมัติครั้งแรก (Revision #1)'', N''seed-trace-req4-submit'', 1, 7, GETUTCDATE()),
        (4, 3, N''Approve'', 2, 4, 3, GETUTCDATE(), N''อนุมัติจ่ายค่าเช่าเครื่องจักรตามสัญญา'', N''อนุมัติคำขอค่าเช่าขั้นสุดท้ายสำเร็จ (Revision #1)'', N''seed-trace-req4-approve'', 1, 3, GETUTCDATE()),
        (4, 3, N''ErpQueued'', 4, 4, 3, GETUTCDATE(), NULL, N''สร้างงานส่งข้อมูลไปยัง ERP ในคิว'', N''seed-trace-req4-erpqueue'', 1, 3, GETUTCDATE()),
        (4, 1, N''ErpAttempt'', 4, 4, 3, GETUTCDATE(), NULL, N''เริ่มส่งข้อมูลไปยัง ERP ครั้งที่ 1/5'', N''seed-trace-req4-erpattempt'', 1, 1, GETUTCDATE()),
        (4, 1, N''ErpSucceeded'', 4, 4, 3, GETUTCDATE(), NULL, N''ส่งข้อมูลไปยัง ERP สำเร็จ (เลขอ้างอิง: ERP-2026-0004-01)'', N''seed-trace-req4-erpsuccess'', 1, 1, GETUTCDATE());

    -- Revalidate all constraints; no disabled/untrusted FK may survive installation.
    ALTER TABLE dbo.MasterRequestStatuses WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.MasterRoles WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.Users WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.MasterRentalTypes WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.ApprovalAssignments WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.MasterVendors WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.MasterContracts WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.MasterCostCenters WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.RentalRequests WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.RequestRevisions WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.Attachments WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.ApprovalSteps WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.Logs WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.ErpJobs WITH CHECK CHECK CONSTRAINT ALL;
    ALTER TABLE dbo.ErpAttempts WITH CHECK CHECK CONSTRAINT ALL;
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1)
        THROW 51013, N''Installation requires every foreign key enabled and trusted.'', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    -- SET IDENTITY_INSERT is session-scoped, not a transactional data change.
    -- If Users creation was rolled back it no longer exists; otherwise ensure OFF.
    IF OBJECT_ID(N''dbo.Users'', N''U'') IS NOT NULL
        SET IDENTITY_INSERT dbo.Users OFF;
    THROW;
END CATCH;
';
END TRY
BEGIN CATCH
    THROW;
END CATCH;
