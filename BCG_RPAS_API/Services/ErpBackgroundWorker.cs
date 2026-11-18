namespace BCG_RPAS_API.Services
{
    public class ErpBackgroundWorker : BackgroundService
    {
        private readonly ErpSyncService _erpSyncService;
        private readonly ILogger<ErpBackgroundWorker> _logger;

        public ErpBackgroundWorker(
            ErpSyncService erpSyncService,
            ILogger<ErpBackgroundWorker> logger)
        {
            _erpSyncService = erpSyncService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // รอให้ระบบเริ่มต้นเรียบร้อยก่อนเริ่มรอบแรก
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _erpSyncService.ProcessDueJobsAsync(actorUserId: null, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ErpBackgroundWorker while processing due ERP jobs");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
