namespace Api.Models
{
    public class ClientPaymentModel
    {
        // From clients table
        public string? ClientId { get; set; }
        
        // From payments table
        public int Id { get; set; }      // public payment reference
        public decimal Amount { get; set; }
    }
}
