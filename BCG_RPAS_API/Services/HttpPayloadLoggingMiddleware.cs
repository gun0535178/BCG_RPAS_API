using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BCG_RPAS_API.Services
{
    public sealed class HttpPayloadLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<HttpPayloadLoggingMiddleware> _logger;

        private static readonly Regex SensitiveFieldRegex = new(
            "(\"(?:password|passwordHash|accessToken)\"\\s*:\\s*\")([^\"]*)(\")",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RouteRequestIdRegex = new(
            @"^/api/(?:rental-requests|approvals)/(\d+)(?:/([a-z-]+))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public HttpPayloadLoggingMiddleware(
            RequestDelegate next,
            ILogger<HttpPayloadLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var pathValue = context.Request.Path.Value ?? string.Empty;
            var method = context.Request.Method;

            // 1. ข้ามคำขอที่ไม่ใช่ /api/... หรือคำขอที่เป็นการดึงข้อมูล/เปลี่ยนหน้าตาราง (Noise)
            if (!pathValue.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
                ShouldIgnoreRoutineEndpoint(method, pathValue))
            {
                await _next(context);
                return;
            }

            var inboundTime = DateTime.Now;
            var sw = Stopwatch.StartNew();

            // 2. อ่าน Inbound Request Payload
            string inboundPayload = string.Empty;
            if (context.Request.ContentLength > 0 &&
                context.Request.ContentType != null &&
                context.Request.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                inboundPayload = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            // 3. ดักจับ Outbound Response Payload
            var originalBodyStream = context.Response.Body;
            await using var responseBuffer = new MemoryStream();
            context.Response.Body = responseBuffer;

            try
            {
                await _next(context);
            }
            finally
            {
                sw.Stop();
                var outboundTime = DateTime.Now;

                responseBuffer.Position = 0;
                string outboundPayload = string.Empty;

                if (responseBuffer.Length > 0 &&
                    context.Response.ContentType != null &&
                    context.Response.ContentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    using var respReader = new StreamReader(
                        responseBuffer,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: false,
                        bufferSize: 4096,
                        leaveOpen: true);

                    outboundPayload = await respReader.ReadToEndAsync();
                }

                responseBuffer.Position = 0;
                await responseBuffer.CopyToAsync(originalBodyStream);
                context.Response.Body = originalBodyStream;

                var statusCode = context.Response.StatusCode;

                // วิเคราะห์หา CorrelationId และชื่อขั้นตอน (STEP) ของ Flow
                var (correlationId, stepName) = ResolveFlowMetadata(pathValue, inboundPayload, outboundPayload);

                var userName = context.User?.Identity?.IsAuthenticated == true
                    ? (context.User.FindFirst("name")?.Value ?? context.User.FindFirst("sub")?.Value ?? "Authenticated")
                    : ExtractUserFromLoginBody(inboundPayload) ?? "Anonymous";

                var maskedInbound = MaskSensitiveJson(TruncateIfTooLong(inboundPayload));
                var maskedOutbound = MaskSensitiveJson(TruncateIfTooLong(outboundPayload));

                var fullUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{pathValue}{context.Request.QueryString}";

                var sb = new StringBuilder();
                sb.AppendLine($"[CorrelationId: {correlationId}] [STEP: {stepName}]");
                sb.AppendLine($"  Actor   : {userName} | Endpoint: {method} {fullUrl}");
                sb.AppendLine($"  INBOUND  [{inboundTime:dd/MM/yyyy HH:mm:ss.fff}] : {(string.IsNullOrWhiteSpace(maskedInbound) ? "(empty)" : maskedInbound)}");
                sb.Append($"  OUTBOUND [{outboundTime:dd/MM/yyyy HH:mm:ss.fff}] : HTTP {statusCode} ({sw.ElapsedMilliseconds} ms) -> {(string.IsNullOrWhiteSpace(maskedOutbound) ? "(empty)" : maskedOutbound)}");

                if (statusCode >= 500)
                {
                    _logger.LogError("{FlowLog}", sb.ToString());
                }
                else if (statusCode >= 400)
                {
                    _logger.LogWarning("{FlowLog}", sb.ToString());
                }
                else
                {
                    _logger.LogInformation("{FlowLog}", sb.ToString());
                }
            }
        }

        /// <summary>
        /// กรองทิ้ง Endpoint ที่เป็นเพียงการโหลดหน้าจอ, ดึงข้อมูลตาราง DataTable, หรือรีเฟรชรายการ
        /// เก็บเฉพาะเหตุการณ์สำคัญที่เปลี่ยนสถานะของระบบ (State-Changing Events)
        /// </summary>
        private static bool ShouldIgnoreRoutineEndpoint(string method, string path)
        {
            // ไม่เก็บ GET ทุกชนิด (โหลดตาราง, โหลด Master, โหลด Inbox, ดูรายละเอียด)
            if (HttpMethods.IsGet(method))
            {
                return true;
            }

            // ไม่เก็บการค้นหา/เปลี่ยนหน้า DataTable
            if (path.Equals("/api/rental-requests/datatable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static (string CorrelationId, string StepName) ResolveFlowMetadata(
            string path,
            string inboundJson,
            string outboundJson)
        {
            // 1. ตรวจสอบ Route ของ /api/rental-requests/{id}/{action} หรือ /api/approvals/{id}/{action}
            var routeMatch = RouteRequestIdRegex.Match(path);
            if (routeMatch.Success && int.TryParse(routeMatch.Groups[1].Value, out var routeReqId) && routeReqId > 0)
            {
                var action = routeMatch.Groups[2].Value.ToLowerInvariant();
                var step = action switch
                {
                    "submit" => "SUBMIT_REQUEST (ส่งขออนุมัติ)",
                    "cancel" => "CANCEL_REQUEST (ยกเลิกคำขอ)",
                    "approve" => "APPROVE_REQUEST (อนุมัติคำขอ)",
                    "return" => "RETURN_FOR_REVISION (ส่งคืนให้แก้ไข)",
                    "reject" => "REJECT_REQUEST (ปฏิเสธคำขอ)",
                    _ => $"REQUEST_{action.ToUpperInvariant()}"
                };

                return ($"FLOW-REQ-{routeReqId:D4}", step);
            }

            // 2. กรณี POST /api/rental-requests (สร้างร่างใหม่ หรือ แก้ไขร่างเดิม)
            if (path.Equals("/api/rental-requests", StringComparison.OrdinalIgnoreCase))
            {
                int? reqIdFromIn = TryExtractIntProperty(inboundJson, "id");
                int? reqIdFromOut = TryExtractIntProperty(outboundJson, "id") ?? TryExtractIntProperty(outboundJson, "requestId");
                int finalReqId = (reqIdFromOut.HasValue && reqIdFromOut.Value > 0)
                    ? reqIdFromOut.Value
                    : (reqIdFromIn ?? 0);

                var isNew = !reqIdFromIn.HasValue || reqIdFromIn.Value == 0;
                var step = isNew
                    ? "CREATE_DRAFT (สร้างคำขอฉบับร่าง)"
                    : "UPDATE_DRAFT (แก้ไขข้อมูลคำขอ)";

                var corrId = finalReqId > 0
                    ? $"FLOW-REQ-{finalReqId:D4}"
                    : "FLOW-REQ-NEW";

                return (corrId, step);
            }

            // 3. กรณีสั่ง Retry ERP จากหน้าติดตามผล (/api/erp/monitoring/{jobId}/retry)
            if (path.StartsWith("/api/erp/monitoring", StringComparison.OrdinalIgnoreCase))
            {
                if (path.EndsWith("/retry", StringComparison.OrdinalIgnoreCase))
                {
                    return ("FLOW-ERP-MANUAL", "ERP_MANUAL_RETRY (กดสั่งส่งข้อมูล ERP ซ้ำ)");
                }
                if (path.EndsWith("/process-due", StringComparison.OrdinalIgnoreCase))
                {
                    return ("FLOW-ERP-MANUAL", "ERP_PROCESS_DUE (กดสั่งประมวลผลคิว ERP)");
                }
                if (path.EndsWith("/mode", StringComparison.OrdinalIgnoreCase))
                {
                    return ("FLOW-ERP-CONFIG", "ERP_TOGGLE_MOCK_MODE (สลับโหมดจำลอง ERP)");
                }
            }

            // 4. กรณีเข้าสู่ระบบ (/api/auth/login)
            if (path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
            {
                var user = ExtractUserFromLoginBody(inboundJson) ?? "UNKNOWN";
                return ($"FLOW-AUTH-{user.ToUpperInvariant()}", "USER_LOGIN (เข้าสู่ระบบ)");
            }

            // 5. กรณีจัดการข้อมูลหลัก / ผู้ใช้งาน / สายอนุมัติ
            if (path.StartsWith("/api/master/", StringComparison.OrdinalIgnoreCase))
            {
                var masterType = path.Substring("/api/master/".Length).ToUpperInvariant();
                return ($"FLOW-MASTER-{masterType}", $"UPSERT_MASTER_{masterType} (จัดการข้อมูลหลัก)");
            }

            if (path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase))
            {
                return ("FLOW-ADMIN-USERS", "UPSERT_USER (จัดการบัญชีผู้ใช้งาน)");
            }

            if (path.StartsWith("/api/approval-assignments", StringComparison.OrdinalIgnoreCase))
            {
                return ("FLOW-ADMIN-ASSIGNMENTS", "UPSERT_APPROVAL_ASSIGNMENT (ตั้งค่าสายอนุมัติ)");
            }

            return ("FLOW-SYSTEM", $"API_{path}");
        }

        private static int? TryExtractIntProperty(string json, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.Number &&
                            prop.Value.TryGetInt32(out var val))
                        {
                            return val;
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed JSON
            }

            return null;
        }

        private static string? ExtractUserFromLoginBody(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "userName", StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.String)
                        {
                            return prop.Value.GetString();
                        }
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }

        private static string MaskSensitiveJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return rawJson;
            }

            return SensitiveFieldRegex.Replace(rawJson, "$1***MASKED***$3");
        }

        private static string TruncateIfTooLong(string input, int maxLength = 4000)
        {
            if (string.IsNullOrEmpty(input) || input.Length <= maxLength)
            {
                return input;
            }

            return input.Substring(0, maxLength) + "...(truncated)";
        }
    }
}
