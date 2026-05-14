namespace ApiGateway.Authentication;

/// <summary>
/// Request model for POST /auth/token (development-only endpoint).
/// </summary>
public class TokenRequest
{
    public string Username { get; set; } = string.Empty;
}

/// <summary>
/// Response model for POST /auth/token.
/// </summary>
public class TokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
}