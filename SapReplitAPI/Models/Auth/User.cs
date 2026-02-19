// ✅ Update User model to include Username
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SapReplitAPI.Models.Auth
{
    [Table("Users")]
    public class User
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        public string Username { get; set; } = string.Empty;

        public int SlpCode { get; set; }

        [Required]
        public string SlpName { get; set; } = string.Empty;

        [Required]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string Role { get; set; } = "Sales";

        public string? TwoFASecret { get; set; }
    }
}