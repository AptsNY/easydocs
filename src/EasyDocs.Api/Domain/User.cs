namespace EasyDocs.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
