using System.ComponentModel.DataAnnotations;

namespace Api.Models;


public class User
{
    public int Id { get; set; } // Auto-incremented primary key

    public string? Name { get; set; }

    [Required]
    [EmailAddress]
    public string Email { get; set; }

    [Required]
    [Phone]
    public string Mobile { get; set; } 

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
