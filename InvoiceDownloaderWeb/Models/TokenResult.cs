namespace InvoiceDownloaderWeb.Models
{

    public class TokenResult
    {
        public string access_token { get; set; }
        public string token_type { get; set; }
        public int expires_in { get; set; }
    }
    public class ApiResponse
    {
        public Document[] result { get; set; }
        public int currentPage { get; set; }
        public int totalPages { get; set; }
        public int pageSize { get; set; }
        public int totalCount { get; set; }
        public Metadata metadata { get; set; } // ✅ أضف ده
    }

    public class Document
    {
        public string uuid { get; set; }
        public string submissionUUID { get; set; }
        public string longId { get; set; }
        public string internalId { get; set; }
        public string typeName { get; set; }
        public string issuerId { get; set; }
        public string issuerName { get; set; }
        public string receiverId { get; set; }
        public string receiverName { get; set; }
        public DateTime dateTimeIssued { get; set; }
        public DateTime dateTimeReceived { get; set; }
        public decimal totalSales { get; set; }
        public decimal totalDiscount { get; set; }
        public decimal netAmount { get; set; }
        public decimal total { get; set; }
        public string status { get; set; }
    }


    public class DocumentIdsDto
    {
        public string uuid { get; set; }
        public string internalId { get; set; }
    }

  

    public class Metadata
    {
        public int totalPages { get; set; }
        public int totalCount { get; set; }
        public bool queryContainsCompleteResultSet { get; set; }
        public int remainingRecordsCount { get; set; }
    }
}
