using InvoiceDownloaderWeb.Models;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InvoiceDownloaderWeb.Controllers
{
    public class InvoiceController : Controller
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<InvoiceController> _logger;
        private static int _downloadProgress = 0;
        private string clientid = "c01ebed1-c08f-451e-a5a3-26968bdc1587";
        private string clientsecret = "db262156-7627-4394-b98e-dca4e3a067c9";
        int count = 0;

        public InvoiceController(IHttpClientFactory httpClientFactory, ILogger<InvoiceController> logger)
        {
            _httpClient = httpClientFactory.CreateClient();
            _logger = logger;
        }

        public IActionResult Index()
        {
            var defaultStartDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            var defaultEndDate = DateTime.Now;

            ViewBag.DefaultStartDate = defaultStartDate.ToString("yyyy-MM-dd");
            ViewBag.DefaultEndDate = defaultEndDate.ToString("yyyy-MM-dd");

            return View();
        }

        [HttpGet]
        public IActionResult GetDownloadProgress()
        {
            return Json(new { progress = _downloadProgress });
        }

        [HttpPost]
        public async Task<IActionResult> DownloadByDate(
            DateTime submissionDateFrom,
            DateTime submissionDateTo,
            string status = "Valid",
            string documentType = "i",
            int pageSize = 100)
        {
            try
            {
                if (submissionDateFrom > submissionDateTo)
                {
                    return BadRequest(new
                    {
                        message = "تاريخ البداية لا يمكن أن يكون بعد تاريخ النهاية"
                    });
                }

                _logger.LogInformation(
                    $"Starting download for dates: {submissionDateFrom} to {submissionDateTo}");

                // 1️⃣ Get Access Token
                var accessToken = await GetAccessToken();
                if (string.IsNullOrEmpty(accessToken))
                    return Content("Failed to get access token.");

                // 2️⃣ Get document IDs
                var documentIds = await GetDocumentIdsByDate(
                    accessToken,
                    submissionDateFrom,
                    submissionDateTo,
                    status,
                    documentType,
                    pageSize
                );

                if (documentIds == null || documentIds.Count == 0)
                {
                    return NoContent(); // 204
                }

                // 3️⃣ Download PDFs
                var pdfFiles = await DownloadPdfs(documentIds, accessToken);

                if (pdfFiles == null || pdfFiles.Count == 0)
                    return Content("Failed to download any PDF files.");

                // 4️⃣ Return result
                if (pdfFiles.Count == 1)
                    return File(pdfFiles[0].Data, "application/pdf", pdfFiles[0].FileName);

                // Multiple files → ZIP
                var memoryStream = new MemoryStream();
                using (var archive = new System.IO.Compression.ZipArchive(
                    memoryStream,
                    System.IO.Compression.ZipArchiveMode.Create,
                    true))
                {
                    foreach (var file in pdfFiles)
                    {
                        var entry = archive.CreateEntry(file.FileName);
                        using var entryStream = entry.Open();
                        await entryStream.WriteAsync(file.Data, 0, file.Data.Length);
                    }
                }

                memoryStream.Position = 0;

                return File(
                    memoryStream,
                    "application/zip",
                    $"Invoices_{submissionDateFrom:yyyyMMdd}_to_{submissionDateTo:yyyyMMdd}.zip");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DownloadByDate");
                return Content($"Error: {ex.Message}");
            }
        }

        private async Task<string> GetAccessToken()
        {
            try
            {
                var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.eta.gov.eg/connect/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "client_id", clientid },
                        { "client_secret", clientsecret },
                        { "grant_type", "client_credentials" }
                    })
                };
                tokenRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var tokenResponse = await _httpClient.SendAsync(tokenRequest);

                if (!tokenResponse.IsSuccessStatusCode)
                {
                    var errorContent = await tokenResponse.Content.ReadAsStringAsync();
                    _logger.LogError($"Token request failed: {errorContent}");
                    return null;
                }

                var tokenData = await tokenResponse.Content.ReadFromJsonAsync<TokenResult>();
                return tokenData.access_token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting access token");
                return null;
            }
        }

        private async Task<List<DocumentIdsDto>> GetDocumentIdsByDate(
            string accessToken,
            DateTime submissionDateFrom,
            DateTime submissionDateTo,
            string status,
            string documentType,
            int pageSize)
        {
            var documents = new List<DocumentIdsDto>();
            int pageNo = 1;
            bool hasMorePages = true;

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            while (hasMorePages)
            {
                var apiUrl =
                    $"https://api.invoicing.eta.gov.eg/api/v1/documents/recent?" +
                    $"pageNo={pageNo}&pageSize={pageSize}&" +
                    $"submissionDateFrom={submissionDateFrom:yyyy-MM-dd}T00:00:00&" +
                    $"submissionDateTo={submissionDateTo:yyyy-MM-dd}T23:59:59&" +
                    $"status={status}&documentType={documentType}&" +
                    $"direction=Sent";

                var response = await _httpClient.GetAsync(apiUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<ApiResponse>(content);

                if (result?.result == null || result.result.Length == 0)
                    break;

                foreach (var doc in result.result)
                {
                    documents.Add(new DocumentIdsDto
                    {
                        uuid = doc.uuid,
                        internalId = doc.internalId
                    });
                }

                _logger.LogInformation(
                    $"Page {pageNo}: Got {result.result.Length} docs, " +
                    $"TotalPages: {result.metadata.totalPages}, " +
                    $"TotalCount: {result.metadata.totalCount}");

                // ✅ الشرط الصحيح باستخدام metadata
                if (pageNo >= result.metadata.totalPages)
                    hasMorePages = false;
                else
                    pageNo++;
            }

            return documents;
        }

        private async Task<List<(string FileName, byte[] Data)>> DownloadPdfs(
            List<DocumentIdsDto> documents,
            string accessToken)
        {
            _downloadProgress = 0;
            var pdfFiles = new List<(string FileName, byte[] Data)>();

            int totalDocs = documents.Count;
            int downloadedCount = 0;
            DateTime lastRequestTime = DateTime.Now.AddSeconds(-6);

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            foreach (var doc in documents)
            {
                try
                {
                    downloadedCount++;
                    _logger.LogInformation(
                        $"Downloading PDF {downloadedCount}/{totalDocs} - UUID: {doc.uuid}, InternalId: {doc.internalId}");

                    // Rate limit protection
                    var timeSinceLastRequest = DateTime.Now - lastRequestTime;
                    if (timeSinceLastRequest.TotalSeconds < 6)
                    {
                        var waitTime = (int)((6 - timeSinceLastRequest.TotalSeconds) * 1000);
                        await Task.Delay(waitTime);
                    }

                    var pdfUrl =
                        $"https://api.invoicing.eta.gov.eg/api/v1/documents/{doc.uuid}/pdf";

                    var pdfResponse = await _httpClient.GetAsync(pdfUrl);
                    lastRequestTime = DateTime.Now;

                    if (pdfResponse.IsSuccessStatusCode)
                    {
                        var pdfBytes = await pdfResponse.Content.ReadAsByteArrayAsync();
                        pdfFiles.Add(($"{doc.internalId}.pdf", pdfBytes));

                        _downloadProgress =
                            (int)((double)downloadedCount / totalDocs * 100);

                        _logger.LogInformation(
                            $"Downloaded: {doc.internalId}.pdf ({pdfBytes.Length} bytes)");
                    }
                    else if (pdfResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning($"Rate limit hit for {doc.uuid}, retrying...");
                        await Task.Delay(6000);

                        pdfResponse = await _httpClient.GetAsync(pdfUrl);
                        lastRequestTime = DateTime.Now;

                        if (pdfResponse.IsSuccessStatusCode)
                        {
                            var pdfBytes = await pdfResponse.Content.ReadAsByteArrayAsync();
                            pdfFiles.Add(($"{doc.internalId}.pdf", pdfBytes));

                            _downloadProgress =
                                (int)((double)downloadedCount / totalDocs * 100);
                        }
                    }
                    else
                    {
                        var errorContent = await pdfResponse.Content.ReadAsStringAsync();
                        _logger.LogError(
                            $"Failed to download {doc.uuid}: {pdfResponse.StatusCode} - {errorContent}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Exception downloading {doc.uuid}");
                }
            }

            _downloadProgress = 100;
            return pdfFiles;
        }
    }
}